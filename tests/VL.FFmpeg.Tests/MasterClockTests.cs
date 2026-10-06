using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class MasterClockTests
{
    [Test]
    public void FirstUpdateAnchorsWithoutAdvancing()
    {
        var timeline = new MasterClock();
        timeline.Reset(2.5d, true, waitForReady: true);
        timeline.Observe(100d);
        timeline.Ready();

        var initial = timeline.Observe(100d);
        var advanced = timeline.Observe(101.25d);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial, Is.EqualTo(2.5d));
            Assert.That(advanced, Is.EqualTo(3.75d));
        }
    }

    [Test]
    public void PauseAndResumePreservePosition()
    {
        var timeline = new MasterClock();

        timeline.Reset(0, true, waitForReady: true);
        timeline.Observe(10d);
        timeline.Ready();
        timeline.Observe(10d);
        timeline.Observe(12d);
        timeline.SetPlay(false);
        var paused = timeline.Observe(12d);
        var held = timeline.Observe(20d);
        timeline.SetPlay(true);
        var resumed = timeline.Observe(20d);
        var advanced = timeline.Observe(21d);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paused, Is.EqualTo(2d));
            Assert.That(held, Is.EqualTo(2d));
            Assert.That(resumed, Is.EqualTo(2d));
            Assert.That(advanced, Is.EqualTo(3d));
        }
    }

    [Test]
    public void ResetUsesTheNextClockValueAsANewAnchor()
    {
        var timeline = new MasterClock();

        timeline.Reset(0, true, waitForReady: true);
        timeline.Observe(10d);
        timeline.Ready();
        timeline.Observe(12d);
        timeline.Reset(7d, true, waitForReady: true);
        timeline.Observe(200d);
        timeline.Ready();

        var reset = timeline.Observe(200d);
        var advanced = timeline.Observe(201d);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reset, Is.EqualTo(7d));
            Assert.That(advanced, Is.EqualTo(8d));
        }
    }
}
