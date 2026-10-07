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
    public async Task SlowProducerStillDeliversImagesWithoutConvertingEveryExpiredFrame()
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
        Assert.That(engine.GrabVideoFrame(), Is.Null, "Do not waste conversion on every stale frame while an image was just delivered.");

        Set(engine, "_lastPresentedStamp", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        await ConvertOne(engine, source, decoder, .040);
        var next = engine.GrabVideoFrame();
        Assert.That(next, Is.Not.Null, "Persistent producer overload must still yield a newer image periodically.");
        using var nextHandle = next!.GetHandle();
        Assert.That(nextHandle.Resource.Timecode.TotalSeconds, Is.EqualTo(.040));
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
        var requestType = typeof(PlaybackSession).GetNestedType("Request", BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(requestType, Get<long>(engine, "_generation"), source.Options,
            0d, Get<object>(engine, "_binding"), null, false)!;
        var channel = Channel.CreateBounded<NativeVideoFrame>(2);
        channel.Writer.TryWrite(Frame(time));
        channel.Writer.Complete();
        var task = (Task)typeof(PlaybackSession).GetMethod("ConvertVideo", Private)!.Invoke(engine,
            [request, decoder, channel.Reader, 0d, CancellationToken.None])!;
        await task.WaitAsync(TimeSpan.FromSeconds(3));
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
