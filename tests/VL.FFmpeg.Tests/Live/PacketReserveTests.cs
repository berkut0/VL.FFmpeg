using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests.Live;

public sealed class PacketReserveTests
{
    [Test]
    public async Task PacketReserveHonorsCountAndBytesAndKeepsDequeuedMemoryCharged()
    {
        Assert.That(FFmpegRuntime.Probe(MediaFixtures.Runtime).Available, Is.True);
        var baseline = ResourceBudget.Cpu.Used;
        using var queue = new PacketQueue(packetCapacity: 2, byteCapacity: 16);
        using var large = Packet(12);
        using var a = Packet(8);
        using var b = Packet(4);
        using var c = Packet(4);
        Assert.That(queue.TryWrite(large), Is.True);
        Assert.That(queue.TryWrite(a), Is.False, "The byte cap applies independently of packet count.");
        using (var dequeued = await queue.Read(CancellationToken.None))
            Assert.That(ResourceBudget.Cpu.Used, Is.EqualTo(baseline + 12), "A dequeued packet is still owned by its decoder.");
        Assert.That(queue.TryWrite(a), Is.True);
        Assert.That(queue.TryWrite(b), Is.True);
        Assert.That(queue.TryWrite(c), Is.False, "The count cap applies even when bytes remain.");
        queue.Clear();
        Assert.That(ResourceBudget.Cpu.Used, Is.EqualTo(baseline));
    }

    private static unsafe NativePacket Packet(int bytes)
    {
        var packet = ffmpeg.av_packet_alloc();
        if (packet is null) throw new OutOfMemoryException();
        packet->size = bytes;
        return new NativePacket(packet);
    }
}
