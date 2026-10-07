using NUnit.Framework;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests;

public sealed unsafe class NotchLcAlphaTests
{
    [Test]
    [Explicit("Known failure in the pinned native FFmpeg runtime; run when validating a NotchLC decoder update. See docs/ARCHITECTURE.md#alpha.")]
    public void NativeDecoderPreservesAlphaBlockSamples()
    {
        var runtime = FFmpegRuntime.Probe(Path.Combine(MediaFixtures.Root, "runtimes", "win-x64", "native"));
        Assert.That(runtime.Available, Is.True, runtime.Status);
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_NOTCHLC);
        Assert.That((nint)codec, Is.Not.EqualTo(nint.Zero));
        var context = ffmpeg.avcodec_alloc_context3(codec);
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            Assert.That((nint)context, Is.Not.EqualTo(nint.Zero));
            Assert.That((nint)packet, Is.Not.EqualTo(nint.Zero));
            Assert.That((nint)frame, Is.Not.EqualTo(nint.Zero));
            context->thread_count = 1;
            Assert.That(ffmpeg.avcodec_open2(context, codec, null), Is.Zero);
            var data = CreatePacket();
            Assert.That(ffmpeg.av_new_packet(packet, data.Length), Is.Zero);
            data.CopyTo(new Span<byte>(packet->data, data.Length));
            Assert.That(ffmpeg.avcodec_send_packet(context, packet), Is.Zero);
            Assert.That(ffmpeg.avcodec_receive_frame(context, frame), Is.Zero);
            Assert.That((AVPixelFormat)frame->format, Is.EqualTo(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE));
            Assert.That(frame->width, Is.EqualTo(16));
            Assert.That(frame->height, Is.EqualTo(16));

            var actual = new ushort[256];
            var expected = new ushort[256];
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var block = y / 4 * 4 + x / 4;
                var index = (y % 4 * 4 + x % 4) % 8;
                var low = 16 + block;
                var high = 240 - block;
                actual[y * 16 + x] = ((ushort*)(frame->data[3] + y * frame->linesize[3]))[x];
                expected[y * 16 + x] = (ushort)((low + ((high - low) * index + 6) / 7) << 4);
                Assert.That(((ushort*)(frame->data[0] + y * frame->linesize[0]))[x], Is.EqualTo(2048),
                    "The packet's constant luma must decode correctly independently of alpha.");
            }
            TestContext.WriteLine($"First row: {string.Join(", ", actual.Take(16))}");
            TestContext.WriteLine($"Samples outside 12-bit range: {actual.Count(value => value > 4095)}");
            Assert.That(actual, Is.EqualTo(expected));
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&context);
        }
    }

    // One 16x16 tile, constant luma/chroma and sixteen independently encoded
    // alpha ramps. Literal-only LZ4 avoids dependence on an external encoder.
    // Layout: FFmpeg notchlc.c; alpha correction: upstream PR #23791.
    private static byte[] CreatePacket()
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(16u); writer.Write(16u);
            writer.Write(128u / 4); // UV offset table
            writer.Write(56u / 4);  // luma controls
            writer.Write(120u / 4); // alpha controls
            writer.Write(132u / 4); // UV data
            writer.Write(32u);      // luma data byte count
            writer.Write(12u / 4);  // alpha data relative to UV data
            writer.Write(0u);
            writer.Write(304u);     // payload end
            for (var row = 0; row < 4; row++) writer.Write((uint)(row * 8));
            for (var block = 0; block < 16; block++) writer.Write(2048u | (2048u << 12));
            writer.Write(0xaaaaaaaau); // every alpha block uses a ramp
            writer.Write(0u);          // first alpha data block
            writer.Write(0u);          // first UV data block
            writer.Write(0u);          // uniform chroma mode
            writer.Write(0x80808080u); // neutral chroma endpoints
            writer.Write(0u);          // chroma selectors
            for (var block = 0; block < 16; block++)
            {
                ulong alpha = (uint)(16 + block) | ((ulong)(240 - block) << 8);
                for (var pixel = 0; pixel < 16; pixel++)
                    alpha |= (ulong)(pixel % 8) << (16 + pixel * 3);
                writer.Write(alpha);
            }
            writer.Write(new byte[32]); // constant luma selectors
        }
        Assert.That(payload.Length, Is.EqualTo(304));
        using var output = new MemoryStream();
        using var packet = new BinaryWriter(output);
        packet.Write(0x4e4c4331u); // NLC1 magic, little-endian word
        packet.Write((uint)payload.Length);
        packet.Write((uint)payload.Length + 3);
        packet.Write(1u); // LZ4
        packet.Write((byte)0xf0);
        packet.Write((byte)255);
        packet.Write((byte)(304 - 15 - 255));
        packet.Write(payload.ToArray());
        return output.ToArray();
    }
}
