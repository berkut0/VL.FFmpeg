using VL.Lib.Basics.Imaging;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed record ManagedBgraVideoFrame(
    byte[] Bgra,
    int FrameWidth,
    int FrameHeight,
    TimeSpan FrameTimecode,
    (int N, int D) SourceFrameRate)
    : VideoFrame(
        Metadata: "FFmpeg software decode; BGRA8",
        Timecode: FrameTimecode,
        FrameRate: SourceFrameRate)
{
    public override int Width => FrameWidth;

    public override int Height => FrameHeight;

    public override PixelFormat PixelFormat => PixelFormat.B8G8R8A8;

    public override int PixelSizeInBytes => 4;

    public override bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = Bgra;
        return true;
    }
}

