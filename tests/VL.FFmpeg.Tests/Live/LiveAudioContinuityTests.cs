using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests.Live;

public sealed class LiveAudioContinuityTests
{
    private string? _runtime;
    [SetUp] public void Setup()
    {
        _runtime = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
    }
    [TearDown] public void Cleanup() => Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", _runtime);

    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [TestCase(false, 50)]
    public void FastAvSourceDoesNotDiscardTimelyAudio(bool h264, int videoInterval)
    {
        using var server = new RtspLiveServer(h264) { PacketIntervalMilliseconds = 1, VideoIntervalTicks = videoInterval };
        using var source = new LiveVideoPlayer();
        source.Update(out _, out var audio, out _, out _, out _, out _, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        var silence = Consume(player, audio, clock, 5, knownTone: true);
        TestContext.WriteLine(source.Session.Status.Message);
        TestContext.WriteLine($"Silent blocks after startup: {silence}");
        Assert.That(Counter(source.Session.Status.Message, "audio gaps"), Is.Zero);
        Assert.That(silence, Is.LessThan(5));
    }

    [Test]
    [Explicit("Reproduces the reported Sintel HTTPS audio path; requires outbound network access.")]
    public void SintelLiveAudioDoesNotOverflow()
    {
        using var source = new LiveVideoPlayer();
        source.Update(out _, out var audio, out _, out _, out _, out _, "https://media.w3.org/2010/05/sintel/trailer.mp4");
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        Consume(player, audio, clock, 8, knownTone: false);
        TestContext.WriteLine(source.Session.Status.Message);
        Assert.That(Counter(source.Session.Status.Message, "audio gaps"), Is.Zero);
        Assert.That(Counter(source.Session.Status.Message, "audio underruns"), Is.LessThan(8));
    }

    [Test]
    public void UnconnectedAudioDoesNotPreventVideoOrCancellation()
    {
        using var server = new RtspLiveServer { PacketIntervalMilliseconds = 1 };
        using var source = new LiveVideoPlayer();
        source.Update(out _, out _, out _, out _, out _, out _, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        for (var i = 0; i < 12; i++) { using var frame = LivePlayerTests.WaitFrame(player, clock); }
        var stopping = Stopwatch.StartNew();
        ((IDisposable)source).Dispose();
        Assert.That(stopping.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    private static int Consume(IVideoPlayer player, IAudioSource audio, ManualClock clock, double seconds, bool knownTone)
    {
        var watch = Stopwatch.StartNew();
        var block = 0;
        var silent = 0;
        var presented = 0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            var now = watch.Elapsed.TotalSeconds;
            clock.Time = now;
            if (player.GrabVideoFrame() is { } picture) { using var handle = picture.GetHandle(); presented++; }
            while (now >= block * .01)
            {
                using var handle = audio.GrabAudioFrame(480, 48000, 1, false)?.GetHandle();
                if (knownTone && now > 2 && (handle is null || !handle.Resource.GetChannel(0).ToArray().Any(value => Math.Abs(value) > .01))) silent++;
                block++;
            }
            Thread.Sleep(1);
        }
        Assert.That(presented, Is.GreaterThan(1));
        return silent;
    }

    private static long Counter(string status, string name)
    {
        var match = Regex.Match(status, Regex.Escape(name) + @" (\d+)");
        Assert.That(match.Success, Is.True, status);
        return long.Parse(match.Groups[1].Value);
    }
}
