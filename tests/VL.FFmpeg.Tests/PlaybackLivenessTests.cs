using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackLivenessTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task PauseKeepsTheShownImageWhenALateFrameArrives()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        engine.Attach(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance));
        Get<MasterClock>(engine, "_clock").Reset(3, true, waitForReady: false);
        using var demux = new FFmpegDemuxContext(MediaFixtures.Video(), CancellationToken.None, MediaFixtures.Runtime);
        using var decoder = new FFmpegVideoDecoder(demux, CancellationToken.None);

        await ConvertOne(engine, source, decoder, 0);
        using var first = engine.GrabVideoFrame()!.GetHandle();
        source.Pause();
        await ConvertOne(engine, source, decoder, .040);

        var paused = engine.GrabVideoFrame();
        if (paused is not null) { using var unexpected = paused.GetHandle(); }
        Assert.That(paused, Is.Null, "Pause must freeze the shown image, even while late work catches up to the frozen clock.");
        Assert.That(source.Status.Position, Is.Zero);

        source.Play();
        using var resumed = engine.GrabVideoFrame()!.GetHandle();
        Assert.That(resumed.Resource.Timecode.TotalSeconds, Is.EqualTo(.040));
    }

    [Test]
    public void ManualSeekClearsThePreviousRealtimeRecoveryMessage()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        var clock = new ManualClock { Time = 2 };
        engine.Attach(new VideoPlaybackContext(clock, NullLogger.Instance));
        Get<MasterClock>(engine, "_clock").Reset(2, true, waitForReady: false);
        Set(engine, "_presented", true);
        Set(engine, "_duration", 100d);
        engine.GrabVideoFrame();
        clock.Time = 2.6;
        engine.GrabVideoFrame();
        engine.GrabVideoFrame();
        Assert.That(source.Status.Message, Does.Contain("Recovering to realtime"));

        source.Seek(.5);
        engine.GrabVideoFrame();
        Assert.That(source.Status.Message, Does.Not.Contain("Recovering to realtime"),
            "An explicit seek replaces the recovery request, including its diagnostic state.");
    }

    [Test]
    public async Task SlowProducerPublishesUsefulFramesWithoutAnArtificialCadenceLimit()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        engine.Attach(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance));
        Get<MasterClock>(engine, "_clock").Reset(3, true, waitForReady: false);
        var filename = Path.Combine(MediaFixtures.Root, "tests", "VL.FFmpeg.Tests", "TestData", "vp9-alpha.webm");
        using var demux = new FFmpegDemuxContext(filename, CancellationToken.None, MediaFixtures.Runtime);
        using var decoder = new FFmpegVideoDecoder(demux, CancellationToken.None);

        await ConvertOne(engine, source, decoder, 0);
        var first = engine.GrabVideoFrame();
        Assert.That(first, Is.Not.Null, "A late first image after seek/recovery must not be discarded forever.");
        using (var handle = first!.GetHandle()) { }

        await ConvertOne(engine, source, decoder, .020);
        var second = engine.GrabVideoFrame();
        Assert.That(second, Is.Not.Null, "The only newer image remains useful even when both images lag the clock.");
        using var secondHandle = second!.GetHandle();

        await ConvertOne(engine, source, decoder, .040);
        var next = engine.GrabVideoFrame();
        Assert.That(next, Is.Not.Null, "Persistent producer overload must still yield a newer image periodically.");
        using var nextHandle = next!.GetHandle();
        Assert.That(nextHandle.Resource.Timecode.TotalSeconds, Is.EqualTo(.040));

        var conversions = engine.Metrics.Conversions;
        await ConvertOne(engine, source, decoder, .010);
        Assert.That(engine.Metrics.Conversions, Is.EqualTo(conversions), "An image older than the displayed one must not start conversion.");
        Assert.That(engine.GrabVideoFrame(), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WaitingForWorkersRechecksCandidateAndGenerationBeforeConversion(bool seekWhileWaiting)
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        engine.Attach(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance));
        Get<MasterClock>(engine, "_clock").Reset(3, true, waitForReady: false);
        using var demux = new FFmpegDemuxContext(MediaFixtures.Video(), CancellationToken.None, MediaFixtures.Runtime);
        using var decoder = new FFmpegVideoDecoder(demux, CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var busy = PlaybackWork.Run(() => { entered.Set(); release.Wait(); return true; }, CancellationToken.None, int.MaxValue);
        var channel = Channel.CreateBounded<NativeVideoFrame>(2);
        Task? conversion = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(3)), Is.True);
            channel.Writer.TryWrite(Frame(0));
            conversion = StartConversion(engine, source, decoder, channel.Reader);
            Assert.That(conversion.IsCompleted, Is.False);
            if (seekWhileWaiting) source.Seek(.5);
            else channel.Writer.TryWrite(Frame(.020));
            channel.Writer.Complete();
        }
        finally
        {
            channel.Writer.TryComplete();
            release.Set();
            await busy;
            if (conversion is not null) await conversion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        if (seekWhileWaiting)
        {
            Assert.That(engine.Metrics.Conversions, Is.Zero, "A superseded generation must not start conversion after admission.");
            Assert.That(engine.GrabVideoFrame(), Is.Null);
            return;
        }
        Assert.That(engine.Metrics.Conversions, Is.EqualTo(1), "Replace a superseded candidate after admission, before allocating output storage.");
        using var shown = engine.GrabVideoFrame()!.GetHandle();
        Assert.That(shown.Resource.Timecode.TotalSeconds, Is.EqualTo(.020));
    }

    [Test]
    public void AutomaticRecoveryRetainsTheDisplayedImageAndCanRetry()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        var clock = new ManualClock { Time = 2 };
        engine.Attach(new VideoPlaybackContext(clock, NullLogger.Instance));
        Get<MasterClock>(engine, "_clock").Reset(2, true, waitForReady: false);
        Set(engine, "_presented", true);
        Set(engine, "_presentedTimeline", 0d);
        Set(engine, "_duration", 100d);
        var generation = Get<long>(engine, "_generation");
        engine.GrabVideoFrame();
        clock.Time = 2.6;
        engine.GrabVideoFrame();
        Assert.That(Get<long>(engine, "_generation"), Is.GreaterThan(generation), "Sustained lag must actually request recovery.");
        Assert.That(Get<bool>(engine, "_presented"), Is.True,
            "Recovery must retain image availability; otherwise it disables recovery and reports permanent Buffering.");
    }

    private static async Task ConvertOne(PlaybackSession engine, VideoPlayerSource source, FFmpegVideoDecoder decoder, double time)
    {
        var channel = Channel.CreateBounded<NativeVideoFrame>(2);
        channel.Writer.TryWrite(Frame(time));
        channel.Writer.Complete();
        await StartConversion(engine, source, decoder, channel.Reader).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static Task StartConversion(PlaybackSession engine, VideoPlayerSource source, FFmpegVideoDecoder decoder,
        ChannelReader<NativeVideoFrame> reader)
    {
        var requestType = typeof(PlaybackSession).GetNestedType("Request", BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(requestType, Get<long>(engine, "_generation"), source.Options,
            0d, Get<object>(engine, "_binding"), null, false)!;
        return (Task)typeof(PlaybackSession).GetMethod("ConvertVideo", Private)!.Invoke(engine,
            [request, decoder, reader, 0d, CancellationToken.None])!;
    }

    private static unsafe NativeVideoFrame Frame(double time)
    {
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->width = 2; frame->height = 2;
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            Assert.That(ffmpeg.av_frame_get_buffer(frame, 32), Is.Zero);
            new Span<byte>(frame->data[0], frame->linesize[0] * 2).Clear();
            return new NativeVideoFrame(frame, time, .020);
        }
        finally { ffmpeg.av_frame_free(&frame); }
    }

    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
}
