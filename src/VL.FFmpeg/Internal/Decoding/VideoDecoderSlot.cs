using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>Clears ownership before a cancellable replacement can yield.</summary>
internal sealed class VideoDecoderSlot : IDisposable
{
    public FFmpegVideoDecoder? Value { get; private set; }
    public async Task Replace(Func<Task<FFmpegVideoDecoder>> create)
    {
        Dispose();
        Value = await create().ConfigureAwait(false);
    }
    public void Dispose()
    {
        var previous = Value;
        Value = null;
        previous?.Dispose();
    }
    public static FFmpegVideoDecoder Open(DecodeMode mode, Func<DecodeMode, FFmpegVideoDecoder> create, Action<string> reportFallback)
    {
        try { return create(mode); }
        catch (FFmpegHardwareException exception) when (mode == DecodeMode.Auto)
        {
            reportFallback(exception.Message);
            return create(DecodeMode.Software);
        }
    }
}
