using System.Diagnostics;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

/// <summary>One media lifetime shared by the video attachment and lazy audio demand.</summary>
internal sealed partial class PlaybackSession : IPlaybackOptionsSink, IDisposable
{
    private readonly object _gate = new();
    private readonly VideoPlayerSource _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncPulse _wake = new();
    private readonly AsyncPulse _commands = new();
    private readonly MasterClock _clock = new();
    private readonly Queue<ReadyFrame> _ready = new();
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
    private PlaybackHealthTracker _health = new();
    private long _generation;
    private double _requestedPosition;
    private double _duration;
    private double _frameDuration;
    private double _position;
    private double _presentedTimeline;
    private long _lastPresentedStamp;
    private double? _behindSince;
    private double _lastRecovery = double.NegativeInfinity;
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
    private string _diagnosticText = "";
    private string _statusMessage = "";
    private string? _statusDescription;
    private string? _statusRecovery;
    private long _lastDiagnosticStamp;
    private long _droppedBeforeConvert;
    private long _resourceWaits;
    private int _resourceBlocked;
    private long _containerOpens;
    private long _seeks;
    private long _conversions;
    private long _starvationConversions;
    private long _audioDiscontinuities;
    public PlaybackMetrics Metrics => new(Interlocked.Read(ref _containerOpens), Interlocked.Read(ref _seeks),
        Interlocked.Read(ref _conversions), Interlocked.Read(ref _droppedBeforeConvert), Interlocked.Read(ref _resourceWaits));
    private long _ioTicks;
    private long _decodeTicks;
    private long _convertTicks;
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
            while (_ready.TryPeek(out var frame) && (forcePreview || frame.Timeline <= target + .001))
            {
                _ready.Dequeue();
                if (frame.Generation != _generation) { frame.Frame.Dispose(); continue; }
                selected?.Frame.Dispose();
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
                _lastPresentedStamp = Stopwatch.GetTimestamp();
                if (_recovering && target - selected.Timeline <= VideoFrameDeadline.Window + _frameDuration)
                {
                    _recovering = false;
                    _recoveryMessage = null;
                }
            }
            var health = _health.Observe(seconds, target, selected?.Timeline, _ready.Count,
                _frameDuration, drained, _options.Play && _presented && !_opening && _fault is null && !(_ended && target >= _duration));
            status = StatusLocked(health, target, drained > 1);
            if (_options.Play && _presented && !_ended && target - _presentedTimeline > 1)
            {
                _behindSince ??= seconds;
                if (seconds - _behindSince >= .5 && seconds - _lastRecovery >= 2)
                {
                    _lastRecovery = seconds;
                    _behindSince = null;
                    _recoveryMessage = "Recovering to realtime.";
                    RestartLocked(target, recovery: true);
                }
            }
            else _behindSince = null;
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
            }
            if (audioOnly)
            {
                PublishWorkerStatus(generation);
                await Task.WhenAny(changed, Task.Delay(250, _lifetime.Token)).WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            else await changed.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private void RestartLocked(double position, bool recovery = false)
    {
        _generation++;
        _recovering = recovery;
        _requestedPosition = Math.Max(0, position);
        _active?.Cancel();
        while (_ready.TryDequeue(out var frame)) frame.Frame.Dispose();
        Volatile.Write(ref _audioOutput, null);
        _audioBuffer?.Dispose();
        _audioBuffer = null;
        _audioReady = false;
        _preview = !recovery;
        _ended = false;
        // The consumer still owns the last image during automatic catch-up.
        // Clearing this flag disables further recovery and can latch Buffering forever.
        if (!recovery) _presented = false;
        _behindSince = null;
        _fault = null;
        _audioFault = null;
        Volatile.Write(ref _resourceBlocked, 0);
        if (!recovery) _health = new();
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
        var stamp = Stopwatch.GetTimestamp();
        if (_lastDiagnosticStamp == 0 || Stopwatch.GetElapsedTime(_lastDiagnosticStamp).TotalMilliseconds >= 250)
        {
            _lastDiagnosticStamp = stamp;
            var metrics = Metrics;
            _diagnosticText = $"Buffer {_ready.Count}/6; empty {health.QueueEmptyEvents}; late {health.PresentationUnderruns}; "
                + $"dropped {health.DroppedFrames + Interlocked.Read(ref _droppedBeforeConvert)}; "
                + $"max empty/late {health.MaxQueueEmptyDuration.TotalMilliseconds:F1}/{health.MaxPresentationLateness.TotalMilliseconds:F1} ms; "
                + $"max producer/queue wait {health.MaxProducerDuration.TotalMilliseconds:F1}/{health.MaxQueueWaitDuration.TotalMilliseconds:F1} ms; "
                + $"I/O/decode/convert {Milliseconds(_ioTicks):F1}/{Milliseconds(_decodeTicks):F1}/{Milliseconds(_convertTicks):F1} ms; "
                + $"resource waits {Interlocked.Read(ref _resourceWaits)}; audio underruns {_audioBuffer?.Underruns ?? 0}; open/seek {metrics.ContainerOpens}/{metrics.Seeks}; converted {metrics.Conversions}; progress frames {Interlocked.Read(ref _starvationConversions)}; audio gaps {Interlocked.Read(ref _audioDiscontinuities)}.";
        }
        if (_lastDiagnosticStamp == stamp || _statusDescription != _description || _statusRecovery != _recoveryMessage)
        {
            _statusDescription = _description;
            _statusRecovery = _recoveryMessage;
            _statusMessage = $"{_description}. {_diagnosticText} {_recoveryMessage}";
        }
        return new(phase, _path, _position, _duration, _options.Play && active && !ended,
            ended, dropped || health.IsPresentationLate || Volatile.Read(ref _resourceBlocked) != 0,
            _fault?.Message ?? _statusMessage);
    }
    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static void Maximum(ref long location, long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref location); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref location, value, previous) != previous);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _active?.Cancel();
        }
        _wake.Pulse();
        _commands.Pulse();
        try { Task.WhenAll(_worker, _controlWorker).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            while (_ready.TryDequeue(out var frame)) frame.Frame.Dispose();
            _audioBuffer?.Dispose();
            _binding?.Dispose();
            foreach (var binding in _retiredBindings) binding.Dispose();
            _retiredBindings.Clear();
        }
        _lifetime.Dispose();
    }

    private sealed record AudioFormat(int Rate, int Channels, int Capacity);
    private sealed record AudioOutput(AudioFormat Format, AudioSampleBuffer Buffer)
    { public long NextTime = long.MinValue; }
    private sealed record Request(long Generation, PlaybackOptions Options, double Position, VideoBinding? Video, AudioFormat? Audio, bool Recovery);
    private sealed record ReadyFrame(long Generation, long Cycle, double Timeline, DecodedVideoFrame Frame);

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

internal readonly record struct PlaybackMetrics(long ContainerOpens, long Seeks, long Conversions, long DroppedBeforeConversion, long ResourceWaits);
