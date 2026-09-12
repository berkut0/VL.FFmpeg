using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal;

internal sealed record PlaybackStatus(
    PlaybackPhase Phase,
    DecodePath DecodePath,
    double Position,
    double Duration,
    bool IsPlaying,
    bool IsEnded,
    bool PlaybackOverload,
    string Message)
{
    public static readonly PlaybackStatus Idle = new(
        Phase: PlaybackPhase.Idle,
        DecodePath: DecodePath.None,
        Position: 0d,
        Duration: 0d,
        IsPlaying: false,
        IsEnded: false,
        PlaybackOverload: false,
        Message: "Idle");

    public static readonly PlaybackStatus BackendPending = Idle with
    {
        Message = "Gamma source is active; waiting for the FFmpeg backend."
    };
}
