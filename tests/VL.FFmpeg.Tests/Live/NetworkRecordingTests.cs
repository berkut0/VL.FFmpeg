using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests.Live;

public sealed class NetworkRecordingTests
{
    private string? _runtime;
    [SetUp] public void Setup()
    {
        _runtime = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
    }
    [TearDown] public void Cleanup() => Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", _runtime);

    [Test]
    public void HttpRecordingSupportsPausedSeekResumeAndLoop()
    {
        using var server = new HttpMediaServer();
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(server.Url, false);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, source, clock);
        source.Seek(5.02);
        using var seeked = WaitFrame(player, source, clock);
        Assert.That(seeked.Resource.Timecode.TotalSeconds, Is.EqualTo(5).Within(.001));
        Assert.That(player.GrabVideoFrame(), Is.Null);
        source.Play();
        using var resumed = WaitFrame(player, source, clock);
        Assert.That(resumed.Resource.Timecode.TotalSeconds, Is.GreaterThan(5));
        source.Pause(); source.SetLoop(true); source.Seek(7.96);
        using var last = WaitFrame(player, source, clock);
        source.Play(); clock.Time += .2;
        using var looped = WaitFrame(player, source, clock);
        Assert.That(looped.Resource.Timecode.TotalSeconds, Is.LessThan(.5));
        Assert.That(server.Offsets.Any(offset => offset > 0), Is.True, "Seek must exercise real HTTP Range requests.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OrdinaryHttpsRejectsAnUntrustedCertificate(bool customDeadlines)
    {
        using var server = new HttpMediaServer(tls: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        // Positive control: prove the TLS server serves valid media before testing trust rejection.
        using (var control = new FFmpegDemuxContext(server.Url, cancel.Token, MediaFixtures.Runtime,
            new MediaInputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
                new Dictionary<string, string> { ["tls_verify"] = "0" })))
            Assert.That(control.FormatName, Is.EqualTo("yuv4mpegpipe"));
        Assert.Throws<FFmpegDecodeException>((Action)(() =>
        {
            using var input = new FFmpegDemuxContext(server.Url, cancel.Token, MediaFixtures.Runtime,
                customDeadlines ? new MediaInputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)) : null);
        }));
    }

    [Test]
    public void HttpRedirectCannotBypassHttpsCertificateVerification()
    {
        using var secure = new HttpMediaServer(tls: true);
        using var redirect = new HttpMediaServer { RedirectTo = secure.Url };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Assert.Throws<FFmpegDecodeException>((Action)(() =>
        {
            using var input = new FFmpegDemuxContext(redirect.Url, cancel.Token, MediaFixtures.Runtime);
        }));
        Assert.That(redirect.Connections, Is.EqualTo(1));
    }

    [Test]
    public void LaterRangeRedirectRetainsCertificateVerification()
    {
        using var secure = new HttpMediaServer(tls: true);
        using var recording = new HttpMediaServer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var input = new FFmpegDemuxContext(recording.Url, cancel.Token, MediaFixtures.Runtime);
        recording.RedirectTo = secure.Url;
        Assert.Throws<FFmpegDecodeException>((Action)(() => input.Seek(TimeSpan.FromSeconds(7))));
        Assert.That(recording.Connections, Is.GreaterThan(1));
    }

    [Test]
    public async Task NetworkSeekHasAnOperationDeadline()
    {
        using var server = new HttpMediaServer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var input = new FFmpegDemuxContext(server.Url, cancel.Token, MediaFixtures.Runtime,
            new MediaInputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200)));
        server.StallRequests = true;
        var started = Stopwatch.StartNew();
        Exception? error = null;
        try { await Task.Run(() => input.Seek(TimeSpan.FromSeconds(7))); }
        catch (Exception e) { error = e; }
        Assert.That(error, Is.TypeOf<TimeoutException>());
        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void ReopeningTheSameUrlRecoversAfterATruncatedResponse()
    {
        using var server = new HttpMediaServer { DropResponseAfterBytes = 65536 };
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(server.Url, true);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        var watch = Stopwatch.StartNew();
        while (source.Status.Phase != PlaybackPhase.Faulted && watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            clock.Time = watch.Elapsed.TotalSeconds;
            if (player.GrabVideoFrame() is { } provider) { using var frame = provider.GetHandle(); }
            Thread.Sleep(5);
        }
        Assert.That(source.Status.Phase, Is.EqualTo(PlaybackPhase.Faulted), source.Status.Message);
        source.Open(server.Url, false);
        using var recovered = WaitFrame(player, source, clock);
        Assert.That(recovered.Resource.Timecode.TotalSeconds, Is.Zero.Within(.001));
        source.Seek(4);
        using var later = WaitFrame(player, source, clock);
        Assert.That(later.Resource.Timecode.TotalSeconds, Is.EqualTo(4).Within(.001));
    }

    [Test]
    public void SwitchingUrlCancelsAnOutstandingHttpSeek()
    {
        using var blocked = new HttpMediaServer();
        using var replacement = new HttpMediaServer();
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(blocked.Url, false);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, source, clock);
        blocked.StallRequests = true;
        source.Seek(7);
        Assert.That(SpinWait.SpinUntil(() => blocked.Connections > 1, 1000), Is.True);
        var watch = Stopwatch.StartNew();
        source.Open(replacement.Url, false);
        using var changed = WaitFrame(player, source, clock);
        Assert.That(changed.Resource.Timecode.TotalSeconds, Is.Zero.Within(.001));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void ServerWithoutRangesDoesNotSilentlyPublishTheWrongSeekFrame()
    {
        using var server = new HttpMediaServer { SupportsRanges = false };
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(server.Url, false);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, source, clock);
        foreach (var position in new[] { 6d, 0d })
        {
            source.Seek(position);
            var watch = Stopwatch.StartNew();
            var finished = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (player.GrabVideoFrame() is { } provider)
                {
                    using var frame = provider.GetHandle();
                    Assert.That(frame.Resource.Timecode.TotalSeconds, Is.EqualTo(position).Within(.04));
                    finished = true; break;
                }
                if (source.Status.Phase == PlaybackPhase.Faulted)
                {
                    Assert.That(source.Status.Message, Does.Contain("seek"));
                    TestContext.WriteLine($"Non-range seek {position}: {source.Status.Message}");
                    return;
                }
                Thread.Sleep(5);
            }
            Assert.That(finished, Is.True, "An unsupported seek must report failure, not buffer forever.");
        }
    }

    [Test]
    [Explicit("Uses the trusted W3C HTTPS recording; requires outbound network access.")]
    public void TrustedHttpsRecordingSupportsPauseSeekAndLoop()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open("https://media.w3.org/2010/05/sintel/trailer.mp4", false);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, source, clock);
        source.Seek(5);
        using var seeked = WaitFrame(player, source, clock);
        Assert.That(seeked.Resource.Timecode.TotalSeconds, Is.EqualTo(5).Within(.06));
        Assert.That(player.GrabVideoFrame(), Is.Null);
        source.Play();
        using var resumed = WaitFrame(player, source, clock);
        source.Pause(); source.SetLoop(true); source.Seek(source.Status.Duration - .05);
        using var ending = WaitFrame(player, source, clock);
        source.Play(); clock.Time += .2;
        var loopWait = Stopwatch.StartNew();
        while (loopWait.Elapsed < TimeSpan.FromSeconds(3))
        {
            using var looped = WaitFrame(player, source, clock);
            if (looped.Resource.Timecode.TotalSeconds < 1) return;
        }
        Assert.Fail("HTTPS recording did not return to its beginning.");
    }

    internal static IResourceHandle<VideoFrame> WaitFrame(IVideoPlayer player, VideoPlayerSource source, ManualClock clock)
    {
        var watch = Stopwatch.StartNew();
        var origin = clock.Time.Seconds;
        while (watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            clock.Time = origin + watch.Elapsed.TotalSeconds;
            if (player.GrabVideoFrame() is { } provider) return provider.GetHandle();
            if (source.Status.Phase == PlaybackPhase.Faulted) Assert.Fail(source.Status.Message);
            Thread.Sleep(5);
        }
        throw new TimeoutException(source.Status.Message);
    }
}
