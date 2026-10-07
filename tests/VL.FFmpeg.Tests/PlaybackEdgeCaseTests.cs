using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackEdgeCaseTests
{
    private string? _previousRuntime;

    [SetUp]
    public void SetRuntime()
    {
        _previousRuntime = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
    }

    [TearDown]
    public void RestoreRuntime() => Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", _previousRuntime);

    [TestCase(0d)]
    [TestCase(.25d)]
    public void PausedSeekCanReadTheNextTimestampBeyondNormalReadAhead(double position)
    {
        using var file = CreateRawVideo(frameRate: 1);
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(file.Path, false);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        using var first = WaitFrame(session, source);
        source.Seek(position);
        using var preview = WaitFrame(session, source);
        Assert.That(preview.Resource.Timecode.TotalSeconds, Is.Zero);
    }

    [TestCase(1, false)]
    [TestCase(4, true)]
    [TestCase(12, false)]
    public void MixedPlayersKeepHeldFramesThroughRapidSeeksAndShutdown(int count, bool linear)
    {
        using var raw = CreateRawVideo();
        string[] files = [MediaFixtures.Video(), Alpha("vp8"), Alpha("vp9"), raw.Path];
        var budgetBefore = ResourceBudget.Cpu.Used;
        var sources = new List<VideoPlayerSource>();
        var sessions = new List<IVideoPlayer>();
        var held = new List<IResourceHandle<VideoFrame>>();
        var pixels = new List<byte[]>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
                sources.Add(source);
                source.Open(files[i % files.Length], false);
                source.SetLoop(true);
                sessions.Add(((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(),
                    NullLogger.Instance, usesLinearColorspace: linear))!);
            }
            foreach (var session in sessions)
            {
                var frame = WaitFrame(session);
                held.Add(frame);
                Assert.That(frame.Resource.TryGetMemory(out var memory), Is.True);
                pixels.Add(memory.ToArray());
            }
            foreach (var source in sources)
            {
                for (var request = 0; request < 12; request++) source.Seek(request % 2 == 0 ? .02 : 0);
                source.Play();
                source.Pause();
            }
            foreach (var session in sessions)
            {
                using var current = WaitFrame(session, sources[sessions.IndexOf(session)]);
                Assert.That(current.Resource.Timecode.TotalSeconds, Is.EqualTo(0).Within(.001));
                var unexpected = session.GrabVideoFrame();
                if (unexpected is not null) { using var handle = unexpected.GetHandle(); }
                Assert.That(unexpected, Is.Null, "Paused preview must not continue draining the ready queue.");
            }
            foreach (IDisposable source in sources) source.Dispose();
            for (var i = 0; i < held.Count; i++)
            {
                Assert.That(held[i].Resource.TryGetMemory(out var memory), Is.True);
                Assert.That(memory.ToArray(), Is.EqualTo(pixels[i]), "A held image must survive its source and pool retirement.");
            }
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
            foreach (IDisposable source in sources) source.Dispose();
            foreach (var frame in held) frame.Dispose();
        }
        Assert.That(ResourceBudget.Cpu.Used, Is.EqualTo(budgetBefore), "All leases and packet reservations must return after shutdown.");
    }

    [Test]
    public void LowFrameRateLoopPreparesTheNextCycleBeforeItsSecondTimestampIsDue()
    {
        using var file = CreateRawVideo(frameRate: 1);
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(file.Path, false);
        source.SetLoop(true);
        source.Seek(19.25);
        var clock = new ManualClock();
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var last = WaitFrame(session, source);
        Assert.That(last.Resource.Timecode.TotalSeconds, Is.EqualTo(19));
        source.Play();
        var initial = session.GrabVideoFrame();
        if (initial is not null) { using var handle = initial.GetHandle(); }
        clock.Time = .8;
        using var wrapped = WaitFrame(session, source);
        Assert.That(wrapped.Resource.Timecode.TotalSeconds, Is.Zero);
        Assert.That(source.GetPlayback().Metrics.ContainerOpens, Is.EqualTo(1));
    }

    [Test]
    public void FileAndResolutionChangesCannotPublishThePreviousGeneration()
    {
        using var raw = CreateRawVideo();
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), false);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        using var held = WaitFrame(session);
        Assert.That(held.Resource.TryGetMemory(out var originalMemory), Is.True);
        var original = originalMemory.ToArray();
        foreach (var file in new[] { Alpha("vp8"), raw.Path, Alpha("vp9"), raw.Path })
        {
            source.Open(file, false);
            using var frame = WaitFrame(session);
            Assert.That(frame.Resource.TryGetMemory(out var memory), Is.True);
            var isRaw = file == raw.Path;
            Assert.That(frame.Resource.Width, Is.EqualTo(isRaw ? 64 : 32));
            Assert.That(frame.Resource.Height, Is.EqualTo(32));
            Assert.That(memory.Length, Is.EqualTo(frame.Resource.Width * frame.Resource.Height * 4));
            Assert.That(memory.Span[3], Is.EqualTo(isRaw ? 255 : 63));
            Assert.That(originalMemory.ToArray(), Is.EqualTo(original));
        }
    }

    [Test]
    public void ValidFileRecoversAfterAnOpenFailure()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mov"), false);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        var watch = Stopwatch.StartNew();
        while (source.Status.Phase != PlaybackPhase.Faulted && watch.ElapsedMilliseconds < 3000)
        {
            var p = session.GrabVideoFrame();
            if (p is not null) { using var h = p.GetHandle(); }
            Thread.Sleep(2);
        }
        Assert.That(source.Status.Phase, Is.EqualTo(PlaybackPhase.Faulted));
        source.Open(Alpha("vp9"), false);
        using var frame = WaitFrame(session);
        Assert.That(source.Status.Phase, Is.EqualTo(PlaybackPhase.Paused));
        Assert.That(frame.Resource.TryGetMemory(out var memory), Is.True);
        Assert.That(memory.Span[3], Is.EqualTo(63));
    }

    private static string Alpha(string codec)
        => Path.Combine(MediaFixtures.Root, "tests", "VL.FFmpeg.Tests", "TestData", $"{codec}-alpha.webm");

    private static TemporaryMedia CreateRawVideo(int frameRate = 25)
    {
        var file = new TemporaryMedia(Path.Combine(Path.GetTempPath(), $"vl-ffmpeg-{Guid.NewGuid():N}.y4m"));
        using var stream = File.Create(file.Path);
        stream.Write(System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W64 H32 F{frameRate}:1 Ip A1:1 C420jpeg\n"));
        var pixels = Enumerable.Repeat((byte)128, 64 * 32 * 3 / 2).ToArray();
        for (var i = 0; i < 20; i++) { stream.Write("FRAME\n"u8); stream.Write(pixels); }
        return file;
    }

    private static IResourceHandle<VideoFrame> WaitFrame(IVideoPlayer session, VideoPlayerSource? source = null)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 5000)
        {
            var provider = session.GrabVideoFrame();
            if (provider is not null) return provider.GetHandle();
            Thread.Sleep(2);
        }
        Assert.Fail($"No frame delivered after a control change. File={source?.Options.Filename}; {source?.Status}");
        return null!;
    }
}
