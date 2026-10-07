using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests;

/// <summary>Feeds codec primitives synchronously for tests; playback owns its own packet scheduling.</summary>
internal static unsafe class DecoderPump
{
    public static void Decode(FFmpegDemuxContext demux, FFmpegVideoDecoder decoder,
        Func<DecodedVideoFrame, bool> accept, CancellationToken token = default)
        => DecodeRaw(demux, decoder, frame => accept(decoder.Convert(frame)), token);

    // Raw frames are borrowed only for the callback, including preroll that is never converted.
    public static void DecodeRaw(FFmpegDemuxContext demux, FFmpegVideoDecoder decoder,
        Func<NativeVideoFrame, bool> accept, CancellationToken token = default)
        => Pump(demux, decoder.StreamIndex, decoder.SendPacket, decoder.ReceiveRaw, frame =>
        {
            using (frame) return accept(frame);
        }, token);

    public static void Decode(FFmpegDemuxContext demux, FFmpegAudioDecoder decoder,
        Func<DecodedAudioFrame, bool> accept, CancellationToken token = default)
    {
        if (Pump(demux, decoder.StreamIndex, decoder.SendPacket, decoder.Receive, accept, token))
            decoder.FlushResampler(accept);
    }

    private delegate int SendPacket(AVPacket* packet);

    private static bool Pump<T>(FFmpegDemuxContext demux, int streamIndex, SendPacket send,
        Func<T?> receive, Func<T, bool> accept, CancellationToken token) where T : class
    {
        bool ReceiveAvailable()
        {
            while (receive() is { } frame)
                if (!accept(frame)) return false;
            return true;
        }

        bool SendAndReceive(AVPacket* packet)
        {
            int result;
            while ((result = send(packet)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                if (!ReceiveAvailable()) return false;
            if (result != ffmpeg.AVERROR_EOF)
                FFmpegDemuxContext.Check(result, "send test packet");
            return ReceiveAvailable();
        }

        var packet = ffmpeg.av_packet_alloc();
        if (packet is null) throw new OutOfMemoryException();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var result = demux.Read(packet);
                if (result == ffmpeg.AVERROR_EOF) return SendAndReceive(null);
                token.ThrowIfCancellationRequested();
                FFmpegDemuxContext.Check(result, "read test packet");
                try
                {
                    if (packet->stream_index == streamIndex && !SendAndReceive(packet)) return false;
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
        }
        finally { ffmpeg.av_packet_free(&packet); }
    }
}
