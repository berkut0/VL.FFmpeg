namespace VL.FFmpeg.Internal.Decoding;

internal sealed record FFmpegMediaInfo(
    int Width,
    int Height,
    TimeSpan Duration,
    (int N, int D) FrameRate,
    string VideoCodec);

