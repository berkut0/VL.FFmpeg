namespace VL.FFmpeg.Internal.Decoding;

internal sealed class FFmpegDecodeException : Exception
{
    public FFmpegDecodeException(string operation, int errorCode, string nativeMessage)
        : base($"FFmpeg failed to {operation}: {nativeMessage} ({errorCode}).")
    {
        Operation = operation;
        ErrorCode = errorCode;
        NativeMessage = nativeMessage;
    }

    public string Operation { get; }

    public int ErrorCode { get; }

    public string NativeMessage { get; }
}

