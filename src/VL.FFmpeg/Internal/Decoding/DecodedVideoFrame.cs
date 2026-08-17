using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

internal abstract class DecodedVideoFrame : IDisposable
{
    protected DecodedVideoFrame(
        int width,
        int height,
        TimeSpan timecode,
        (int N, int D) frameRate,
        FFmpegDecodePath decodePath,
        string decodeStatus)
    {
        Width = width;
        Height = height;
        Timecode = timecode;
        FrameRate = frameRate;
        DecodePath = decodePath;
        DecodeStatus = decodeStatus;
    }

    public int Width { get; }

    public int Height { get; }

    public TimeSpan Timecode { get; }

    public (int N, int D) FrameRate { get; }

    public FFmpegDecodePath DecodePath { get; }

    public string DecodeStatus { get; internal set; }

    public abstract IResourceProvider<VideoFrame> CreateProvider();

    public abstract void Dispose();
}

internal sealed class CpuDecodedVideoFrame : DecodedVideoFrame
{
    private byte[]? _bgra;

    public CpuDecodedVideoFrame(
        byte[] bgra,
        int width,
        int height,
        TimeSpan timecode,
        (int N, int D) frameRate,
        string decodeStatus)
        : base(width, height, timecode, frameRate, FFmpegDecodePath.Software, decodeStatus)
    {
        _bgra = bgra;
    }

    public override IResourceProvider<VideoFrame> CreateProvider()
    {
        var pixels = Interlocked.Exchange(ref _bgra, null)
            ?? throw new InvalidOperationException("The decoded CPU frame was already consumed.");
        return ResourceProvider.Return<VideoFrame>(new ManagedBgraVideoFrame(
            pixels,
            Width,
            Height,
            Timecode,
            FrameRate));
    }

    public override void Dispose() => _bgra = null;
}

internal sealed class GpuDecodedVideoFrame : DecodedVideoFrame
{
    private D3D11TextureLease? _lease;

    public GpuDecodedVideoFrame(
        D3D11TextureLease lease,
        TimeSpan timecode,
        (int N, int D) frameRate,
        string decodeStatus)
        : base(
            lease.Texture.Width,
            lease.Texture.Height,
            timecode,
            frameRate,
            FFmpegDecodePath.D3D11VaGpuTexture,
            decodeStatus)
    {
        _lease = lease;
    }

    public override IResourceProvider<VideoFrame> CreateProvider()
    {
        var lease = Interlocked.Exchange(ref _lease, null)
            ?? throw new InvalidOperationException("The decoded GPU frame was already consumed.");
        try
        {
            VideoFrame frame = new GpuVideoFrame<BgraPixel>(
                lease.Texture,
                Metadata: DecodeStatus,
                Timecode: Timecode,
                FrameRate: FrameRate);
            return ResourceProvider.Return(
                frame,
                lease,
                static value => value.Dispose());
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public override void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
