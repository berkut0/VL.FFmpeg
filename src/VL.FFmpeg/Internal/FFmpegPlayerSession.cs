using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed class FFmpegPlayerSession : IVideoPlayer, IPlaybackOptionsSink
{
    private const int QueueCapacity = 4;
    private const double PresentationEpsilon = 0.001;

    private readonly object _syncRoot = new();
    private readonly FFmpegVideoPlayer _source;
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

    private PlaybackOptions _options;
    private DecodeRequest _decodeRequest;
    private CancellationTokenSource _requestCancellation;
    private FFmpegMediaInfo? _mediaInfo;
    private Exception? _decodeFault;
    private TimeSpan _latestMediaTime;
    private FFmpegDecodePath _decodePath;
    private string _decodeStatus = "Software BGRA8";
    private long _nextRequestGeneration;
    private double _timelineSeconds;
    private double _anchorTimelineSeconds;
    private double _anchorClockSeconds;
    private bool _clockNeedsReset = true;
    private bool _lastPlay;
    private bool _opening;
    private bool _endOfStream;
    private bool _hasPresentedFrame;
    private bool _disposed;

    public FFmpegPlayerSession(FFmpegVideoPlayer source, VideoPlaybackContext context)
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
        _decodeRequest = CreateRequestLocked(_options, InitialPosition(_options));
        _lastPlay = _options.Play;
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
            ApplyClockStateLocked(clockSeconds);
            var targetTimeline = CurrentTimelineLocked(clockSeconds);
            var drainedFrames = 0;
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
                _latestMediaTime = queued.MediaTime;
                drainedFrames++;
            }

            _timelineSeconds = targetTimeline;
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
            status = BuildStatusLocked(drainedFrames > 1);
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
                        : $"Software BGRA8 fallback; {fallbackReason}";
                    if (!decoder.HardwareConfigured)
                        _decodePath = FFmpegDecodePath.Software;
                }
                PublishWorkerState(
                    opening: false,
                    endOfStream: false,
                    fault: null,
                    mediaInfo: mediaInfo,
                    message: Describe(mediaInfo, _decodeStatus));

                decoder.Decode(decodedFrame =>
                {
                    request.Cancellation.ThrowIfCancellationRequested();

                    if (fallbackReason is not null)
                        decodedFrame.DecodeStatus = $"Software BGRA8 fallback; {fallbackReason}";
                    var timeline = cycleOffset + decodedFrame.Timecode.TotalSeconds;
                    lastTimeline = Math.Max(lastTimeline, timeline);
                    lastMediaTime = decodedFrame.Timecode;
                    var queued = new QueuedFrame(
                        RequestGeneration: request.Generation,
                        TimelineSeconds: timeline,
                        MediaTime: decodedFrame.Timecode,
                        Frame: decodedFrame);
                    return WriteFrame(queued, request.Cancellation);
                });
            }
            catch (FFmpegHardwareException exception) when (
                request.DecodeMode == FFmpegDecodeMode.Auto
                && effectiveMode != FFmpegDecodeMode.Software)
            {
                fallbackReason = exception.Message;
                effectiveMode = FFmpegDecodeMode.Software;
                initialPosition = lastMediaTime;
                lock (_syncRoot)
                {
                    _decodePath = FFmpegDecodePath.Software;
                    _decodeStatus = $"Software BGRA8 fallback; {fallbackReason}";
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

    private bool WriteFrame(QueuedFrame frame, CancellationToken cancellationToken)
    {
        var written = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_frames.Writer.TryWrite(frame))
                {
                    written = true;
                    return true;
                }

                if (!_frames.Writer.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                    return false;
            }

            return false;
        }
        finally
        {
            if (!written)
                frame.Frame.Dispose();
        }
    }

    private void ApplyClockStateLocked(double clockSeconds)
    {
        if (_clockNeedsReset)
        {
            _anchorClockSeconds = clockSeconds;
            _anchorTimelineSeconds = _timelineSeconds;
            _lastPlay = _options.Play;
            _clockNeedsReset = false;
            return;
        }

        if (_lastPlay == _options.Play)
            return;

        if (_lastPlay)
            _timelineSeconds = CurrentTimelineLocked(clockSeconds);

        _anchorClockSeconds = clockSeconds;
        _anchorTimelineSeconds = _timelineSeconds;
        _lastPlay = _options.Play;
    }

    private double CurrentTimelineLocked(double clockSeconds)
    {
        if (!_options.Play)
            return _timelineSeconds;

        var elapsed = Math.Max(0d, clockSeconds - _anchorClockSeconds);
        return _anchorTimelineSeconds + elapsed;
    }

    private PlaybackStatus BuildStatusLocked(bool playbackOverload)
    {
        if (_decodeFault is not null)
        {
            return PlaybackStatus.Idle with
            {
                Phase = FFmpegPlaybackPhase.Faulted,
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
                Phase = _opening ? FFmpegPlaybackPhase.Opening : FFmpegPlaybackPhase.Buffering,
                DecodePath = _decodePath,
                Message = _opening ? "Opening media with FFmpeg." : "Waiting for decoded video frames."
            };
        }

        var noQueuedFrames = !_frames.Reader.TryPeek(out _);
        var ended = _endOfStream && noQueuedFrames;
        var phase = ended
            ? FFmpegPlaybackPhase.Ended
            : _options.Play
                ? FFmpegPlaybackPhase.Playing
                : FFmpegPlaybackPhase.Paused;

        return new PlaybackStatus(
            Phase: phase,
            DecodePath: _decodePath,
            Position: _latestMediaTime.TotalSeconds,
            Duration: _mediaInfo.Duration.TotalSeconds,
            IsPlaying: _options.Play && !ended && _hasPresentedFrame,
            IsEnded: ended,
            PlaybackOverload: playbackOverload,
            Message: Describe(_mediaInfo, _decodeStatus));
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
                    Phase = FFmpegPlaybackPhase.Faulted,
                    DecodePath = _decodePath,
                    Message = message
                }
                : PlaybackStatus.BackendPending with
                {
                    Phase = opening ? FFmpegPlaybackPhase.Opening : FFmpegPlaybackPhase.Buffering,
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
        _decodePath = FFmpegDecodePath.None;
        _decodeStatus = options.DecodeMode == FFmpegDecodeMode.Hardware
            ? "Waiting for required D3D11VA decode."
            : "Waiting for decoder selection.";
        _timelineSeconds = initialPosition.TotalSeconds;
        _anchorTimelineSeconds = _timelineSeconds;
        _clockNeedsReset = true;
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
            Cancellation: _requestCancellation.Token);

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
        FFmpegDecodeMode DecodeMode,
        CancellationToken Cancellation);

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

    public IVideoPlayer Create(FFmpegVideoPlayer source, VideoPlaybackContext context)
        => new FFmpegPlayerSession(source, context);
}
