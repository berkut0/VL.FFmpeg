using VL.Lib.Basics.Imaging;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed record ManagedBgraVideoFrame(
    byte[] Bgra,
    int FrameWidth,
    int FrameHeight,
    TimeSpan FrameTimecode,
    (int N, int D) SourceFrameRate,
    string FrameMetadata)
    : VideoFrame(
        Metadata: FrameMetadata,
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

internal sealed record ManagedRgba16fVideoFrame(
    byte[] Rgba16f,
    int FrameWidth,
    int FrameHeight,
    TimeSpan FrameTimecode,
    (int N, int D) SourceFrameRate,
    string FrameMetadata)
    : VideoFrame(
        Metadata: FrameMetadata,
        Timecode: FrameTimecode,
        FrameRate: SourceFrameRate)
{
    public override int Width => FrameWidth;

    public override int Height => FrameHeight;

    public override PixelFormat PixelFormat => PixelFormat.R16G16B16A16F;

    public override int PixelSizeInBytes => 8;

    public override bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = Rgba16f;
        return true;
    }
}
