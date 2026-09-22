using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackHealthTrackerTests
{
    [Test]
    public void FiftyFpsFramesOnSixtyFpsClockDoNotCountAsPresentationUnderruns()
    {
        var tracker = new PlaybackHealthTracker();

        tracker.Observe(
            clockSeconds: 0d,
            targetTimelineSeconds: 0d,
            presentedTimelineSeconds: 0d,
            queueDepth: 0,
            frameDurationSeconds: 0.02d,
            drainedFrames: 1,
            active: true);
        var betweenFrames = tracker.Observe(
            clockSeconds: 1d / 60d,
            targetTimelineSeconds: 1d / 60d,
            presentedTimelineSeconds: null,
            queueDepth: 0,
            frameDurationSeconds: 0.02d,
            drainedFrames: 0,
            active: true);
        var nextFrame = tracker.Observe(
            clockSeconds: 2d / 60d,
            targetTimelineSeconds: 2d / 60d,
            presentedTimelineSeconds: 0.02d,
            queueDepth: 0,
            frameDurationSeconds: 0.02d,
            drainedFrames: 1,
            active: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(betweenFrames.QueueEmptyEvents, Is.EqualTo(1));
            Assert.That(betweenFrames.PresentationUnderruns, Is.Zero);
            Assert.That(nextFrame.PresentationUnderruns, Is.Zero);
            Assert.That(nextFrame.DroppedFrames, Is.Zero);
        }
    }

    [Test]
    public void MissingFramePastDeadlineCountsOneUnderrunAndTracksMaximumDelay()
    {
        var tracker = new PlaybackHealthTracker();
        tracker.Observe(0d, 0d, 0d, 0, 0.02d, 1, active: true);
        tracker.Observe(1d / 60d, 1d / 60d, null, 0, 0.02d, 0, active: true);
        tracker.Observe(2d / 60d, 2d / 60d, null, 0, 0.02d, 0, active: true);
        var late = tracker.Observe(
            3d / 60d,
            3d / 60d,
            null,
            0,
            0.02d,
            0,
            active: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(late.QueueEmptyEvents, Is.EqualTo(1));
            Assert.That(late.PresentationUnderruns, Is.EqualTo(1));
            Assert.That(late.IsPresentationLate, Is.True);
            Assert.That(late.MaxQueueEmptyDuration.TotalMilliseconds, Is.EqualTo(33.333d).Within(0.01d));
            Assert.That(late.MaxPresentationLateness.TotalMilliseconds, Is.EqualTo(30d).Within(0.01d));
        }
    }

    [Test]
    public void DrainingSeveralFramesCountsDroppedFrames()
    {
        var tracker = new PlaybackHealthTracker();

        var snapshot = tracker.Observe(
            clockSeconds: 0.1d,
            targetTimelineSeconds: 0.1d,
            presentedTimelineSeconds: 0.08d,
            queueDepth: 1,
            frameDurationSeconds: 0.02d,
            drainedFrames: 3,
            active: true);

        Assert.That(snapshot.DroppedFrames, Is.EqualTo(2));
    }

    [Test]
    public void TracksMaximumProducerAndQueueWaitDurations()
    {
        var tracker = new PlaybackHealthTracker();
        tracker.ObserveProducerDuration(TimeSpan.FromMilliseconds(12d));
        tracker.ObserveProducerDuration(TimeSpan.FromMilliseconds(8d));
        tracker.ObserveQueueWaitDuration(TimeSpan.FromMilliseconds(5d));

        var snapshot = tracker.Observe(
            0d,
            0d,
            null,
            0,
            0.02d,
            0,
            active: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.MaxProducerDuration.TotalMilliseconds, Is.EqualTo(12d));
            Assert.That(snapshot.MaxQueueWaitDuration.TotalMilliseconds, Is.EqualTo(5d));
        }
    }
}
