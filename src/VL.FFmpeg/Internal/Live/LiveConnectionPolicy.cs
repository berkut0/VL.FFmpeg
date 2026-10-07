using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Live;

internal sealed class LiveConnectionPolicy
{
    private static readonly int[] Delays = [1, 2, 4, 8, 10];
    private static readonly int[] TerminalErrors =
    [
        ffmpeg.AVERROR_HTTP_BAD_REQUEST, ffmpeg.AVERROR_HTTP_UNAUTHORIZED,
        ffmpeg.AVERROR_HTTP_FORBIDDEN, ffmpeg.AVERROR_HTTP_NOT_FOUND,
        ffmpeg.AVERROR_DECODER_NOT_FOUND, ffmpeg.AVERROR_DEMUXER_NOT_FOUND,
        ffmpeg.AVERROR_PROTOCOL_NOT_FOUND, ffmpeg.AVERROR_INVALIDDATA
    ];
    private int _retries;
    private double? _healthySince;

    public TimeSpan? NextDelay()
    {
        _healthySince = null;
        return _retries < Delays.Length ? TimeSpan.FromSeconds(Delays[_retries++]) : null;
    }

    public void ObserveHealthy(double seconds)
    {
        _healthySince ??= seconds;
        if (seconds - _healthySince >= 10) _retries = 0;
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || uri.Scheme is not ("rtsp" or "http" or "https"))
            throw new ArgumentException("Live playback requires an RTSP or HTTP/HTTPS URL.");
        return uri;
    }

    public static bool CanRetry(Exception error)
        => error is TimeoutException or IOException
            || error is FFmpegDecodeException native && !TerminalErrors.Contains(native.ErrorCode);

    // Native av_strerror descriptions contain no input URL. Do not echo arbitrary exception text.
    public static string Describe(Exception error) => error switch
    {
        FFmpegDecodeException native => $"{native.Operation}: {native.NativeMessage} ({native.ErrorCode})",
        TimeoutException => "Network operation timed out.",
        FFmpegHardwareException => "The requested hardware decoder is unavailable.",
        ArgumentException => "Invalid live source configuration.",
        NotSupportedException => "Unsupported live source or media format.",
        _ => "Live media processing failed."
    };
}
