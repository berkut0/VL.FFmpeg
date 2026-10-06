using System.Diagnostics;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.Core;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.Lib.Animation;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed class RealtimeRegressionTests
{
    [Test]
    public void LateImageIsReportedEvenWhenAnotherFrameIsQueued()
    {
        var health = new PlaybackHealthTracker();
        var snapshot = health.Observe(.150, .150, .040, 2, .040, 1, true);
        Assert.That(snapshot.IsPresentationLate, Is.True);
        Assert.That(snapshot.MaxPresentationLateness.TotalMilliseconds, Is.EqualTo(70).Within(.001));
    }

    [Test]
    public void PausedSeekBetweenFramesPublishesTheContainingFrame()
    {
        var filename = MediaFixtures.Video();
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(filename, false);
        source.Seek(.505);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        double? pts = null;
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            var provider = session.GrabVideoFrame();
            if (provider is not null)
            {
                using var handle = provider.GetHandle();
                pts = handle.Resource.Timecode.TotalSeconds;
                break;
            }
            Thread.Sleep(5);
        }
        Assert.That(pts, Is.EqualTo(.480).Within(.001), source.Status.Message);
    }

    [Test]
    public void ResamplerDeliversAllSamplesAtEndOfStream()
    {
        using var fixture = MediaFixtures.Wave(44100, 1);
        using var decoder = new FFmpegAudioDecoder(fixture.Path, TimeSpan.Zero, 48000, 1,
            CancellationToken.None, MediaFixtures.Runtime);
        var count = 0;
        decoder.Decode(frame => { count += frame.SampleCount; return true; });
        Assert.That(count, Is.EqualTo(48000));
    }

    [Test]
    public void SeekReusesContainerAndPreservesHeldFrame()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), false);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        using var first = WaitFrame(session);
        Assert.That(first.Resource.TryGetMemory(out var memory), Is.True);
        var original = memory.ToArray();
        source.Seek(.505);
        using var next = WaitFrame(session);
        Assert.That(next.Resource.Timecode.TotalSeconds, Is.EqualTo(.480).Within(.001));
        Assert.That(memory.ToArray(), Is.EqualTo(original));
        Assert.That(source.GetPlayback().Metrics.ContainerOpens, Is.EqualTo(1));
        Assert.That(source.GetPlayback().Metrics.Seeks, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void AudioReconfigurationUsesCurrentTimeInsteadOfProducerTail()
    {
        using var fixture = MediaFixtures.Wave(44100, 3);
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(fixture.Path, true);
        var playback = source.GetPlayback();
        var watch = Stopwatch.StartNew();
        VL.Lib.Basics.Resources.IResourceProvider<VL.Lib.Basics.Audio.AudioFrame>? provider = null;
        while (watch.ElapsedMilliseconds < 2000 && provider is null)
        { provider = playback.GrabAudio(256, 48000, 1, false); if (provider is null) Thread.Sleep(2); }
        Assert.That(provider, Is.Not.Null);
        using (var handle = provider!.GetHandle()) { }
        provider = null;
        while (watch.ElapsedMilliseconds < 2000 && provider is null)
        { provider = playback.GrabAudio(256, 44100, 1, false); if (provider is null) Thread.Sleep(2); }
        Assert.That(provider, Is.Not.Null);
        using var changed = provider!.GetHandle();
        Assert.That(changed.Resource.Timecode.TotalSeconds, Is.LessThan(watch.Elapsed.TotalSeconds + .050));
        Assert.That(playback.Metrics.ContainerOpens, Is.EqualTo(1));
    }

    [Test]
    public void LoopReusesContainerAndKeepsRealtimeAcrossBoundary()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), true);
        source.SetLoop(true);
        source.Seek(6.45);
        var clock = new ManualClock();
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var last = WaitFrame(session);
        clock.Time = .2;
        var watch = Stopwatch.StartNew();
        var wrapped = false;
        while (watch.ElapsedMilliseconds < 3000 && !wrapped)
        {
            var provider = session.GrabVideoFrame();
            if (provider is not null)
            {
                using var handle = provider.GetHandle();
                wrapped = handle.Resource.Timecode.TotalSeconds < .2;
            }
            Thread.Sleep(2);
        }
        Assert.That(wrapped, Is.True, source.Status.Message);
        Assert.That(source.GetPlayback().Metrics.ContainerOpens, Is.EqualTo(1));
    }

    [Test]
    public void RealtimeJumpDiscardsStaleFramesBeforeConversion()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), true);
        var clock = new ManualClock();
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(session);
        Thread.Sleep(100); // Let the bounded future queues fill before the deliberate clock jump.
        clock.Time = 2;
        var watch = Stopwatch.StartNew();
        var latest = 0d;
        while (watch.ElapsedMilliseconds < 3000 && latest < 1.95)
        {
            var provider = session.GrabVideoFrame();
            if (provider is not null) { using var handle = provider.GetHandle(); latest = handle.Resource.Timecode.TotalSeconds; }
            Thread.Sleep(2);
        }
        Assert.That(latest, Is.GreaterThanOrEqualTo(1.95), source.Status.Message);
        Assert.That(source.GetPlayback().Metrics.DroppedBeforeConversion, Is.GreaterThan(0));
    }

    [Test]
    public void WarmCpuFramesDoNotAllocateFullPixelArrays()
    {
        using var decoder = new FFmpegVideoDecoder(MediaFixtures.Video(), TimeSpan.Zero, CancellationToken.None, MediaFixtures.Runtime);
        var count = 0;
        long before = 0;
        decoder.Decode(frame =>
        {
            using (var handle = frame.CreateProvider().GetHandle()) { }
            frame.Dispose();
            count++;
            if (count == 8) before = GC.GetAllocatedBytesForCurrentThread();
            return count < 48;
        });
        Assert.That(count, Is.EqualTo(48));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(200_000));
    }

    [Test]
    public void ClockPastEndDoesNotConvertExpiredFrames()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), true);
        var clock = new ManualClock();
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(session);
        Thread.Sleep(100);
        var before = source.GetPlayback().Metrics.Conversions;
        clock.Time = 100;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 3000 && !source.Status.IsEnded)
        {
            var p = session.GrabVideoFrame();
            if (p is not null) { using var h = p.GetHandle(); }
            Thread.Sleep(2);
        }
        Assert.That(source.Status.IsEnded, Is.True);
        Assert.That(source.GetPlayback().Metrics.Conversions - before, Is.LessThanOrEqualTo(1),
            "No future frame can be shown; only an already running conversion may complete.");
    }

    [Test]
    public void AudioDemandDoesNotWaitForVideoPoolRetirement()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(MediaFixtures.Video(), true);
        using var session = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        using var first = WaitFrame(session);
        Thread.Sleep(100);
        var playback = source.GetPlayback();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = playback.GetType().GetField("_gate", flags)!.GetValue(playback)!;
        object? owner = null;
        lock (gate)
        {
            var queue = (System.Collections.IEnumerable)playback.GetType().GetField("_ready", flags)!.GetValue(playback)!;
            foreach (var queued in queue)
            {
                var frame = queued.GetType().GetProperty("Frame")!.GetValue(queued)!;
                var lease = frame.GetType().GetField("_lease", flags)?.GetValue(frame);
                if (lease is not null) { owner = lease.GetType().GetField("_owner", flags)!.GetValue(lease); break; }
            }
        }
        Assert.That(owner, Is.Not.Null);
        var poolGate = owner!.GetType().GetField("_gate", flags)!.GetValue(owner)!;
        Task<VL.Lib.Basics.Resources.IResourceProvider<VL.Lib.Basics.Audio.AudioFrame>?> pull;
        bool returned;
        lock (poolGate)
        {
            pull = Task.Run(() => playback.GrabAudio(512, 48000, 2, false));
            returned = pull.Wait(300);
        }
        if (pull.GetAwaiter().GetResult() is { } provider) { using var handle = provider.GetHandle(); }
        Assert.That(returned, Is.True, "An audio callback must not retire video resources or wait for their producer.");
    }

    [Test]
    public void AudioOnlyPublishesEndedWithoutAVideoConsumer()
    {
        using var fixture = MediaFixtures.Wave(44100, .05);
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(fixture.Path, true);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1500 && !source.Status.IsEnded)
        {
            var p = source.GetPlayback().GrabAudio(512, 48000, 1, false);
            if (p is not null) { using var h = p.GetHandle(); }
            Thread.Sleep(5);
        }
        Assert.That(source.Status.IsEnded, Is.True, source.Status.Message);
    }

    [Test]
    public unsafe void AudioLoopRetainsCompatibleResamplerAndDrainsEachCycle()
    {
        using var fixture = MediaFixtures.Wave(44100, 1);
        using var demux = new FFmpegDemuxContext(fixture.Path, CancellationToken.None, MediaFixtures.Runtime);
        using var decoder = new FFmpegAudioDecoder(fixture.Path, TimeSpan.Zero, 48000, 1, CancellationToken.None, demux: demux);
        var count = 0;
        decoder.Decode(frame => { count += frame.SampleCount; return true; });
        var field = typeof(FFmpegAudioDecoder).GetField("_swrContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var context = (nint)System.Reflection.Pointer.Unbox(field.GetValue(decoder)!);
        demux.Seek(TimeSpan.Zero);
        decoder.Flush(TimeSpan.Zero, 48000, 1, CancellationToken.None);
        Assert.That((nint)System.Reflection.Pointer.Unbox(field.GetValue(decoder)!), Is.EqualTo(context));
        decoder.Decode(frame => { count += frame.SampleCount; return true; });
        Assert.That(count, Is.EqualTo(96000));
    }

    [Test]
    public void ConsecutiveAudioPullsAdvanceBySamplesDespiteCallbackJitter()
    {
        using var fixture = MediaFixtures.Wave(44100, 1);
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        source.Open(fixture.Path, true);
        var playback = source.GetPlayback();
        VL.Lib.Basics.Resources.IResourceProvider<VL.Lib.Basics.Audio.AudioFrame>? first = null;
        var watch = Stopwatch.StartNew();
        while (first is null && watch.ElapsedMilliseconds < 1000)
        { first = playback.GrabAudio(512, 48000, 1, false); if (first is null) Thread.Sleep(2); }
        Assert.That(first, Is.Not.Null);
        using var a = first!.GetHandle();
        using var b = playback.GrabAudio(512, 48000, 1, false)!.GetHandle();
        Assert.That((b.Resource.Timecode - a.Resource.Timecode).TotalSeconds, Is.EqualTo(512d / 48000).Within(.000001));
    }

    [Test]
    public void PlayingDoesNotPublishAFutureFirstFrameBeforeItsStreamOffset()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var playback = source.GetPlayback();
        playback.Attach(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(PlaybackSession);
        var gate = type.GetField("_gate", flags)!.GetValue(playback)!;
        lock (gate)
        {
            var generation = (long)type.GetField("_generation", flags)!.GetValue(playback)!;
            var queue = type.GetField("_ready", flags)!.GetValue(playback)!;
            var frame = new CpuDecodedVideoFrame(new byte[16], 2, 2, TimeSpan.FromSeconds(5), (25, 1), "offset", false);
            var ready = Activator.CreateInstance(type.GetNestedType("ReadyFrame", System.Reflection.BindingFlags.NonPublic)!, generation, 0L, 5d, frame);
            queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [ready]);
            ((MasterClock)type.GetField("_clock", flags)!.GetValue(playback)!).Ready();
        }
        var provider = playback.GrabVideoFrame();
        if (provider is not null) { using var handle = provider.GetHandle(); }
        Assert.That(provider, Is.Null, "An initial preview must not erase the video stream's positive offset during Play.");
    }

    private static VL.Lib.Basics.Resources.IResourceHandle<VideoFrame> WaitFrame(VL.Lib.Basics.Video.IVideoPlayer session)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 3000)
        {
            var provider = session.GrabVideoFrame();
            if (provider is not null) return provider.GetHandle();
            Thread.Sleep(2);
        }
        Assert.Fail("No video frame was delivered.");
        return null!;
    }
}

internal sealed class ManualClock : IFrameClock
{
    public Time Time { get; set; }
    public double TimeDifference => 0;
    public IObservable<FrameTimeMessage> GetTicks() => Observable.Never<FrameTimeMessage>();
    public IObservable<FrameFinishedMessage> GetFrameFinished() => Observable.Never<FrameFinishedMessage>();
}

internal static class MediaFixtures
{
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Directory.Build.props")))
                directory = directory.Parent;
            return directory!.FullName;
        }
    }
    internal static string Runtime => System.IO.Path.Combine(Root, "runtimes", "win-x64", "native");
    internal static string Video()
    {
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", Runtime);
        if (!Directory.Exists(@"C:\Program Files\vvvv")) Assert.Ignore("Gamma reference clip is unavailable.");
        var file = Directory.GetDirectories(@"C:\Program Files\vvvv", "vvvv_gamma_*")
            .OrderDescending().Select(p => System.IO.Path.Combine(p, "packs", "VL.Video", "help", "Birds_H264.mp4"))
            .FirstOrDefault(File.Exists);
        if (file is null) Assert.Ignore("Gamma reference clip is unavailable.");
        return file!;
    }
    internal static TemporaryMedia Wave(int rate, double seconds)
    {
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", Runtime);
        var media = new TemporaryMedia(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vl-ffmpeg-{Guid.NewGuid():N}.wav"));
        using var writer = new BinaryWriter(File.Create(media.Path));
        var samples = checked((int)Math.Round(rate * seconds));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 10000));
        return media;
    }
}

internal sealed record TemporaryMedia(string Path) : IDisposable
{
    public void Dispose() => File.Delete(Path);
}
