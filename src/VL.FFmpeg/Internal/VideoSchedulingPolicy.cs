namespace VL.FFmpeg.Internal;

/// <summary>Timing decisions only; the session owns queues, clocks and resource lifetimes.</summary>
internal static class VideoSchedulingPolicy
{
    public const int ReadyFrameLimit = 6;
    public const int RawFrameLimit = 2;
    public const double BufferWindow = .150;
    public const double TimestampTolerance = .001;
    public const double ProgressInterval = .250;

    public static bool NeedsPreview(bool requested, bool presented, int readyCount, bool playing, bool waitingForStart)
        => requested && !presented && readyCount == 0 && (!playing || waitingForStart);

    // Overload must still yield images, but never revive a cycle whose end is already behind the clock.
    public static bool NeedsProgress(int readyCount, bool presented, double secondsSincePresentation,
        double target, double cycleOffset, double duration)
        => readyCount == 0 && (duration <= 0 || target < cycleOffset + duration)
            && (!presented || secondsSincePresentation >= ProgressInterval);

    // A bounded jitter window avoids alternating between catch-up and complete output starvation.
    public static bool IsExpired(double timestamp, double duration, double target)
        => duration > 0 && timestamp + duration + BufferWindow < target - TimestampTolerance;
}

/// <summary>Debounces sustained lateness and limits recovery seeks. Uses execution time, not media time.</summary>
internal sealed class RealtimeRecovery
{
    private const double LagThreshold = 1;
    private const double Persistence = .5;
    private const double Cooldown = 2;
    private double? _behindSince;
    private double _lastRecovery = double.NegativeInfinity;

    public bool ShouldSeek(double executionTime, double lateness, bool enabled)
    {
        if (!enabled || lateness <= LagThreshold)
        {
            ResetObservation();
            return false;
        }
        _behindSince ??= executionTime;
        if (executionTime - _behindSince < Persistence || executionTime - _lastRecovery < Cooldown)
            return false;
        _lastRecovery = executionTime;
        ResetObservation();
        return true;
    }

    // A manual seek resets the observation but must not bypass the recovery cooldown.
    public void ResetObservation() => _behindSince = null;
}
