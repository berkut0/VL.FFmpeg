namespace VL.FFmpeg.Nodes;

/// <summary>
/// High-level lifecycle state of an FFmpeg playback session.
/// </summary>
public enum FFmpegPlaybackPhase
{
    /// <summary>No media session is active.</summary>
    Idle,

    /// <summary>The input is being opened and inspected.</summary>
    Opening,

    /// <summary>The session is waiting for enough decoded data.</summary>
    Buffering,

    /// <summary>The presentation clock is advancing.</summary>
    Playing,

    /// <summary>The session is open but its presentation clock is paused.</summary>
    Paused,

    /// <summary>The end of the selected media range was reached.</summary>
    Ended,

    /// <summary>The session cannot continue because of an error.</summary>
    Faulted,

    /// <summary>The source has been disposed.</summary>
    Disposed
}
