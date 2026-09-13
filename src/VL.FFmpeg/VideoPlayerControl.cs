using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Nodes;

/// <summary>
/// Thread-safe transport control for VideoPlayer (Advanced).
/// </summary>
public sealed class VideoPlayerControl
{
    private readonly VideoPlayerSource _source;

    internal VideoPlayerControl(VideoPlayerSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Current file requested by the transport.</summary>
    public string Filename => _source.Options.Filename;

    /// <summary>Timecode of the most recently presented frame, in seconds.</summary>
    public double Position => _source.Status.Position;

    /// <summary>Duration reported by the active media stream, in seconds.</summary>
    public double Duration => _source.Status.Duration;

    /// <summary>Whether the session is currently presenting playback.</summary>
    public bool IsPlaying => _source.Status.IsPlaying;

    /// <summary>Whether the active media stream reached its end.</summary>
    public bool IsEnded => _source.Status.IsEnded;

    /// <summary>Whether presentation skipped more than one queued frame.</summary>
    public bool PlaybackOverload => _source.Status.PlaybackOverload;

    /// <summary>Whether looping is requested.</summary>
    public bool Loop => _source.Options.Loop;

    /// <summary>Requested decoder selection mode.</summary>
    public DecodeMode DecodeMode => _source.Options.DecodeMode;

    /// <summary>Current playback lifecycle phase.</summary>
    public PlaybackPhase Phase => _source.Status.Phase;

    /// <summary>Current decoder delivery path.</summary>
    public DecodePath DecodePath => _source.Status.DecodePath;

    /// <summary>Current diagnostic message.</summary>
    public string Status => _source.Status.Message;

    /// <summary>Opens a file from its beginning and optionally starts playback.</summary>
    public void Open(string filename, bool play = true)
        => _source.Open(filename, play);

    /// <summary>Starts or resumes playback; restarts from zero after end-of-file.</summary>
    public void Play()
        => _source.Play();

    /// <summary>Pauses playback at the current position.</summary>
    public void Pause()
        => _source.Pause();

    /// <summary>Pauses playback and returns to the beginning.</summary>
    public void Stop()
        => _source.Stop();

    /// <summary>Closes the current file and resets the transport.</summary>
    public void Close()
        => _source.Close();

    /// <summary>Seeks to an absolute media time in seconds.</summary>
    public void Seek(double position)
        => _source.Seek(position);

    /// <summary>Enables or disables playback looping.</summary>
    public void SetLoop(bool loop)
        => _source.SetLoop(loop);

    /// <summary>Selects the decoder path used for subsequent frames.</summary>
    public void SetDecodeMode(DecodeMode decodeMode)
        => _source.SetDecodeMode(decodeMode);
}
