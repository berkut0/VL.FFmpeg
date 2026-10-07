namespace VL.FFmpeg.Internal;

/// <summary>Timing decisions only; the session owns queues, clocks and resource lifetimes.</summary>
internal static class VideoSchedulingPolicy
{
    public const int ReadyFrameLimit = 6;
    public const int RawFrameLimit = 2;
    public const double BufferWindow = .150;
    public const double TimestampTolerance = .001;

    public static bool NeedsPreview(bool requested, bool presented, int readyCount, bool playing, bool waitingForStart)
        => requested && !presented && readyCount == 0 && (!playing || waitingForStart);

    // Lateness alone does not make the newest available image useless. Reject rollback
    // and completed cycles; superseded candidates are replaced before conversion.
    public static bool IsObsolete(double timestamp, double? displayedTimeline,
        double target, double cycleOffset, double duration)
        => (displayedTimeline is { } shown && timestamp < shown - TimestampTolerance)
            || (duration > 0 && target >= cycleOffset + duration);

    // Diagnostic threshold, not permission to discard the only useful image.
    public static bool IsLate(double timestamp, double duration, double target)
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
