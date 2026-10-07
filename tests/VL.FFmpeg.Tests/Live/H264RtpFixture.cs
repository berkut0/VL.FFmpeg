using System.Buffers.Binary;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests.Live;

/// <summary>Small repeating GOP from the installed Gamma reference clip, packetized as RFC 6184.</summary>
internal sealed unsafe class H264RtpFixture
{
    internal sealed record AccessUnit(uint Timestamp, byte[][] Payloads);
    public string ParameterSets { get; }
    public List<AccessUnit> Frames { get; } = [];
    public uint CycleTicks { get; }

    public H264RtpFixture()
    {
        using var input = new FFmpegDemuxContext(MediaFixtures.Video(), CancellationToken.None, MediaFixtures.Runtime);
        var index = ffmpeg.av_find_best_stream(input.Context, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        FFmpegDemuxContext.Check(index, "find test video");
        var stream = input.Context->streams[index];
        var config = new ReadOnlySpan<byte>(stream->codecpar->extradata, stream->codecpar->extradata_size);
        if (config.Length < 7 || config[0] != 1) throw new InvalidDataException("Expected AVC configuration in the H.264 reference clip.");
        var lengthSize = (config[4] & 3) + 1;
        var offset = 6;
        var sets = new List<string>();
        ReadSets(config[5] & 31);
        ReadSets(config[offset++]);
        ParameterSets = string.Join(',', sets);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            while (Frames.Count < 50 && input.Read(packet) >= 0)
            {
                try
                {
                    if (packet->stream_index != index) continue;
                    var bytes = new ReadOnlySpan<byte>(packet->data, packet->size);
                    var payloads = new List<byte[]>();
                    for (var position = 0; position < bytes.Length;)
                    {
                        var length = 0;
                        for (var i = 0; i < lengthSize; i++) length = checked(length * 256 + bytes[position++]);
                        var nal = bytes.Slice(position, length); position += length;
                        if (length <= 1000) payloads.Add(nal.ToArray());
                        else
                            for (var start = 1; start < length; start += 998)
                            {
                                var count = Math.Min(998, length - start);
                                payloads.Add([(byte)((nal[0] & 0xe0) | 28),
                                    (byte)((nal[0] & 31) | (start == 1 ? 0x80 : 0) | (start + count == length ? 0x40 : 0)),
                                    .. nal.Slice(start, count)]);
                            }
                    }
                    var timestamp = (uint)Math.Max(0, Math.Round(packet->pts * (double)stream->time_base.num / stream->time_base.den * 90000));
                    Frames.Add(new(timestamp, payloads.ToArray()));
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            CycleTicks = Frames.Max(f => f.Timestamp) + 3600;
        }
        finally { ffmpeg.av_packet_free(&packet); }

        void ReadSets(int count)
        {
            // Span cannot be captured by a local function; borrow the codec-owned configuration.
            var data = new ReadOnlySpan<byte>(stream->codecpar->extradata, stream->codecpar->extradata_size);
            for (var i = 0; i < count; i++)
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]); offset += 2;
                sets.Add(Convert.ToBase64String(data.Slice(offset, length))); offset += length;
            }
        }
    }
}
