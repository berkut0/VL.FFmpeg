using System.Diagnostics;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

/// <summary>One media lifetime shared by the video attachment and lazy audio demand.</summary>
internal sealed partial class PlaybackSession : IDisposable
{
    // Protects commands and presentation state. Native codec/container calls belong to WorkerLoop;
    // cancellation callbacks and resource retirement must run outside this lock.
    private readonly object _gate = new();
    private readonly VideoPlayerSource _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncPulse _wake = new(); // Timeline, queue capacity or request changed.
    private readonly AsyncPulse _commands = new(); // Control worker has commands or retirement work.
    private readonly MasterClock _clock = new();
    private readonly Queue<ReadyFrame> _ready = new();
    private readonly Queue<IDisposable> _retired = new();
    // Device references can retire only after the previous pipeline has stopped using them.
    private readonly List<VideoBinding> _retiredBindings = [];
    private readonly Task _worker;
    private readonly Task _controlWorker;
    private PlaybackOptions _options;
    private VideoBinding? _binding;
    private AudioFormat? _audioFormat;
    private AudioFormat? _audioDemand;
    private AudioOutput? _audioOutput;
    private long _audioPositionTicks;
    private AudioSampleBuffer? _audioBuffer;
    private CancellationTokenSource? _active;
    private long _activeGeneration;
    private PlaybackHealthTracker _health = new();
    private long _generation;
    private double _requestedPosition;
    private double _duration;
    private double _frameDuration;
    private double _position;
    private double _presentedTimeline;
    private readonly RealtimeRecovery _recovery = new();
    private bool _opening;
    private bool _ended;
    private bool _presented;
    private bool _preview = true;
    private bool _audioReady;
    private bool _disposed;
    private Exception? _fault;
    private Exception? _audioFault;
    private DecodePath _path;
    private string _description = "Waiting for media.";
    private readonly PlaybackDiagnostics _diagnostics = new();
    public PlaybackMetrics Metrics => _diagnostics.Metrics;
    private string? _recoveryMessage;
    private bool _recovering;

    public PlaybackSession(VideoPlayerSource source)
    {
        _source = source;
        _options = source.Options;
        _requestedPosition = _options.SeekRequestId > 0 ? _options.SeekTime : 0;
        _clock.Reset(_requestedPosition, _options.Play, waitForReady: true);
        _worker = Task.Run(WorkerLoop);
        _controlWorker = Task.Run(ControlLoop);
    }
    public Exception? AudioFault => Volatile.Read(ref _audioFault);

    public void Attach(VideoPlaybackContext context)
    {
        var binding = new VideoBinding(context);
        lock (_gate)
        {
            if (_disposed) { binding.Dispose(); return; }
            if (_binding is not null) _retiredBindings.Add(_binding);
            _binding = binding;
            _commands.Pulse();
            RestartLocked(_clock.Position);
        }
    }
    public void Detach()
    {
        lock (_gate)
        {
            if (_binding is null) return;
            _retiredBindings.Add(_binding);
            _binding = null;
            _commands.Pulse();
            _clock.DetachVideo();
            RestartLocked(_clock.Position);
        }
    }

    public void OptionsChanged(PlaybackOptions options)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var previous = _options;
            _options = options;
            var fileChanged = previous.Filename != options.Filename;
            var seek = previous.SeekRequestId != options.SeekRequestId;
            _clock.SetPlay(options.Play);
            if (fileChanged || seek)
            {
                var position = fileChanged ? 0 : options.SeekTime;
                if (fileChanged)
                {
                    _position = 0; _duration = 0; _path = DecodePath.None;
                    _description = string.IsNullOrWhiteSpace(options.Filename) ? "Idle" : "Opening media";
                    _recoveryMessage = null;
                    Interlocked.Exchange(ref _audioPositionTicks, 0);
                }
                _clock.Reset(position, options.Play, waitForReady: fileChanged);
                if (!fileChanged && _binding is not null) _clock.Observe(_binding.Context.FrameClock.Time.Seconds);
                RestartLocked(position);
            }
            else if (previous.DecodeMode != options.DecodeMode)
                RestartLocked(_clock.Position);
            else if (!previous.Loop && options.Loop && _ended)
            {
                _clock.Reset(0, options.Play, waitForReady: false);
                RestartLocked(0);
            }
        }
        _wake.Pulse();
    }

    public IResourceProvider<VideoFrame>? GrabVideoFrame()
    {
        IResourceProvider<VideoFrame>? result = null;
        PlaybackStatus status;
        lock (_gate)
        {
            if (_disposed || _binding is null) return null;
            var seconds = _binding.Context.FrameClock.Time.Seconds;
            var target = _clock.Observe(seconds);
            ReadyFrame? selected = null;
            var drained = 0;
            var forcePreview = _preview && !_options.Play;
            // Pause freezes the displayed image, not just the clock. Late producer results
            // may still arrive below that clock; only an explicit preview may replace it.
            while ((_options.Play || forcePreview) && _ready.TryPeek(out var frame)
                && (forcePreview || frame.Timeline <= target + VideoSchedulingPolicy.TimestampTolerance))
            {
                _ready.Dequeue();
                if (frame.Generation != _generation) { RetireLocked(frame.Frame); continue; }
                if (selected is not null) RetireLocked(selected.Frame);
                selected = frame;
                drained++;
                if (_preview) { _preview = false; if (forcePreview) break; }
            }
            if (selected is not null)
            {
                _position = selected.Frame.Timecode.TotalSeconds;
                _presentedTimeline = selected.Timeline;
                _path = selected.Frame.DecodePath;
                _description = selected.Frame.DecodeStatus;
                result = selected.Frame.CreateProvider();
                selected.Frame.Dispose();
                _presented = true;
                if (_recovering && target - selected.Timeline <= VideoSchedulingPolicy.BufferWindow + _frameDuration)
                {
                    _recovering = false;
                    _recoveryMessage = null;
                }
            }
            var health = _health.Observe(seconds, target, selected?.Timeline, _ready.Count,
                _frameDuration, drained, _options.Play && _presented && !_opening && _fault is null && !(_ended && target >= _duration));
            status = StatusLocked(health, target, drained > 1);
            if (_recovery.ShouldSeek(seconds, target - _presentedTimeline, _options.Play && _presented && !_ended))
            {
                _recoveryMessage = "Recovering to realtime.";
                RestartLocked(target, recovery: true);
            }
        }
        _wake.Pulse();
        _source.PublishPlaybackStatus(this, status);
        return result;
    }

    public IResourceProvider<AudioFrame>? GrabAudio(int samples, int rate, int channels, bool interleaved)
    {
        if (samples <= 0 || rate <= 0 || channels < 0 || Volatile.Read(ref _disposed)) return null;
        var options = Volatile.Read(ref _options);
        if (!options.Play || string.IsNullOrWhiteSpace(options.Filename)) return null;
        var demand = Volatile.Read(ref _audioDemand);
        if (demand is null || demand.Rate != rate || demand.Channels != channels || demand.Capacity < samples)
        {
            demand = new AudioFormat(rate, channels, Math.Max(rate / 4, samples));
            Volatile.Write(ref _audioDemand, demand);
            _commands.Pulse();
        }
        var output = Volatile.Read(ref _audioOutput);
        if (output is null || !ReferenceEquals(output.Format, demand)) return null;
        var now = _clock.Position;
        var cursor = Interlocked.Read(ref output.NextTime);
        var target = cursor == long.MinValue ? now : TimeSpan.FromTicks(cursor).TotalSeconds;
        // Preserve sample continuity through callback jitter; correct sustained drift against execution time.
        if (now - target > Math.Max(.050, 2d * samples / rate)) target = now;
        if (target - now > .250) return null;
        if (Volatile.Read(ref _ended) && !options.Loop && _duration > 0 && target >= _duration) return null;
        var position = TimeSpan.FromSeconds(target);
        IResourceProvider<AudioFrame> result;
        try { result = output.Buffer.ReadAt(samples, interleaved, position); }
        catch (ResourceUnavailableException) { return null; }
        Interlocked.Exchange(ref output.NextTime, (position + TimeSpan.FromSeconds(samples / (double)rate)).Ticks);
        Interlocked.Exchange(ref _audioPositionTicks, position.Ticks);
        return result;
    }

    private async Task ControlLoop()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var changed = _commands.Next;
            bool audioOnly;
            long generation;
            CancellationTokenSource? cancel;
            lock (_gate)
            {
                var demand = Volatile.Read(ref _audioDemand);
                if (demand is not null && !ReferenceEquals(_audioFormat, demand))
                {
                    _audioFormat = demand;
                    RestartLocked(_clock.Position);
                }
                audioOnly = _binding is null && _audioFormat is not null;
                generation = _generation;
                cancel = _activeGeneration != _generation ? _active : null;
            }
            Cancel(cancel);
            DrainRetired();
            if (audioOnly)
            {
                PublishWorkerStatus(generation);
                await PlaybackWait.ForChange(changed, TimeSpan.FromMilliseconds(250), _lifetime.Token).ConfigureAwait(false);
            }
            else await changed.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private void RestartLocked(double position, bool recovery = false)
    {
        _generation++;
        if (_recovering && !recovery) _recoveryMessage = null;
        _recovering = recovery;
        _requestedPosition = Math.Max(0, position);
        while (_ready.TryDequeue(out var frame)) RetireLocked(frame.Frame);
        Volatile.Write(ref _audioOutput, null);
        if (_audioBuffer is not null) RetireLocked(_audioBuffer);
        _audioBuffer = null;
        _audioReady = false;
        _preview = !recovery;
        _ended = false;
        // The consumer still owns the last image during automatic catch-up.
        // Clearing this flag disables further recovery and can latch Buffering forever.
        if (!recovery) _presented = false;
        _recovery.ResetObservation();
        _fault = null;
        _audioFault = null;
        _diagnostics.ResourceAvailable();
        if (!recovery) _health = new();
        _commands.Pulse();
        _wake.Pulse();
    }

    private PlaybackStatus StatusLocked(PlaybackHealthSnapshot health, double target, bool dropped)
    {
        var ended = _ended && _ready.Count == 0 && (_duration <= 0 || target >= _duration);
        var active = _binding is null ? _audioReady : _presented;
        var phase = _fault is not null ? PlaybackPhase.Faulted
            : string.IsNullOrWhiteSpace(_options.Filename) ? PlaybackPhase.Idle
            : _opening ? PlaybackPhase.Opening
            : ended ? PlaybackPhase.Ended
            : !_options.Play ? PlaybackPhase.Paused
            : !active ? PlaybackPhase.Buffering : PlaybackPhase.Playing;
        var message = _diagnostics.Describe(_description, _recoveryMessage, health,
            _ready.Count, _audioBuffer?.Underruns ?? 0);
        return new(phase, _path, _position, _duration, _options.Play && active && !ended,
            ended, dropped || health.IsPresentationLate || _diagnostics.ResourceBlocked,
            _fault?.Message ?? message);
    }

    private void RetireLocked(IDisposable resource)
    {
        _retired.Enqueue(resource);
        _commands.Pulse();
    }

    private void DrainRetired()
    {
        while (true)
        {
            IDisposable resource;
            lock (_gate) { if (!_retired.TryDequeue(out resource!)) return; }
            resource.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        // The worker can finish/dispose a generation between snapshot and notification.
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
        }
        // Never run cancellation callbacks or native resource releases under the presentation lock.
        _lifetime.Cancel();
        Cancel(active);
        _wake.Pulse();
        _commands.Pulse();
        try { Task.WhenAll(_worker, _controlWorker).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            while (_ready.TryDequeue(out var frame)) RetireLocked(frame.Frame);
            if (_audioBuffer is not null) RetireLocked(_audioBuffer);
            if (_binding is not null) RetireLocked(_binding);
            foreach (var binding in _retiredBindings) RetireLocked(binding);
            _retiredBindings.Clear();
        }
        DrainRetired();
        _lifetime.Dispose();
    }

    private sealed record AudioFormat(int Rate, int Channels, int Capacity);
    private sealed record AudioOutput(AudioFormat Format, AudioSampleBuffer Buffer)
    { public long NextTime = long.MinValue; }
    private sealed record Request(long Generation, PlaybackOptions Options, double Position, VideoBinding? Video, AudioFormat? Audio, bool Recovery);
    private sealed record ReadyFrame(long Generation, double Timeline, DecodedVideoFrame Frame);

    private sealed unsafe class VideoBinding : IDisposable
    {
        public VideoPlaybackContext Context { get; }
        public nint Device { get; }
        public VideoBinding(VideoPlaybackContext context)
        {
            Context = context;
            Device = context.GraphicsDeviceType == GraphicsDeviceType.Direct3D11 ? context.GraphicsDevice : 0;
            if (Device != 0) Interop.D3D11Interop.AddRef((void*)Device);
        }
        public void Dispose() { if (Device != 0) Interop.D3D11Interop.Release((void*)Device); }
    }
}
