using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal;

internal sealed record PlaybackOptions(
    string Filename,
    bool Play,
    bool Loop,
    double SeekTime,
    long SeekRequestId,
    FFmpegDecodeMode DecodeMode,
    long Revision)
{
    public static readonly PlaybackOptions Default = new(
        Filename: string.Empty,
        Play: true,
        Loop: false,
        SeekTime: 0d,
        SeekRequestId: 0,
        DecodeMode: FFmpegDecodeMode.Auto,
        Revision: 0);
}
