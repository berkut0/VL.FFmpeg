using System.Diagnostics;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal;

/// <summary>Codec send/receive protocol. The callback owns each video frame, even on failure.</summary>
internal static class MediaDecodePump
{
    public static Task Video(FFmpegVideoDecoder decoder, PacketQueue packets,
        Func<NativeVideoFrame, Task> publish, PlaybackDiagnostics diagnostics, CancellationToken token,
        bool waitForKeyframe = false)
        => Run(packets, packet =>
        {
            if (waitForKeyframe && packet is not null)
            {
                if (!IsKeyframe(packet)) return 0;
                waitForKeyframe = false;
            }
            var started = Stopwatch.GetTimestamp();
            try { return Send(decoder, packet); }
            finally { diagnostics.DecodeCompleted(Stopwatch.GetTimestamp() - started); }
        }, () =>
        {
            var started = Stopwatch.GetTimestamp();
            try { return decoder.ReceiveRaw(); }
            finally { diagnostics.DecodeCompleted(Stopwatch.GetTimestamp() - started); }
        }, publish, token, decoder.ThreadCount);

    public static async Task Audio(FFmpegAudioDecoder decoder, PacketQueue packets,
        Func<DecodedAudioFrame, Task> publish, Action<NativePacket?> prepare, CancellationToken token)
    {
        await Run(packets, packet => { prepare(packet); return Send(decoder, packet); },
            decoder.Receive, publish, token, 1).ConfigureAwait(false);
        var tail = await PlaybackWork.Run(() =>
        {
            var frames = new List<DecodedAudioFrame>();
            decoder.FlushResampler(frame => { frames.Add(frame); return true; });
            return frames;
        }, token).ConfigureAwait(false);
        foreach (var frame in tail) await publish(frame).ConfigureAwait(false);
    }

    private static async Task Run<T>(PacketQueue packets, Func<NativePacket?, int> send,
        Func<T?> receive, Func<T, Task> publish, CancellationToken token, int weight) where T : class
    {
        async Task Receive()
        {
            while (await PlaybackWork.Run(receive, token, weight).ConfigureAwait(false) is { } frame)
                await publish(frame).ConfigureAwait(false);
        }
        while (true)
        {
            using var packet = await packets.Read(token).ConfigureAwait(false);
            while (await PlaybackWork.Run(() => send(packet), token, weight).ConfigureAwait(false) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                await Receive().ConfigureAwait(false);
            await Receive().ConfigureAwait(false);
            if (packet is null) return;
        }
    }

    private static unsafe bool IsKeyframe(NativePacket packet) => (packet.Pointer->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
    private static unsafe int Send(FFmpegVideoDecoder decoder, NativePacket? packet)
        => Check(decoder.SendPacket(packet is null ? null : packet.Pointer));
    private static unsafe int Send(FFmpegAudioDecoder decoder, NativePacket? packet)
        => Check(decoder.SendPacket(packet is null ? null : packet.Pointer));
    private static int Check(int result)
    {
        if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_EOF)
            FFmpegDemuxContext.Check(result, "send media packet");
        return result;
    }
}
