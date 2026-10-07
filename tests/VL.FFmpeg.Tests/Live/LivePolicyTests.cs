using NUnit.Framework;
using VL.FFmpeg.Internal.Live;

namespace VL.FFmpeg.Tests.Live;

public sealed class LivePolicyTests
{
    [Test]
    public void RetryAllowanceIsFiniteAndResetsOnlyAfterHealthyOutput()
    {
        var policy = new LiveConnectionPolicy();
        foreach (var seconds in new[] { 1, 2, 4, 8, 10 })
            Assert.That(policy.NextDelay(), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
        Assert.That(policy.NextDelay(), Is.Null);
        policy.ObserveHealthy(20);
        policy.ObserveHealthy(29);
        policy.ObserveHealthy(30);
        Assert.That(policy.NextDelay(), Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [TestCase("http://host/live")]
    [TestCase("https://host/live?secret=x")]
    [TestCase("rtsp://user:password@host:8554/live")]
    public void SupportedInputsAreAccepted(string url) => Assert.That(LiveConnectionPolicy.ValidateUrl(url).IsAbsoluteUri, Is.True);

    [TestCase("file:///C:/video.mov")]
    [TestCase("srt://host:9000")]
    [TestCase("not a url")]
    public void UnsupportedInputsDoNotLeakTheirValue(string url)
        => Assert.Throws<ArgumentException>((Action)(() => { LiveConnectionPolicy.ValidateUrl(url); }));

    [Test]
    public void TimelinePreservesOffsetsAndRecognizesClockReset()
    {
        var time = new LiveTimeline();
        var video = time.Map(100, .04, 0, video: true);
        var audio = time.Map(100.1, .02, .01, video: false);
        Assert.That(audio.Time - video.Time, Is.EqualTo(.1).Within(.00001));
        var next = time.Map(100.04, .04, .04, video: true);
        Assert.That(next.Epoch, Is.EqualTo(video.Epoch));
        var reset = time.Map(0, .04, .08, video: true);
        Assert.That(reset.Epoch, Is.GreaterThan(video.Epoch));
        Assert.That(reset.Time, Is.EqualTo(.08).Within(.00001));
        Assert.That(time.Map(.1, .02, .09, video: false).Epoch, Is.EqualTo(reset.Epoch));
    }

    [Test]
    public void NetworkGapDoesNotResetEpochButTimestampJumpDoes()
    {
        var time = new LiveTimeline();
        var first = time.Map(10, .04, 0, true);
        Assert.That(time.Map(20, .04, 10, true).Epoch, Is.EqualTo(first.Epoch));
        Assert.That(time.Map(40, .04, 10.04, true).Epoch, Is.GreaterThan(first.Epoch));
    }

    [Test]
    public void MissingTimestampsUseDurationOrArrivalTime()
    {
        var time = new LiveTimeline();
        Assert.That(time.Map(null, .04, 0, true).Time, Is.Zero);
        Assert.That(time.Map(null, .04, .005, true).Time, Is.EqualTo(.04).Within(.00001));
        Assert.That(time.Map(null, 0, .08, true).Time, Is.EqualTo(.08).Within(.00001));
    }

    [Test]
    public void OtherTrackCannotReintroduceAnOldEpoch()
    {
        var time = new LiveTimeline();
        time.Map(100, .04, 0, true);
        time.Map(100, .02, 0, false);
        var reset = time.Map(0, .04, 1, true);
        Assert.That(time.Map(100.1, .02, 1.01, false).Accepted, Is.False);
        var joined = time.Map(.02, .02, 1.02, false);
        Assert.That(joined.Epoch, Is.EqualTo(reset.Epoch));
        Assert.That(joined.Time, Is.EqualTo(1.02).Within(.00001));
    }

    [Test]
    public void SmallBackwardResetRejectsOldAudioWithoutResettingTwice()
    {
        var time = new LiveTimeline();
        time.Map(100, .04, 0, true);
        time.Map(100, .02, 0, false);
        var reset = time.Map(99, .04, .04, true);
        Assert.That(time.Map(100.02, .02, .05, false).Accepted, Is.False);
        var joined = time.Map(99.04, .02, .08, false);
        Assert.That(joined.Accepted, Is.True);
        Assert.That(joined.Epoch, Is.EqualTo(reset.Epoch));
        Assert.That(joined.Time, Is.EqualTo(.08).Within(.00001));
    }

    [Test]
    public void MissingAudioTimestampRejoinsAfterVideoReset()
    {
        var time = new LiveTimeline();
        time.Map(100, .04, 0, true);
        time.Map(100, .02, 0, false);
        var reset = time.Map(0, .04, 1, true);
        var joined = time.Map(null, .02, 1.01, false);
        Assert.That(joined.Accepted, Is.True);
        Assert.That(joined.Epoch, Is.EqualTo(reset.Epoch));
        Assert.That(joined.Time, Is.EqualTo(1.01).Within(.00001));
        Assert.That(time.Map(null, .02, 1.03, false).Time, Is.EqualTo(1.03).Within(.00001));
    }
}
