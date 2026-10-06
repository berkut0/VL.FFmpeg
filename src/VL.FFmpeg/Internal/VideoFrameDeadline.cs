namespace VL.FFmpeg.Internal;

internal static class VideoFrameDeadline
{
    public const double Window = .150;
    // Keep a bounded recovery window instead of oscillating into complete output starvation.
    // Frames within this window are still reported as late; the media clock never slows down.
    public static bool IsExpired(double timestamp, double duration, double now)
        => duration > 0 && timestamp + duration + Window < now - .001;
}
