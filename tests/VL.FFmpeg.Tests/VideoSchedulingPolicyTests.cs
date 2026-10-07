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
    public void ProgressIsRateLimitedAndCannotReviveAnEndedCycle()
    {
        Assert.That(VideoSchedulingPolicy.NeedsProgress(0, false, 0, 3, 0, 10), Is.True);
        Assert.That(VideoSchedulingPolicy.NeedsProgress(0, true, .1, 3, 0, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.NeedsProgress(0, true, .25, 3, 0, 10), Is.True);
        Assert.That(VideoSchedulingPolicy.NeedsProgress(1, true, 1, 3, 0, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.NeedsProgress(0, false, 1, 10, 0, 10), Is.False);
        Assert.That(VideoSchedulingPolicy.NeedsProgress(0, true, 1, 11, 10, 10), Is.True);
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
