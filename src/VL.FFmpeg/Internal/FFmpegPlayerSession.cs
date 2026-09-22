using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed class FFmpegPlayerSession : IVideoPlayer, IPlaybackOptionsSink
{
    private const int QueueCapacity = 2;
    private const double PresentationEpsilon = 0.001;

    private readonly object _syncRoot = new();
    private readonly VideoPlayerSource _source;
    private readonly VideoPlaybackContext _context;
    private readonly nint _graphicsDevice;
    private readonly GraphicsDeviceType _graphicsDeviceType;
    private readonly bool _usesLinearColorspace;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _requestSignal = new(0, 1);
    private readonly Channel<QueuedFrame> _frames = Channel.CreateBounded<QueuedFrame>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly Task _worker;
    private readonly List<CancellationTokenSource> _retiredRequestCancellations = [];
    private readonly PlaybackTimeline _timeline = new();

    private PlaybackOptions _options;
    private DecodeRequest _decodeRequest;
    private CancellationTokenSource _requestCancellation;
    private FFmpegMediaInfo? _mediaInfo;
    private Exception? _decodeFault;
    private TimeSpan _latestMediaTime;
    private DecodePath _decodePath;
    private string _decodeStatus = "Software BGRA8";
    private long _nextRequestGeneration;
    private bool _opening;
    private bool _endOfStream;
    private bool _hasPresentedFrame;
    private bool _disposed;

    public FFmpegPlayerSession(VideoPlayerSource source, VideoPlaybackContext context)
    {
        _source = source;
        _context = context;
        _graphicsDeviceType = context.GraphicsDeviceType;
        _graphicsDevice = _graphicsDeviceType == GraphicsDeviceType.Direct3D11
            ? context.GraphicsDevice
            : nint.Zero;
        _usesLinearColorspace = context.UsesLinearColorspace;
        _options = source.Options;
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var initialPosition = InitialPosition(_options);
        _decodeRequest = CreateRequestLocked(_options, initialPosition);
        _timeline.Reset(initialPosition.TotalSeconds);
        _worker = Task.Run(WorkerLoop);
        SignalWorker();
    }

    public IResourceProvider<VideoFrame>? GrabVideoFrame()
    {
        PlaybackStatus status;
        IResourceProvider<VideoFrame>? result;

        lock (_syncRoot)
        {
            if (_disposed)
                return null;

            var clockSeconds = _context.FrameClock.Time.Seconds;
            var targetTimeline = _timeline.Update(clockSeconds, _options.Play);
            var drainedFrames = 0;
            double? presentedTimelineSeconds = null;
            DecodedVideoFrame? selectedFrame = null;

            while (_frames.Reader.TryPeek(out var queued)
                   && (queued.RequestGeneration != _decodeRequest.Generation
                       || queued.TimelineSeconds <= targetTimeline + PresentationEpsilon))
            {
                if (!_frames.Reader.TryRead(out queued))
                    break;
                if (queued.RequestGeneration != _decodeRequest.Generation)
                {
                    queued.Frame.Dispose();
                    continue;
                }

                selectedFrame?.Dispose();
                selectedFrame = queued.Frame;
                presentedTimelineSeconds = queued.TimelineSeconds;
                _latestMediaTime = queued.MediaTime;
                drainedFrames++;
            }

            if (selectedFrame is not null)
            {
                _decodePath = selectedFrame.DecodePath;
                _decodeStatus = selectedFrame.DecodeStatus;
                result = selectedFrame.CreateProvider();
                selectedFrame.Dispose();
                _hasPresentedFrame = true;
            }
            else
            {
                result = null;
            }

            var health = _decodeRequest.Health.Observe(
                clockSeconds,
                targetTimeline,
                presentedTimelineSeconds,
                _frames.Reader.Count,
                FrameDuration(_mediaInfo),
                drainedFrames,
                active: _options.Play
                && _mediaInfo is not null
                && !_opening
                && !_endOfStream
                && _decodeFault is null
                && _hasPresentedFrame);
            status = BuildStatusLocked(drainedFrames > 1, health);
        }

        _source.PublishStatus(this, status);
        return result;
    }

    public void OptionsChanged(PlaybackOptions options)
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            var previousOptions = _options;
            var requiresRestart = !string.Equals(
                    previousOptions.Filename,
                    options.Filename,
                    StringComparison.Ordinal)
                || previousOptions.SeekRequestId != options.SeekRequestId
                || previousOptions.DecodeMode != options.DecodeMode;
            var restartEndedLoop = !previousOptions.Loop && options.Loop && _endOfStream;

            _options = options;
            if (requiresRestart || restartEndedLoop)
            {
                var filenameChanged = !string.Equals(
                    previousOptions.Filename,
                    options.Filename,
                    StringComparison.Ordinal);
                var seekChanged = previousOptions.SeekRequestId != options.SeekRequestId;
                var initialPosition = filenameChanged
                    ? TimeSpan.Zero
                    : seekChanged
                        ? TimeSpan.FromSeconds(options.SeekTime)
                        : requiresRestart
                            ? _latestMediaTime
                            : TimeSpan.Zero;
                ResetDecodeRequestLocked(options, initialPosition);
            }
        }

        SignalWorker();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            _requestCancellation.Cancel();
            _lifetimeCancellation.Cancel();
            _frames.Writer.TryComplete();
        }

        SignalWorker();

        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown. The native AVIO interrupt callback observes the
            // same token, so native I/O stops before resources are released.
        }
        finally
        {
            DrainQueuedFrames();
            _requestCancellation.Dispose();
            foreach (var cancellation in _retiredRequestCancellations)
                cancellation.Dispose();
            _requestSignal.Dispose();
            _lifetimeCancellation.Dispose();
            _source.SessionDisposed(this);
        }
    }

    private void WorkerLoop()
    {
        var processedGeneration = -1L;

        while (!_lifetimeCancellation.IsCancellationRequested)
        {
            DecodeRequest request;
            lock (_syncRoot)
                request = _decodeRequest;

            if (request.Generation == processedGeneration)
            {
                _requestSignal.Wait(_lifetimeCancellation.Token);
                continue;
            }

            processedGeneration = request.Generation;
            if (string.IsNullOrWhiteSpace(request.Filename))
            {
                PublishWorkerState(
                    opening: false,
                    endOfStream: false,
                    fault: null,
                    mediaInfo: null,
                    message: "Set Filename to open media with FFmpeg.");
                continue;
            }

            try
            {
                RunDecodeRequest(request);
            }
            catch (OperationCanceledException) when (
                request.Cancellation.IsCancellationRequested
                || _lifetimeCancellation.IsCancellationRequested)
            {
                // A new filename/seek generation or normal disposal superseded
                // this decoder. The outer loop will pick up the new request.
            }
            catch (Exception exception)
            {
                _context.Logger.LogError(
                    exception,
                    "FFmpeg playback failed for {Filename}",
                    request.Filename);
                PublishWorkerState(
                    opening: false,
                    endOfStream: false,
                    fault: exception,
                    mediaInfo: null,
                    message: exception.Message);
            }
        }
    }

    private void RunDecodeRequest(DecodeRequest request)
    {
        var cycleOffset = 0d;
        var initialPosition = request.InitialPosition;
        var effectiveMode = request.DecodeMode;
        string? fallbackReason = null;

        while (!request.Cancellation.IsCancellationRequested)
        {
            PublishWorkerState(
                opening: true,
                endOfStream: false,
                fault: null,
                mediaInfo: null,
                message: $"Opening {Path.GetFileName(request.Filename)}");

            var lastTimeline = cycleOffset + initialPosition.TotalSeconds;
            var lastMediaTime = initialPosition;
            FFmpegMediaInfo mediaInfo;
            try
            {
                using var decoder = new FFmpegVideoDecoder(
                    request.Filename,
                    initialPosition,
                    request.Cancellation,
                    decodeMode: effectiveMode,
                    graphicsDevice: _graphicsDevice,
                    graphicsDeviceType: _graphicsDeviceType,
                    usesLinearColorspace: _usesLinearColorspace);
                mediaInfo = decoder.MediaInfo;
                lock (_syncRoot)
                {
                    _decodeStatus = fallbackReason is null
                        ? decoder.DecodeStatus
                        : $"Software fallback; {fallbackReason}";
                    if (!decoder.HardwareConfigured)
                        _decodePath = DecodePath.Software;
                }
                PublishWorkerState(
                    opening: false,
                    endOfStream: false,
                    fault: null,
                    mediaInfo: mediaInfo,
                    message: Describe(mediaInfo, _decodeStatus));

                var producerStarted = Stopwatch.GetTimestamp();
                decoder.Decode(decodedFrame =>
                {
                    request.Cancellation.ThrowIfCancellationRequested();
                    request.Health.ObserveProducerDuration(
                        Stopwatch.GetElapsedTime(producerStarted));

                    if (fallbackReason is not null)
                        decodedFrame.DecodeStatus += $"; hardware fallback: {fallbackReason}";
                    var timeline = cycleOffset + decodedFrame.Timecode.TotalSeconds;
                    lastTimeline = Math.Max(lastTimeline, timeline);
                    lastMediaTime = decodedFrame.Timecode;
                    var queued = new QueuedFrame(
                        RequestGeneration: request.Generation,
                        TimelineSeconds: timeline,
                        MediaTime: decodedFrame.Timecode,
                        Frame: decodedFrame);
                    var written = WriteFrame(
                        queued,
                        request.Cancellation,
                        request.Health);
                    producerStarted = Stopwatch.GetTimestamp();
                    return written;
                });
            }
            catch (FFmpegHardwareException exception) when (
                request.DecodeMode == DecodeMode.Auto
                && effectiveMode != DecodeMode.Software)
            {
                fallbackReason = exception.Message;
                effectiveMode = DecodeMode.Software;
                initialPosition = lastMediaTime;
                lock (_syncRoot)
                {
                    _decodePath = DecodePath.Software;
                    _decodeStatus = $"Software fallback; {fallbackReason}";
                }
                PublishWorkerState(
                    opening: false,
                    endOfStream: false,
                    fault: null,
                    mediaInfo: null,
                    message: _decodeStatus);
                continue;
            }

            request.Cancellation.ThrowIfCancellationRequested();

            PlaybackOptions currentOptions;
            lock (_syncRoot)
                currentOptions = _options;

            if (!currentOptions.Loop
                || request.Generation != CurrentRequestGeneration())
            {
                PublishWorkerState(
                    opening: false,
                    endOfStream: true,
                    fault: null,
                    mediaInfo: mediaInfo,
                    message: Describe(mediaInfo, _decodeStatus));
                return;
            }

            var duration = mediaInfo.Duration.TotalSeconds;
            var frameStep = mediaInfo.FrameRate.N > 0
                ? mediaInfo.FrameRate.D / (double)mediaInfo.FrameRate.N
                : 0.001d;
            cycleOffset = duration > 0d
                ? cycleOffset + duration
                : lastTimeline + frameStep;
            initialPosition = TimeSpan.Zero;
        }
    }

    private bool WriteFrame(
        QueuedFrame frame,
        CancellationToken cancellationToken,
        PlaybackHealthTracker health)
    {
        var written = false;
        long? waitStarted = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_frames.Writer.TryWrite(frame))
                {
                    written = true;
                    return true;
                }

                waitStarted ??= Stopwatch.GetTimestamp();
                if (!_frames.Writer.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                    return false;
            }

            return false;
        }
        finally
        {
            if (waitStarted is { } started)
                health.ObserveQueueWaitDuration(Stopwatch.GetElapsedTime(started));
            if (!written)
                frame.Frame.Dispose();
        }
    }

    private PlaybackStatus BuildStatusLocked(
        bool playbackOverload,
        PlaybackHealthSnapshot health)
    {
        if (_decodeFault is not null)
        {
            return PlaybackStatus.Idle with
            {
                Phase = PlaybackPhase.Faulted,
                DecodePath = _decodePath,
                Message = _decodeFault.Message
            };
        }

        if (string.IsNullOrWhiteSpace(_options.Filename))
            return PlaybackStatus.BackendPending with { Message = "Set Filename to open media with FFmpeg." };

        if (_opening || _mediaInfo is null)
        {
            return PlaybackStatus.BackendPending with
            {
                Phase = _opening ? PlaybackPhase.Opening : PlaybackPhase.Buffering,
                DecodePath = _decodePath,
                Message = _opening ? "Opening media with FFmpeg." : "Waiting for decoded video frames."
            };
        }

        var noQueuedFrames = !_frames.Reader.TryPeek(out _);
        var ended = _endOfStream && noQueuedFrames;
        var phase = ended
            ? PlaybackPhase.Ended
            : _options.Play
                ? PlaybackPhase.Playing
                : PlaybackPhase.Paused;

        return new PlaybackStatus(
            Phase: phase,
            DecodePath: _decodePath,
            Position: _latestMediaTime.TotalSeconds,
            Duration: _mediaInfo.Duration.TotalSeconds,
            IsPlaying: _options.Play && !ended && _hasPresentedFrame,
            IsEnded: ended,
            PlaybackOverload: playbackOverload,
            Message: DescribeWithDiagnostics(_mediaInfo, health));
    }

    private void PublishWorkerState(
        bool opening,
        bool endOfStream,
        Exception? fault,
        FFmpegMediaInfo? mediaInfo,
        string message)
    {
        PlaybackStatus status;
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _opening = opening;
            _endOfStream = endOfStream;
            _decodeFault = fault;
            if (mediaInfo is not null || fault is not null)
                _mediaInfo = mediaInfo;

            status = fault is not null
                ? PlaybackStatus.Idle with
                {
                    Phase = PlaybackPhase.Faulted,
                    DecodePath = _decodePath,
                    Message = message
                }
                : PlaybackStatus.BackendPending with
                {
                    Phase = opening ? PlaybackPhase.Opening : PlaybackPhase.Buffering,
                    DecodePath = _decodePath,
                    Duration = mediaInfo?.Duration.TotalSeconds ?? _mediaInfo?.Duration.TotalSeconds ?? 0d,
                    Message = message
                };
        }

        _source.PublishStatus(this, status);
    }

    private void ResetDecodeRequestLocked(PlaybackOptions options, TimeSpan initialPosition)
    {
        _requestCancellation.Cancel();
        _retiredRequestCancellations.Add(_requestCancellation);
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _decodeRequest = CreateRequestLocked(options, initialPosition);

        DrainQueuedFrames();

        _mediaInfo = null;
        _decodeFault = null;
        _latestMediaTime = initialPosition;
        _decodePath = DecodePath.None;
        _decodeStatus = options.DecodeMode == DecodeMode.Hardware
            ? "Waiting for required D3D11VA decode."
            : "Waiting for decoder selection.";
        _timeline.Reset(initialPosition.TotalSeconds);
        _opening = false;
        _endOfStream = false;
        _hasPresentedFrame = false;
    }

    private DecodeRequest CreateRequestLocked(
        PlaybackOptions options,
        TimeSpan initialPosition)
        => new(
            Generation: ++_nextRequestGeneration,
            Filename: options.Filename,
            InitialPosition: initialPosition,
            DecodeMode: options.DecodeMode,
            Cancellation: _requestCancellation.Token,
            Health: new PlaybackHealthTracker());

    private long CurrentRequestGeneration()
    {
        lock (_syncRoot)
            return _decodeRequest.Generation;
    }

    private static TimeSpan InitialPosition(PlaybackOptions options)
        => options.SeekRequestId > 0
            ? TimeSpan.FromSeconds(options.SeekTime)
            : TimeSpan.Zero;

    private static string Describe(FFmpegMediaInfo mediaInfo, string decodeStatus)
        => $"{mediaInfo.VideoCodec}, {mediaInfo.Width}x{mediaInfo.Height}, {decodeStatus}.";

    private static double FrameDuration(FFmpegMediaInfo? mediaInfo)
        => mediaInfo?.FrameRate.N > 0
            ? mediaInfo.FrameRate.D / (double)mediaInfo.FrameRate.N
            : 0d;

    private string DescribeWithDiagnostics(
        FFmpegMediaInfo mediaInfo,
        PlaybackHealthSnapshot health)
        => $"{Describe(mediaInfo, _decodeStatus)} "
            + $"Buffer {health.QueueDepth}/{QueueCapacity}; "
            + $"empty {health.QueueEmptyEvents}; "
            + $"late {health.PresentationUnderruns}; "
            + $"dropped {health.DroppedFrames}; "
            + $"max empty/late "
            + $"{health.MaxQueueEmptyDuration.TotalMilliseconds:F1}/"
            + $"{health.MaxPresentationLateness.TotalMilliseconds:F1} ms; "
            + $"max producer/queue wait "
            + $"{health.MaxProducerDuration.TotalMilliseconds:F1}/"
            + $"{health.MaxQueueWaitDuration.TotalMilliseconds:F1} ms.";

    private void DrainQueuedFrames()
    {
        while (_frames.Reader.TryRead(out var queued))
            queued.Frame.Dispose();
    }

    private void SignalWorker()
    {
        try
        {
            if (_requestSignal.CurrentCount == 0)
                _requestSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // A late option notification raced with normal disposal.
        }
        catch (SemaphoreFullException)
        {
            // Another notification already woke the single worker.
        }
    }

    private sealed record DecodeRequest(
        long Generation,
        string Filename,
        TimeSpan InitialPosition,
        DecodeMode DecodeMode,
        CancellationToken Cancellation,
        PlaybackHealthTracker Health);

    private sealed record QueuedFrame(
        long RequestGeneration,
        double TimelineSeconds,
        TimeSpan MediaTime,
        DecodedVideoFrame Frame);
}

internal sealed class FFmpegPlayerSessionFactory : IFFmpegPlayerSessionFactory
{
    public static readonly FFmpegPlayerSessionFactory Instance = new();

    private FFmpegPlayerSessionFactory()
    {
    }

    public IVideoPlayer Create(VideoPlayerSource source, VideoPlaybackContext context)
        => new FFmpegPlayerSession(source, context);
}
