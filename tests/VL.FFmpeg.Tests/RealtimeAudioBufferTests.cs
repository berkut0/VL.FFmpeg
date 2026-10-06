using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;

namespace VL.FFmpeg.Tests;

public sealed class RealtimeAudioBufferTests
{
    [Test]
    public void UnderrunPadsWithSilenceAndLateSamplesAreNotReplayed()
    {
        using var buffer = new AudioSampleBuffer(100, 1, 25);
        buffer.Write(new DecodedAudioFrame([1, 2], 1, 2, 0, 100, TimeSpan.Zero), CancellationToken.None);
        using var first = buffer.ReadAt(4, false, TimeSpan.Zero).GetHandle();
        Assert.That(first.Resource.GetChannel(0).ToArray(), Is.EqualTo(new float[] { 1, 2, 0, 0 }));
        buffer.Write(new DecodedAudioFrame([3, 4, 5, 6], 1, 4, 0, 100, TimeSpan.FromSeconds(.02)), CancellationToken.None);
        using var second = buffer.ReadAt(4, false, TimeSpan.FromSeconds(.04)).GetHandle();
        Assert.That(second.Resource.GetChannel(0).ToArray(), Is.EqualTo(new float[] { 5, 6, 0, 0 }));
    }

    [Test]
    public async Task FutureBlockWaitsForSpaceInsteadOfBeingDiscarded()
    {
        using var buffer = new AudioSampleBuffer(100, 1, 25);
        buffer.Write(new DecodedAudioFrame(Enumerable.Repeat(1f, 20).ToArray(), 1, 20, 0, 100, TimeSpan.Zero), CancellationToken.None);
        var pending = buffer.WriteAsync(new DecodedAudioFrame(Enumerable.Repeat(2f, 10).ToArray(), 1, 10, 0, 100,
            TimeSpan.FromSeconds(.2)), () => 0d, CancellationToken.None).AsTask();
        Assert.That(pending.IsCompleted, Is.False);
        using (var first = buffer.ReadAt(10, false, TimeSpan.Zero).GetHandle()) { }
        Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
        using var rest = buffer.ReadAt(20, false, TimeSpan.FromSeconds(.1)).GetHandle();
        Assert.That(rest.Resource.GetChannel(0).ToArray(), Is.EqualTo(new float[]
            {1,1,1,1,1,1,1,1,1,1,2,2,2,2,2,2,2,2,2,2}));
    }

    [Test]
    public void AudioMemoryRemainsChargedUntilConsumerReleasesItsHandle()
    {
        var budget = new ResourceBudget(1024);
        using var buffer = new AudioSampleBuffer(100, 1, 25, budget);
        buffer.Write(new DecodedAudioFrame([1, 2], 1, 2, 0, 100, TimeSpan.Zero), CancellationToken.None);
        Assert.That(budget.Used, Is.EqualTo(8));
        var handle = buffer.ReadAt(2, false, TimeSpan.Zero).GetHandle();
        buffer.Dispose();
        Assert.That(budget.Used, Is.GreaterThanOrEqualTo(8));
        handle.Dispose();
        Assert.That(budget.Used, Is.Zero);
    }
}
