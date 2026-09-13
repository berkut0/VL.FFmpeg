using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed class VideoPlayerSource : IVideoSource2, IDisposable
{
    private readonly object _syncRoot = new();
    private readonly IFFmpegPlayerSessionFactory _sessionFactory;
    private readonly PlaybackControl _control;
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private IVideoPlayer? _currentSession;
    private bool _wasEnded;
    private bool _disposed;
    private int _changedTicket;

    internal VideoPlayerSource(IFFmpegPlayerSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _control = new PlaybackControl(OptionsChanged);
    }

    internal PlaybackOptions Options => _control.Options;

    internal PlaybackStatus Status => Volatile.Read(ref _status);

    internal void UpdateFromPins(
        string? filename,
        bool play,
        bool loop,
        double seekTime,
        bool seek,
        Nodes.DecodeMode decodeMode)
    {
        ThrowIfDisposed();
        _control.UpdateFromPins(filename, play, loop, seekTime, seek, decodeMode);
    }

    internal void ReadOutputs(
        out double position,
        out double duration,
        out bool isPlaying,
        out bool isEnded,
        out bool onEnd,
        out bool playbackOverload,
        out Nodes.PlaybackPhase phase,
        out Nodes.DecodePath decodePath,
        out string status)
    {
        ThrowIfDisposed();

        var snapshot = Status;
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
    }

    internal void Open(string? filename, bool play)
    {
        ThrowIfDisposed();
        _control.Open(filename, play);
    }

    internal void Play()
    {
        ThrowIfDisposed();
        if (Status.IsEnded)
            _control.PlayFromStart();
        else
            _control.SetPlay(true);
    }

    internal void Pause()
    {
        ThrowIfDisposed();
        _control.SetPlay(false);
    }

    internal void Stop()
    {
        ThrowIfDisposed();
        _control.Stop();
    }

    internal void Close()
    {
        ThrowIfDisposed();
        _control.Close();
    }

    internal void Seek(double positionSeconds)
    {
        ThrowIfDisposed();
        _control.Seek(positionSeconds);
    }

    internal void SetLoop(bool loop)
    {
        ThrowIfDisposed();
        _control.SetLoop(loop);
    }

    internal void SetDecodeMode(Nodes.DecodeMode decodeMode)
    {
        ThrowIfDisposed();
        _control.SetDecodeMode(decodeMode);
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

    private void OptionsChanged(PlaybackOptions options)
    {
        IPlaybackOptionsSink? sink;
        lock (_syncRoot)
            sink = _currentSession as IPlaybackOptionsSink;
        sink?.OptionsChanged(options);
    }

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

    void IDisposable.Dispose()
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
                Phase = Nodes.PlaybackPhase.Disposed,
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
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
