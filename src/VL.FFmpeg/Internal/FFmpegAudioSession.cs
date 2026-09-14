using VL.FFmpeg.Internal.Decoding;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;

namespace VL.FFmpeg.Internal;

internal sealed class FFmpegAudioSession : IPlaybackOptionsSink, IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _requestSignal = new(0, 1);
    private readonly List<CancellationTokenSource> _retiredRequestCancellations = [];
    private readonly Task _worker;
    private PlaybackOptions _options;
    private DecodeRequest? _request;
    private CancellationTokenSource? _requestCancellation;
    private AudioSampleBuffer? _buffer;
    private TimeSpan _latestMediaTime;
    private long _nextGeneration;
    private Exception? _fault;
    private bool _ended;
    private bool _disposed;

    public FFmpegAudioSession(PlaybackOptions options)
    {
        _options = options;
        _worker = Task.Run(WorkerLoop);
    }

    public Exception? Fault => Volatile.Read(ref _fault);

    public IResourceProvider<AudioFrame>? Grab(
        int sampleCount,
        int sampleRate,
        int channelCount,
        bool interleaved,
        TimeSpan currentPosition)
    {
        if (sampleCount <= 0 || sampleRate <= 0 || channelCount < 0)
            return null;

        AudioSampleBuffer? buffer;
        var signalWorker = false;
        lock (_syncRoot)
        {
            if (_disposed || !_options.Play || string.IsNullOrWhiteSpace(_options.Filename))
                return null;

            var requiredCapacity = Math.Max(sampleRate * 2, sampleCount * 8);
            if (_request is null
                || _request.SampleRate != sampleRate
                || _request.ChannelCount != channelCount
                || _request.BufferCapacity < requiredCapacity)
            {
                var start = _latestMediaTime > TimeSpan.Zero
                    ? _latestMediaTime
                    : currentPosition;
                ResetRequestLocked(_options, start, sampleRate, channelCount, requiredCapacity);
                signalWorker = true;
            }
            buffer = _buffer;
        }

        if (signalWorker)
            SignalWorker();
        return buffer?.TryRead(sampleCount, interleaved);
    }

    public void OptionsChanged(PlaybackOptions options)
    {
        var signalWorker = false;
        var playChanged = false;
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            var previous = _options;
            _options = options;
            playChanged = options.Play != previous.Play;
            if (_request is not null
                && (!string.Equals(previous.Filename, options.Filename, StringComparison.Ordinal)
                    || previous.SeekRequestId != options.SeekRequestId
                    || (!previous.Loop && options.Loop && _ended)))
            {
                var position = !string.Equals(previous.Filename, options.Filename, StringComparison.Ordinal)
                    ? TimeSpan.Zero
                    : previous.SeekRequestId != options.SeekRequestId
                        ? TimeSpan.FromSeconds(options.SeekTime)
                        : _latestMediaTime;
                ResetRequestLocked(
                    options,
                    position,
                    _request.SampleRate,
                    _request.ChannelCount,
                    _request.BufferCapacity);
                signalWorker = true;
            }
        }

        if (signalWorker || playChanged)
            SignalWorker();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            _requestCancellation?.Cancel();
            _lifetimeCancellation.Cancel();
            _buffer?.Dispose();
        }
        SignalWorker();

        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _buffer?.Dispose();
            _requestCancellation?.Dispose();
            foreach (var cancellation in _retiredRequestCancellations)
                cancellation.Dispose();
            _requestSignal.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void WorkerLoop()
    {
        var processedGeneration = -1L;
        while (!_lifetimeCancellation.IsCancellationRequested)
        {
            DecodeRequest? request;
            lock (_syncRoot)
                request = _request;

            if (request is null || request.Generation == processedGeneration)
            {
                _requestSignal.Wait(_lifetimeCancellation.Token);
                continue;
            }
            processedGeneration = request.Generation;
            if (string.IsNullOrWhiteSpace(request.Filename))
                continue;

            try
            {
                RunDecodeRequest(request);
            }
            catch (OperationCanceledException) when (
                request.Cancellation.IsCancellationRequested
                || _lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                lock (_syncRoot)
                {
                    if (_request?.Generation == request.Generation)
                    {
                        Volatile.Write(ref _fault, exception);
                        _ended = true;
                    }
                }
            }
        }
    }

    private void RunDecodeRequest(DecodeRequest request)
    {
        var initialPosition = request.InitialPosition;
        while (!request.Cancellation.IsCancellationRequested)
        {
            using var decoder = new FFmpegAudioDecoder(
                request.Filename,
                initialPosition,
                request.SampleRate,
                request.ChannelCount,
                request.Cancellation);

            AudioSampleBuffer buffer;
            lock (_syncRoot)
            {
                if (_request?.Generation != request.Generation)
                    return;
                _buffer ??= new AudioSampleBuffer(
                    decoder.OutputSampleRate,
                    decoder.OutputChannelCount,
                    request.BufferCapacity);
                buffer = _buffer;
                _ended = false;
            }

            decoder.Decode(frame =>
            {
                request.Cancellation.ThrowIfCancellationRequested();
                buffer.Write(frame, request.Cancellation);
                lock (_syncRoot)
                {
                    if (_request?.Generation == request.Generation)
                    {
                        _latestMediaTime = frame.Timecode
                            + TimeSpan.FromSeconds(frame.SampleCount / (double)frame.SampleRate);
                    }
                }
                return true;
            });

            request.Cancellation.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                if (_request?.Generation != request.Generation)
                    return;
                if (!_options.Loop)
                {
                    _ended = true;
                    return;
                }
            }
            initialPosition = TimeSpan.Zero;
        }
    }

    private void ResetRequestLocked(
        PlaybackOptions options,
        TimeSpan initialPosition,
        int sampleRate,
        int channelCount,
        int bufferCapacity)
    {
        _requestCancellation?.Cancel();
        if (_requestCancellation is not null)
            _retiredRequestCancellations.Add(_requestCancellation);
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _buffer?.Dispose();
        _buffer = null;
        _latestMediaTime = initialPosition;
        Volatile.Write(ref _fault, null);
        _ended = false;
        _request = new DecodeRequest(
            ++_nextGeneration,
            options.Filename,
            initialPosition,
            sampleRate,
            channelCount,
            bufferCapacity,
            _requestCancellation.Token);
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
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private sealed record DecodeRequest(
        long Generation,
        string Filename,
        TimeSpan InitialPosition,
        int SampleRate,
        int ChannelCount,
        int BufferCapacity,
        CancellationToken Cancellation);
}
