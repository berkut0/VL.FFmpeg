using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests.Live;

public sealed class RtspEdgeCaseTests
{
    private string? _runtime;
    [SetUp] public void Setup()
    {
        _runtime = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
    }
    [TearDown] public void Cleanup() => Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", _runtime);

    [TestCase(LiveTransport.Tcp)]
    [TestCase(LiveTransport.Udp)]
    public void ServerDisconnectReconnectsAndPresentsNewFrames(LiveTransport transport)
    {
        using var server = new RtspLiveServer { DisconnectAfterTicks = 80 };
        using var source = new LiveVideoPlayer();
        source.Update(out _, out _, out _, out _, out _, out _, server.Url, transport: transport);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = LivePlayerTests.WaitFrame(player, clock);
        var watch = Stopwatch.StartNew();
        var origin = clock.Time.Seconds;
        var recovered = false;
        while (watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            clock.Time = origin + watch.Elapsed.TotalSeconds;
            if (player.GrabVideoFrame() is { } provider)
            {
                using var frame = provider.GetHandle();
                if (server.Connections >= 2) { recovered = true; break; }
            }
            Thread.Sleep(5);
        }
        Assert.That(recovered, Is.True, source.Session.Status.Message);
        Assert.That(source.Session.Metrics.Seeks, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UdpPacketLossAndReorderingDoNotTriggerAReconnectLoop(bool reordered)
    {
        using var server = new RtspLiveServer { DropEveryVideoPacket = reordered ? 7 : 3, ReorderVideoFragments = reordered };
        using var source = new LiveVideoPlayer();
        source.Update(out _, out _, out _, out _, out _, out _, server.Url, transport: LiveTransport.Udp);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        for (var i = 0; i < 8; i++) { using var frame = LivePlayerTests.WaitFrame(player, clock); }
        Assert.That(server.Connections, Is.EqualTo(1));
        Assert.That(source.Session.Status.Phase, Is.EqualTo(LivePlaybackPhase.Playing));
    }

    [Test]
    public void H264UdpContinuesAfterLosingInterFrameFragments()
    {
        using var server = new RtspLiveServer(h264: true);
        using var source = new LiveVideoPlayer();
        source.Update(out _, out _, out _, out _, out _, out _, server.Url, transport: LiveTransport.Udp);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = LivePlayerTests.WaitFrame(player, clock);
        server.DropEveryVideoPacket = 7;
        var watch = Stopwatch.StartNew();
        var start = clock.Time.Seconds;
        while (watch.Elapsed < TimeSpan.FromSeconds(.8))
        {
            clock.Time = start + watch.Elapsed.TotalSeconds;
            player.GrabVideoFrame()?.GetHandle().Dispose();
            Thread.Sleep(5);
        }
        server.DropEveryVideoPacket = 0;
        // Observe beyond the two-second GOP cycle, not just a queued or concealed frame.
        watch.Restart();
        var count = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            using var recovered = LivePlayerTests.WaitFrame(player, clock);
            Assert.That(recovered.Resource.Width, Is.EqualTo(first.Resource.Width));
            count++;
        }
        Assert.That(count, Is.GreaterThan(8));
        Assert.That(source.Session.Status.Phase, Is.EqualTo(LivePlaybackPhase.Playing));
        Assert.That(server.Connections, Is.EqualTo(1));
    }

    [Test]
    public void SetupRejectionIsTerminal()
    {
        using var server = new RtspLiveServer { RejectSetup = true };
        using var source = new LiveVideoPlayer();
        LivePlayerTests.Update(source, server.Url);
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        Assert.That(SpinWait.SpinUntil(() => source.Session.Status.Phase == LivePlaybackPhase.Faulted, 3000), Is.True);
        Assert.That(server.Connections, Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RtspAuthenticationSucceedsOrFailsWithoutRetryStorm(bool valid)
    {
        using var server = new RtspLiveServer { RequireAuthorization = true };
        using var source = new LiveVideoPlayer();
        var url = server.Url.Replace("rtsp://", valid ? "rtsp://demo:password@" : "rtsp://demo:wrong-password@");
        LivePlayerTests.Update(source, url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        if (valid)
        {
            using var frame = LivePlayerTests.WaitFrame(player, clock);
            Assert.That(frame.Resource.Width, Is.EqualTo(32));
        }
        else Assert.That(SpinWait.SpinUntil(() => source.Session.Status.Phase == LivePlaybackPhase.Faulted, 3000), Is.True);
        Assert.That(server.Connections, Is.EqualTo(1));
        Assert.That(source.Session.Status.Message, Does.Not.Contain("password@"));
    }

    [Test]
    public void AudioAndVideoResumeAfterBackwardTimestampReset()
    {
        using var server = new RtspLiveServer();
        using var source = new LiveVideoPlayer();
        source.Update(out _, out var audio, out _, out _, out _, out _, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = LivePlayerTests.WaitFrame(player, clock);
        using (var sound = audio.GrabAudioFrame(256, 48000, 1, false)?.GetHandle()) { }
        var watch = Stopwatch.StartNew();
        var origin = clock.Time.Seconds;
        var audibleAfterReset = false;
        var framesAfterReset = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            var elapsed = watch.Elapsed.TotalSeconds;
            clock.Time = origin + elapsed;
            if (elapsed > 1) server.TimestampOffset = -90000;
            if (player.GrabVideoFrame() is { } provider)
            {
                using var frame = provider.GetHandle();
                if (elapsed > 2) framesAfterReset++;
            }
            if (audio.GrabAudioFrame(256, 48000, 1, false) is { } soundProvider)
            {
                using var sound = soundProvider.GetHandle();
                if (elapsed > 2 && sound.Resource.GetChannel(0).ToArray().Any(value => Math.Abs(value) > .01))
                    audibleAfterReset = true;
            }
            Thread.Sleep(5);
        }
        Assert.That(framesAfterReset, Is.GreaterThan(5), source.Session.Status.Message);
        Assert.That(audibleAfterReset, Is.True);
        Assert.That(server.Connections, Is.EqualTo(1));
    }

    [Test]
    public void UrlChangeCancelsStalledRtspHandshake()
    {
        using var blocked = new RtspLiveServer { StallMethod = "DESCRIBE" };
        using var replacement = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        LivePlayerTests.Update(source, blocked.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        Assert.That(SpinWait.SpinUntil(() => blocked.Connections > 0, 1000), Is.True);
        var watch = Stopwatch.StartNew();
        LivePlayerTests.Update(source, replacement.Url);
        using var frame = LivePlayerTests.WaitFrame(player, clock);
        Assert.That(frame.Resource.Width, Is.EqualTo(16));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void OrdinaryPlayerReportsUnsupportedRtspSeekWithoutHanging()
    {
        using var server = new RtspLiveServer { RejectSeek = true };
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(server.Url, true);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = NetworkRecordingTests.WaitFrame(player, source, clock);
        source.Pause();
        Assert.That(player.GrabVideoFrame(), Is.Null);
        source.Seek(20);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3) && source.Status.Phase != PlaybackPhase.Faulted)
        { player.GrabVideoFrame()?.GetHandle().Dispose(); Thread.Sleep(5); }
        Assert.That(source.Status.Phase, Is.EqualTo(PlaybackPhase.Faulted), source.Status.Message);
        Assert.That(source.Status.Message, Does.Contain("seek"));
    }
}
