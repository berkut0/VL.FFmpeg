using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Tests;

public sealed class VideoDecoderSlotTests
{
    [Test]
    public async Task CancelledReplacementLeavesNoDisposedDecoderToReuse()
    {
        using var slot = new VideoDecoderSlot();
        await slot.Replace(() => Task.FromResult(CreateSoftware()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>((Func<Task>)(() =>
            slot.Replace(() => Task.FromCanceled<FFmpegVideoDecoder>(cancellation.Token))));
        Assert.That(slot.Value, Is.Null);
        await slot.Replace(() => Task.FromResult(CreateSoftware()));
        slot.Value!.Flush(TimeSpan.Zero, CancellationToken.None);
        var count = 0;
        slot.Value.Decode(frame => { frame.Dispose(); count++; return false; });
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void AutoRetriesSoftwareWhenHardwareConstructionFails()
    {
        var attempts = new List<DecodeMode>();
        using var decoder = VideoDecoderSlot.Open(DecodeMode.Auto, mode =>
        {
            attempts.Add(mode);
            if (mode == DecodeMode.Auto) throw new FFmpegHardwareException("Hardware codec open failed.");
            return CreateSoftware();
        }, _ => { });
        Assert.That(attempts, Is.EqualTo(new[] { DecodeMode.Auto, DecodeMode.Software }));
        var count = 0;
        decoder.Decode(frame => { frame.Dispose(); count++; return false; });
        Assert.That(count, Is.EqualTo(1));
    }

    private static FFmpegVideoDecoder CreateSoftware()
        => new(MediaFixtures.Video(), TimeSpan.Zero, CancellationToken.None, MediaFixtures.Runtime);
}
