using VL.Core.Import;
using VL.FFmpeg.Internal;
using VL.Lib.Basics.Video;
using VL.Model;

namespace VL.FFmpeg.Nodes;

/// <summary>
/// FFmpeg video player for vvvv gamma.
/// </summary>
/// <remarks>
/// Connect the output to VideoSourceToSKImage or VideoSourceToTexture.
/// </remarks>
[ProcessNode]
public sealed class FFmpegVideoPlayer : IVideoSource2, IDisposable
{
    private readonly object _syncRoot = new();
    private readonly IFFmpegPlayerSessionFactory _sessionFactory;
    private PlaybackOptions _options = PlaybackOptions.Default;
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private IVideoPlayer? _currentSession;
    private bool _lastSeek;
    private bool _wasEnded;
    private bool _disposed;
    private int _changedTicket;

    /// <summary>
    /// Creates a Gamma video source. Native resources are created lazily by a
    /// subscribed video consumer.
    /// </summary>
    public FFmpegVideoPlayer()
        : this(FFmpegPlayerSessionFactory.Instance)
    {
    }

    internal FFmpegVideoPlayer(IFFmpegPlayerSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>
    /// Updates playback parameters and returns a renderer-neutral video source.
    /// </summary>
    public void Update(
        out IVideoSource videoSource,
        out double position,
        out double duration,
        out bool isPlaying,
        out bool isEnded,
        out bool onEnd,
        out bool playbackOverload,
        out FFmpegPlaybackPhase phase,
        out FFmpegDecodePath decodePath,
        [Pin(Visibility = PinVisibility.Optional)] out string status,
        string filename = "",
        bool play = true,
        bool loop = false,
        double seekTime = 0d,
        bool seek = false,
        [Pin(Visibility = PinVisibility.Optional)] FFmpegDecodeMode decodeMode = FFmpegDecodeMode.Auto)
    {
        ThrowIfDisposed();
        PlaybackOptions? changedOptions = null;

        filename ??= string.Empty;
        seekTime = Math.Max(0d, seekTime);

        var current = Volatile.Read(ref _options);
        var seekRequestId = current.SeekRequestId;
        if (seek && !_lastSeek)
            seekRequestId++;
        _lastSeek = seek;

        if (!string.Equals(current.Filename, filename, StringComparison.Ordinal)
            || current.Play != play
            || current.Loop != loop
            || current.SeekTime != seekTime
            || current.SeekRequestId != seekRequestId
            || current.DecodeMode != decodeMode)
        {
            var next = new PlaybackOptions(
                Filename: filename,
                Play: play,
                Loop: loop,
                SeekTime: seekTime,
                SeekRequestId: seekRequestId,
                DecodeMode: decodeMode,
                Revision: current.Revision + 1);
            Volatile.Write(ref _options, next);
            changedOptions = next;
        }

        if (changedOptions is not null)
        {
            IPlaybackOptionsSink? sink;
            lock (_syncRoot)
                sink = _currentSession as IPlaybackOptionsSink;
            sink?.OptionsChanged(changedOptions);
        }

        var snapshot = Volatile.Read(ref _status);
        position = snapshot.Position;
        duration = snapshot.Duration;
        isPlaying = snapshot.IsPlaying;
        isEnded = snapshot.IsEnded;
        onEnd = snapshot.IsEnded && !_wasEnded;
        _wasEnded = snapshot.IsEnded;
        playbackOverload = snapshot.PlaybackOverload;
        phase = snapshot.Phase;
        decodePath = snapshot.DecodePath;
        status = snapshot.Message;
        videoSource = this;
    }

    IVideoPlayer? IVideoSource2.Start(VideoPlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_syncRoot)
        {
            if (_disposed || _currentSession is not null)
                return null;

            Volatile.Write(ref _status, PlaybackStatus.BackendPending);
            _currentSession = _sessionFactory.Create(this, context);
            return _currentSession;
        }
    }

    int IVideoSource2.ChangedTicket => Volatile.Read(ref _changedTicket);

    internal PlaybackOptions Options => Volatile.Read(ref _options);

    internal void PublishStatus(IVideoPlayer session, PlaybackStatus status)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(status);

        lock (_syncRoot)
        {
            if (ReferenceEquals(_currentSession, session))
                Volatile.Write(ref _status, status);
        }
    }

    internal void SessionDisposed(IVideoPlayer session)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_currentSession, session))
                return;

            _currentSession = null;
            Volatile.Write(ref _status, PlaybackStatus.Idle);
            unchecked
            {
                _changedTicket++;
            }
        }
    }

    /// <summary>
    /// Stops the active consumer session and prevents future subscriptions.
    /// </summary>
    void IDisposable.Dispose() => DisposeCore();

    private void DisposeCore()
    {
        IVideoPlayer? session;
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            session = _currentSession;
            _currentSession = null;
            Volatile.Write(ref _status, PlaybackStatus.Idle with
            {
                Phase = FFmpegPlaybackPhase.Disposed,
                Message = "Disposed"
            });
            unchecked
            {
                _changedTicket++;
            }
        }

        session?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
