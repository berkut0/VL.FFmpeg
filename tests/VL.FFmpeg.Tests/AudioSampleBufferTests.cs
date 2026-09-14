using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;

namespace VL.FFmpeg.Tests;

public sealed class AudioSampleBufferTests
{
    [Test]
    public void ReadsAnExactPlanarBlockWithTimecode()
    {
        using var buffer = new AudioSampleBuffer(sampleRate: 48_000, channelCount: 2, capacity: 8);
        buffer.Write(
            new DecodedAudioFrame(
                Samples: [1f, 2f, 3f, 4f, 10f, 20f, 30f, 40f],
                ChannelCount: 2,
                SampleCount: 4,
                SampleOffset: 0,
                SampleRate: 48_000,
                Timecode: TimeSpan.FromSeconds(2d)),
            CancellationToken.None);

        var provider = buffer.TryRead(sampleCount: 4, interleaved: false);
        Assert.That(provider, Is.Not.Null);
        using var handle = provider!.GetHandle();
        var frame = handle.Resource;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.IsPlanar, Is.True);
            Assert.That(frame.ChannelCount, Is.EqualTo(2));
            Assert.That(frame.SampleCount, Is.EqualTo(4));
            Assert.That(frame.Timecode, Is.EqualTo(TimeSpan.FromSeconds(2d)));
            Assert.That(frame.GetChannel(0).ToArray(), Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
            Assert.That(frame.GetChannel(1).ToArray(), Is.EqualTo(new[] { 10f, 20f, 30f, 40f }));
        }
    }

    [Test]
    public void InsufficientSamplesReturnImmediatelyWithoutConsumption()
    {
        using var buffer = new AudioSampleBuffer(sampleRate: 48_000, channelCount: 1, capacity: 8);
        buffer.Write(
            new DecodedAudioFrame(
                Samples: [1f, 2f],
                ChannelCount: 1,
                SampleCount: 2,
                SampleOffset: 0,
                SampleRate: 48_000,
                Timecode: TimeSpan.Zero),
            CancellationToken.None);

        Assert.That(buffer.TryRead(sampleCount: 4, interleaved: false), Is.Null);
        var provider = buffer.TryRead(sampleCount: 2, interleaved: true);
        Assert.That(provider, Is.Not.Null);
        using var handle = provider!.GetHandle();
        Assert.That(handle.Resource.IsInterleaved, Is.True);
        Assert.That(handle.Resource.GetSamples(0).ToArray(), Is.EqualTo(new[] { 1f }));
        Assert.That(handle.Resource.GetSamples(1).ToArray(), Is.EqualTo(new[] { 2f }));
    }
}
