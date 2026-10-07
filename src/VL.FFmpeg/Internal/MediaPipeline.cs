using System.Threading.Channels;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;
using System.Diagnostics;

namespace VL.FFmpeg.Internal;

/// <summary>One bounded run. The session owns transport, clocks and the borrowed codecs.</summary>
internal static class MediaPipeline
{
    public static async Task<double> RunAsync(FFmpegDemuxContext demux, int videoIndex, int audioIndex,
        Func<PacketQueue, ChannelWriter<NativeVideoFrame>, CancellationToken, Task<double>> decodeVideo,
        Func<ChannelReader<NativeVideoFrame>, CancellationToken, Task> convertVideo,
        Func<PacketQueue, CancellationToken, Task<double>> decodeAudio,
        Action<double, CancellationToken> admitVideo, PlaybackDiagnostics diagnostics, CancellationToken token)
    {
        using var videoPackets = new PacketQueue();
        using var audioPackets = new PacketQueue();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runToken = lifetime.Token;
        demux.SetCancellation(runToken);
        var raw = Channel.CreateBounded<NativeVideoFrame>(new BoundedChannelOptions(VideoSchedulingPolicy.RawFrameLimit)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var io = Io(() => { Demux(demux, videoIndex, audioIndex, videoPackets, audioPackets, admitVideo, diagnostics, runToken); return true; }, runToken);
        var decode = videoIndex < 0 ? Task.FromResult(0d) : decodeVideo(videoPackets, raw.Writer, runToken);
        var convert = videoIndex < 0 ? Task.CompletedTask : convertVideo(raw.Reader, runToken);
        var audio = audioIndex < 0 ? Task.FromResult(0d) : decodeAudio(audioPackets, runToken);
        var tasks = new Task[] { io, decode, convert, audio };
        foreach (var task in tasks)
            _ = task.ContinueWith(_ => { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); return Math.Max(decode.Result, audio.Result); }
        catch
        {
            var failure = tasks.Select(t => t.Exception?.GetBaseException()).FirstOrDefault(e => e is not null && e is not OperationCanceledException);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally { while (raw.Reader.TryRead(out var frame)) frame.Dispose(); demux.SetCancellation(token); }
    }

    public static Task<T> Io<T>(Func<T> action, CancellationToken token)
        => Task.Factory.StartNew(() => { token.ThrowIfCancellationRequested(); return action(); }, token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe void Demux(FFmpegDemuxContext demux, int videoIndex, int audioIndex,
        PacketQueue video, PacketQueue audio, Action<double, CancellationToken> admitVideo, PlaybackDiagnostics diagnostics, CancellationToken token)
    {
        var discontinuity = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pointer = ffmpeg.av_packet_alloc();
                if (pointer is null) throw new OutOfMemoryException();
                var started = Stopwatch.GetTimestamp();
                int result;
                try { result = demux.Read(pointer); }
                catch { ffmpeg.av_packet_free(&pointer); throw; }
                var readTicks = Stopwatch.GetTimestamp() - started;
                diagnostics.ReadCompleted(readTicks);
                if (result < 0)
                {
                    ffmpeg.av_packet_free(&pointer);
                    token.ThrowIfCancellationRequested();
                    if (result == ffmpeg.AVERROR_EOF) return;
                    FFmpegDemuxContext.Check(result, "read media packet");
                }
                var packet = new NativePacket(pointer);
                var transferred = false;
                try
                {
                    var queue = packet.StreamIndex == videoIndex ? video : packet.StreamIndex == audioIndex ? audio : null;
                    if (queue is null) continue;
                    if (packet.Size > ResourceBudget.Cpu.Capacity) throw new InvalidDataException("A media packet exceeds the playback memory budget.");
                    if (queue == video)
                    {
                        diagnostics.VideoPacketRead(readTicks, packet.Size);
                        var timestamp = pointer->dts != ffmpeg.AV_NOPTS_VALUE ? pointer->dts : pointer->pts;
                        var timeBase = demux.Context->streams[videoIndex]->time_base;
                        if (timestamp != ffmpeg.AV_NOPTS_VALUE && timeBase.den > 0)
                        {
                            var timeline = timestamp * (double)timeBase.num / timeBase.den - demux.OriginSeconds;
                            admitVideo(timeline, token);
                        }
                    }
                    if (queue == audio && videoIndex >= 0)
                    {
                        packet.Discontinuity = discontinuity;
                        transferred = queue.TryWrite(packet);
                        if (!transferred) { queue.Clear(); discontinuity = true; diagnostics.AudioDiscontinuity(); }
                        else discontinuity = false;
                    }
                    else
                    {
                        while (!transferred)
                        {
                            token.ThrowIfCancellationRequested();
                            var changed = queue.Changed.Next;
                            var budget = ResourceBudget.Cpu.Changed.Next;
                            transferred = queue.TryWrite(packet);
                            if (!transferred) Task.WhenAny(changed, budget).WaitAsync(token).GetAwaiter().GetResult();
                        }
                    }
                }
                finally { if (!transferred) packet.Dispose(); }
            }
        }
        finally { video.Complete(); audio.Complete(); }
    }

}
