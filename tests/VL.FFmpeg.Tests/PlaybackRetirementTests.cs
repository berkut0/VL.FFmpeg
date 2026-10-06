using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackRetirementTests
{
    [Test]
    public void SupersededFallbackCannotPublishBeforeCancellationNotification()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(PlaybackSession);
        var gate = type.GetField("_gate", flags)!.GetValue(engine)!;
        lock (gate)
        {
            var generation = (long)type.GetField("_generation", flags)!.GetValue(engine)!;
            var request = Activator.CreateInstance(type.GetNestedType("Request", BindingFlags.NonPublic)!,
                generation - 1, source.Options, 0d, null, null, false);
            var accepted = (bool)type.GetMethod("BeginSoftwareFallback", flags)!.Invoke(engine,
                [request, null, "retired decoder", CancellationToken.None])!;
            Assert.That(accepted, Is.False, "Generation ownership must not depend on cancellation callback timing.");
            Assert.That(type.GetField("_recoveryMessage", flags)!.GetValue(engine), Is.Null);
        }
    }

    [Test]
    public void SeekDoesNotWaitForConsumerResourceCleanup()
    {
        using var source = new VideoPlayerSource(FFmpegPlayerSessionFactory.Instance);
        var engine = source.GetPlayback();
        engine.Attach(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance));
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(PlaybackSession);
        lock (type.GetField("_gate", flags)!.GetValue(engine)!)
        {
            var queue = type.GetField("_ready", flags)!.GetValue(engine)!;
            var generation = (long)type.GetField("_generation", flags)!.GetValue(engine)!;
            var ready = Activator.CreateInstance(type.GetNestedType("ReadyFrame", BindingFlags.NonPublic)!,
                generation, 0L, 1d, new BlockingFrame(entered, release));
            queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [ready]);
        }
        var seek = Task.Run(() => source.Seek(1));
        bool seekReturned;
        bool pullReturned = false;
        Task? pull = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            seekReturned = seek.Wait(200);
            pull = Task.Run(() => engine.GrabVideoFrame());
            pullReturned = pull.Wait(200);
        }
        finally
        {
            release.Set();
            seek.GetAwaiter().GetResult();
            pull?.GetAwaiter().GetResult();
        }
        Assert.That(seekReturned, Is.True, "The command caller must not wait for native resource cleanup.");
        Assert.That(pullReturned, Is.True, "Cleanup must not hold the presentation lock.");
    }

    private sealed class BlockingFrame(ManualResetEventSlim entered, ManualResetEventSlim release)
        : DecodedVideoFrame(1, 1, TimeSpan.Zero, (25, 1), DecodePath.Software, "retirement")
    {
        public override IResourceProvider<VideoFrame> CreateProvider() => throw new InvalidOperationException();
        public override void Dispose() { entered.Set(); release.Wait(); }
    }
}
