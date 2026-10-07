using System.Diagnostics;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Live;

internal sealed record LiveOptions(string Url, bool Enabled, DecodeMode DecodeMode, LiveTransport Transport);
internal sealed record LiveStatus(LivePlaybackPhase Phase, DecodePath Path, bool Overload, string Message);

/// <summary>Owns live connection commands and presentation. Native ownership stays on its worker.</summary>
internal sealed partial class LiveSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncPulse _changed = new();
    private readonly AsyncPulse _wake = new();
    private readonly Queue<ReadyFrame> _ready = new();
    private readonly Queue<IDisposable> _retired = new();
    private readonly List<Binding> _retiredBindings = [];
    private readonly MasterClock _clock = new();
    private readonly PlaybackDiagnostics _diagnostics = new();
    private readonly Task _worker;
    private LiveOptions _options = new("", true, DecodeMode.Auto, LiveTransport.Tcp);
    private LiveStatus _status = new(LivePlaybackPhase.Idle, DecodePath.None, false, "No live source.");
    private Binding? _binding;
    private AudioDemand? _audioDemand;
    private AudioOutput? _audioOutput;
    private long _revision, _connectionRevision, _attempt, _epoch;
    private bool _reconnect, _disposed, _started;
    private double _lastPresented = double.NegativeInfinity;
    private string _description = "Live source";
    public PlaybackMetrics Metrics => _diagnostics.Metrics;
    public LiveStatus Status { get { lock (_gate) return _status; } }

    public LiveSession() => _worker = Task.Run(Worker);

    public void Update(LiveOptions options, bool reconnect)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var command = reconnect && !_reconnect;
            _reconnect = reconnect;
            if (options == _options && !command) return;
            _options = options;
            _connectionRevision++;
            _revision++;
            _attempt++;
            ClearOutputLocked();
            _status = new(options.Enabled && options.Url.Length > 0 ? LivePlaybackPhase.Connecting : LivePlaybackPhase.Idle,
                DecodePath.None, false, options.Enabled ? "Connecting live source." : "Disabled.");
        }
        Signal();
    }

    public void Attach(VideoPlaybackContext? context)
    {
        var binding = context is null ? null : new Binding(context);
        lock (_gate)
        {
            if (_disposed) { binding?.Dispose(); return; }
            if (_binding is not null) _retiredBindings.Add(_binding);
            _binding = binding;
            _revision++;
            _attempt++;
            ClearOutputLocked();
            if (binding is null) _clock.DetachVideo();
        }
        Signal();
    }

    public IResourceProvider<VideoFrame>? GrabVideo()
    {
        IResourceProvider<VideoFrame>? result = null;
        lock (_gate)
        {
            if (_disposed || !_options.Enabled || _binding is null) return null;
            var target = _clock.Observe(_binding.Context.FrameClock.Time.Seconds);
            ReadyFrame? selected = null;
            while (_ready.TryPeek(out var next) && next.Time <= target + .001)
            {
                _ready.Dequeue();
                if (next.Attempt != _attempt || next.Epoch != _epoch) { Retire(next.Frame); continue; }
                if (selected is not null) { Retire(selected.Frame); _diagnostics.FrameDropped(); }
                selected = next;
            }
            if (selected is not null)
            {
                _lastPresented = selected.Time;
                result = selected.Frame.CreateProvider();
                selected.Frame.Dispose();
                _description = selected.Frame.DecodeStatus;
                _status = new(LivePlaybackPhase.Playing, selected.Frame.DecodePath, false, _description);
            }
            var late = _started && double.IsFinite(_lastPresented) && target - _lastPresented > .5;
            _status = _status with { Overload = late || _diagnostics.ResourceBlocked,
                Message = _diagnostics.Describe(_description, null, default, _ready.Count, _audioOutput?.Buffer.Underruns ?? 0) };
        }
        Signal();
        return result;
    }

    public IResourceProvider<AudioFrame>? GrabAudio(int count, int rate, int channels, bool interleaved)
    {
        if (count <= 0 || rate <= 0) return null;
        AudioOutput? output;
        lock (_gate)
        {
            if (_disposed || !_options.Enabled) return null;
            var first = _audioDemand is null;
            if (_audioDemand is null || _audioDemand.Rate != rate || _audioDemand.Channels != channels || _audioDemand.Capacity < count)
                _audioDemand = new(rate, channels, Math.Max(count, rate / 4));
            if (first && _binding is null) _revision++;
            output = _audioOutput;
            if (output?.Demand != _audioDemand) output = null;
        }
        Signal();
        if (output is null) return null;
        var now = _clock.Position;
        var cursor = Interlocked.Read(ref output.NextTime);
        var target = cursor == long.MinValue ? now : TimeSpan.FromTicks(cursor).TotalSeconds;
        if (now - target > Math.Max(.05, 2d * count / rate)) target = now;
        if (target - now > .25) return null;
        try
        {
            var result = output.Buffer.ReadAt(count, interleaved, TimeSpan.FromSeconds(target));
            Interlocked.Exchange(ref output.NextTime, TimeSpan.FromSeconds(target + count / (double)rate).Ticks);
            return result;
        }
        catch (ResourceUnavailableException) { return null; }
    }

    private void Signal() { _changed.Pulse(); _wake.Pulse(); }
    private void Retire(IDisposable resource) => _retired.Enqueue(resource);
    private void ClearOutputLocked()
    {
        while (_ready.TryDequeue(out var frame)) Retire(frame.Frame);
        if (_audioOutput is not null) Retire(_audioOutput.Buffer);
        _audioOutput = null;
        _started = false;
        _lastPresented = double.NegativeInfinity;
    }
    private void DrainRetired(bool bindings = false)
    {
        List<IDisposable> resources = [];
        lock (_gate)
        {
            while (_retired.TryDequeue(out var resource)) resources.Add(resource);
            if (bindings) { resources.AddRange(_retiredBindings); _retiredBindings.Clear(); }
        }
        foreach (var resource in resources) resource.Dispose();
    }
    private void SetStatus(LivePlaybackPhase phase, string message, long revision)
    {
        lock (_gate)
        {
            if (_disposed || _revision != revision) return;
            _description = message;
            _status = _status with { Phase = phase, Message = message };
        }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        Signal();
        try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        lock (_gate)
        {
            ClearOutputLocked();
            if (_binding is not null) { _retiredBindings.Add(_binding); _binding = null; }
        }
        DrainRetired(bindings: true);
        _lifetime.Dispose();
    }

    private sealed record AudioDemand(int Rate, int Channels, int Capacity);
    private sealed record AudioOutput(AudioDemand Demand, AudioSampleBuffer Buffer)
    { public long NextTime = long.MinValue; }
    private sealed record ReadyFrame(long Attempt, long Epoch, double Time, DecodedVideoFrame Frame);
    internal sealed unsafe class Binding : IDisposable
    {
        private nint _device;
        public VideoPlaybackContext Context { get; }
        public nint Device => _device;
        public Binding(VideoPlaybackContext context)
            : this(context, context.GraphicsDeviceType == GraphicsDeviceType.Direct3D11 ? context.GraphicsDevice : 0) { }
        private Binding(VideoPlaybackContext context, nint device)
        {
            Context = context;
            _device = device;
            if (Device != 0) D3D11Interop.AddRef((void*)Device);
        }
        // Acquire under the session gate, before the attachment can be retired.
        public Binding Retain() => new(Context, Device);
        public void Dispose()
        {
            var device = Interlocked.Exchange(ref _device, 0);
            if (device != 0) D3D11Interop.Release((void*)device);
        }
    }
}
