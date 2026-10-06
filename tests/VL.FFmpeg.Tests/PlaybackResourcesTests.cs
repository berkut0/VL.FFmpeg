using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackResourcesTests
{
    [Test]
    public void LeasedBuffersCannotBeReusedUntilReleased()
    {
        var budget = new ResourceBudget(32);
        using var pool = new CpuFramePool(budget);
        var first = pool.TryRent(16)!;
        var original = first.Buffer;
        original[0] = 79;
        var second = pool.TryRent(16)!;
        Assert.That(second.Buffer, Is.Not.SameAs(original));
        Assert.That(pool.TryRent(16), Is.Null);
        first.Dispose();
        using var third = pool.TryRent(16)!;
        Assert.That(third.Buffer, Is.SameAs(original));
        Assert.That(third.Buffer[0], Is.EqualTo(79));
        pool.Dispose();
        Assert.That(budget.Used, Is.EqualTo(32));
        second.Dispose();
        third.Dispose();
        Assert.That(budget.Used, Is.Zero);
    }

    [Test]
    public void ClockWaitsForFirstFrameThenAdvancesAcrossUnderrun()
    {
        var clock = new MasterClock();
        clock.Reset(0, true, waitForReady: true);
        Assert.That(clock.Observe(100), Is.Zero);
        Assert.That(clock.Observe(102), Is.Zero);
        clock.Ready();
        Assert.That(clock.Observe(103), Is.Zero);
        Assert.That(clock.Observe(105), Is.EqualTo(2));
        clock.SetPlay(false);
        var paused = clock.Observe(106);
        Assert.That(clock.Observe(200), Is.EqualTo(paused));
        clock.SetPlay(true);
        Assert.That(clock.Observe(201), Is.EqualTo(paused));
        Assert.That(clock.Observe(202), Is.EqualTo(paused + 1));
    }

    [Test]
    public void AnotherPlayerCanReclaimIdleBuffersButNotConsumerLeases()
    {
        var budget = new ResourceBudget(32);
        using var first = new CpuFramePool(budget);
        using var second = new CpuFramePool(budget);
        using var held = first.TryRent(16)!;
        first.TryRent(16)!.Dispose();
        using var acquired = second.TryRent(16);
        Assert.That(acquired, Is.Not.Null, "Idle storage must not permanently starve another player.");
        Assert.That(second.TryRent(16), Is.Null, "The outstanding consumer leases still own their memory.");
    }

    [Test]
    public void AttachingFrameClockDoesNotRewindAudioTime()
    {
        var clock = new MasterClock();
        clock.Reset(0, true, waitForReady: false);
        Thread.Sleep(30);
        var previous = clock.Position;
        Assert.That(clock.Observe(100), Is.GreaterThanOrEqualTo(previous));
    }

    [Test]
    public void ACodecWithoutThreadingDoesNotReserveMultipleCpuWorkers()
    {
        using var file = new TemporaryMedia(Path.Combine(Path.GetTempPath(), $"vl-ffmpeg-{Guid.NewGuid():N}.y4m"));
        using (var stream = File.Create(file.Path))
        {
            stream.Write("YUV4MPEG2 W64 H32 F60:1 Ip A1:1 C420jpeg\nFRAME\n"u8);
            stream.Write(new byte[64 * 32 * 3 / 2]);
        }
        using var decoder = new FFmpegVideoDecoder(file.Path, TimeSpan.Zero, CancellationToken.None, MediaFixtures.Runtime);
        Assert.That(decoder.ThreadCount, Is.EqualTo(1), "Rawvideo has no codec workers to reserve.");
    }

    [Test]
    public void PixelLeaseWaitsForTheLastResourceHandle()
    {
        var budget = new ResourceBudget(16);
        using var pool = new CpuFramePool(budget);
        var lease = pool.TryRent(16)!;
        using var frame = new CpuDecodedVideoFrame(lease.Buffer, 2, 2, TimeSpan.Zero, (25, 1), "test", false, lease);
        var provider = frame.CreateProvider();
        var first = provider.GetHandle();
        var second = provider.GetHandle();
        first.Dispose();
        try { Assert.That(pool.TryRent(16), Is.Null, "A second consumer still owns the pixels."); }
        finally { second.Dispose(); }
        using var reusable = pool.TryRent(16);
        Assert.That(reusable, Is.Not.Null);
    }

    [TestCase(1.170, false)]
    [TestCase(1.172, true)]
    [TestCase(100, true)]
    public void DeadlineAllowsBoundedJitterButRejectsStaleContent(double now, bool expired)
        => Assert.That(VideoFrameDeadline.IsExpired(1, .020, now), Is.EqualTo(expired));
}
