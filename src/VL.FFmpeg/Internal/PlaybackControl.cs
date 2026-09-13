using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal;

internal sealed class PlaybackControl
{
    private readonly object _syncRoot = new();
    private readonly Action<PlaybackOptions> _optionsChanged;
    private PlaybackOptions _options = PlaybackOptions.Default;
    private bool _lastSeek;

    public PlaybackControl(Action<PlaybackOptions> optionsChanged)
    {
        _optionsChanged = optionsChanged ?? throw new ArgumentNullException(nameof(optionsChanged));
    }

    public PlaybackOptions Options => Volatile.Read(ref _options);

    public void UpdateFromPins(
        string? filename,
        bool play,
        bool loop,
        double seekTime,
        bool seek,
        DecodeMode decodeMode)
    {
        filename ??= string.Empty;
        seekTime = Math.Max(0d, seekTime);

        lock (_syncRoot)
        {
            var current = _options;
            var seekRequestId = current.SeekRequestId;
            if (seek && !_lastSeek)
                seekRequestId++;
            _lastSeek = seek;

            CommitLocked(current with
            {
                Filename = filename,
                Play = play,
                Loop = loop,
                SeekTime = seekTime,
                SeekRequestId = seekRequestId,
                DecodeMode = decodeMode
            });
        }
    }

    public void SetFilename(string? filename)
        => Change(filename ?? string.Empty, static (options, value) => options with { Filename = value });

    public void SetPlay(bool play)
        => Change(play, static (options, value) => options with { Play = value });

    public void SetLoop(bool loop)
        => Change(loop, static (options, value) => options with { Loop = value });

    public void SetDecodeMode(DecodeMode decodeMode)
        => Change(decodeMode, static (options, value) => options with { DecodeMode = value });

    public void Open(string? filename, bool play)
    {
        filename ??= string.Empty;
        lock (_syncRoot)
        {
            var current = _options;
            CommitLocked(current with
            {
                Filename = filename,
                Play = play,
                SeekTime = 0d,
                SeekRequestId = current.SeekRequestId + 1
            });
        }
    }

    public void PlayFromStart()
    {
        lock (_syncRoot)
        {
            var current = _options;
            CommitLocked(current with
            {
                Play = true,
                SeekTime = 0d,
                SeekRequestId = current.SeekRequestId + 1
            });
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            var current = _options;
            CommitLocked(current with
            {
                Play = false,
                SeekTime = 0d,
                SeekRequestId = current.SeekRequestId + 1
            });
        }
    }

    public void Close()
    {
        lock (_syncRoot)
        {
            var current = _options;
            CommitLocked(current with
            {
                Filename = string.Empty,
                Play = false,
                SeekTime = 0d
            });
        }
    }

    public void Seek(double positionSeconds)
    {
        positionSeconds = Math.Max(0d, positionSeconds);
        Change(positionSeconds, static (options, value) => options with
        {
            SeekTime = value,
            SeekRequestId = options.SeekRequestId + 1
        });
    }

    private void Change<T>(T value, Func<PlaybackOptions, T, PlaybackOptions> change)
    {
        lock (_syncRoot)
            CommitLocked(change(_options, value));
    }

    private void CommitLocked(PlaybackOptions next)
    {
        var current = _options;
        if (next == current)
            return;

        next = next with { Revision = current.Revision + 1 };
        Volatile.Write(ref _options, next);
        _optionsChanged(next);
    }
}
