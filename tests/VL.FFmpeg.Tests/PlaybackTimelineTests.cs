using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackTimelineTests
{
    [Test]
    public void FirstUpdateAnchorsWithoutAdvancing()
    {
        var timeline = new PlaybackTimeline();
        timeline.Reset(2.5d);

        var initial = timeline.Update(clockSeconds: 100d, play: true);
        var advanced = timeline.Update(clockSeconds: 101.25d, play: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial, Is.EqualTo(2.5d));
            Assert.That(advanced, Is.EqualTo(3.75d));
        }
    }

    [Test]
    public void PauseAndResumePreservePosition()
    {
        var timeline = new PlaybackTimeline();

        timeline.Update(clockSeconds: 10d, play: true);
        var paused = timeline.Update(clockSeconds: 12d, play: false);
        var held = timeline.Update(clockSeconds: 20d, play: false);
        var resumed = timeline.Update(clockSeconds: 20d, play: true);
        var advanced = timeline.Update(clockSeconds: 21d, play: true);

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
        var timeline = new PlaybackTimeline();

        timeline.Update(clockSeconds: 10d, play: true);
        timeline.Update(clockSeconds: 12d, play: true);
        timeline.Reset(7d);

        var reset = timeline.Update(clockSeconds: 200d, play: true);
        var advanced = timeline.Update(clockSeconds: 201d, play: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reset, Is.EqualTo(7d));
            Assert.That(advanced, Is.EqualTo(8d));
        }
    }
}
