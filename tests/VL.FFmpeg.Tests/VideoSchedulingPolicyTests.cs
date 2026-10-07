using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class VideoSchedulingPolicyTests
{
    [Test]
    public void RecoveryRequiresSustainedLagAndRetainsCooldownAcrossSeek()
    {
        var recovery = new RealtimeRecovery();
        Assert.That(recovery.ShouldSeek(10, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(10.4, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(10.5, 2, true), Is.True);
        recovery.ResetObservation();
        Assert.That(recovery.ShouldSeek(11, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(11.6, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(12.5, 2, true), Is.True);
    }

    [Test]
    public void PauseOrCatchUpClearsTheLagObservation()
    {
        var recovery = new RealtimeRecovery();
        recovery.ShouldSeek(0, 2, true);
        Assert.That(recovery.ShouldSeek(.6, 2, false), Is.False);
        Assert.That(recovery.ShouldSeek(1, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(1.6, .5, true), Is.False);
        Assert.That(recovery.ShouldSeek(2, 2, true), Is.False);
        Assert.That(recovery.ShouldSeek(2.5, 2, true), Is.True);
    }

    [Test]
    public void LateCandidatesRemainUsefulUntilSupersededOrTheirCycleEnds()
    {
        Assert.That(VideoSchedulingPolicy.IsObsolete(.020, null, 3, 0, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.IsObsolete(.020, 0, 3, 0, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.IsObsolete(.020, .040, 3, 0, 10), Is.True);
        Assert.That(VideoSchedulingPolicy.IsObsolete(9.9, null, 10, 0, 10), Is.True);
        Assert.That(VideoSchedulingPolicy.IsObsolete(10.1, 9.9, 11, 10, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.IsObsolete(0, 0, 3, 0, 0), Is.False,
            "Equal timestamps are allowed when a source has no useful timing metadata.");
    }

    [Test]
    public void PlayingSeekUsesDeadlinesWhilePausedSeekAllowsPreview()
    {
        Assert.That(VideoSchedulingPolicy.NeedsPreview(true, false, 0, true, false), Is.False);
        Assert.That(VideoSchedulingPolicy.NeedsPreview(true, false, 0, false, false), Is.True);
        Assert.That(VideoSchedulingPolicy.NeedsPreview(true, false, 0, true, true), Is.True);
        Assert.That(VideoSchedulingPolicy.NeedsPreview(true, false, 1, false, false), Is.False);
    }
}
