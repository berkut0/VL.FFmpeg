namespace VL.FFmpeg.Internal.Decoding;

internal sealed class FFmpegHardwareException : Exception
{
    public FFmpegHardwareException(string message)
        : base(message)
    {
    }

    public FFmpegHardwareException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
