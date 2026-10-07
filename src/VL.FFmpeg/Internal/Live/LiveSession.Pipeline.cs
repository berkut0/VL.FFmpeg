using System.Diagnostics;
using System.Threading.Channels;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal.Live;

internal sealed partial class LiveSession
{
    /// <summary>A single attachment configuration; borrows its connection and device.</summary>
    private sealed class LiveRun(LiveSession owner, FFmpegDemuxContext demux, Binding? binding,
        AudioDemand? initialAudio, DecodeMode mode, bool software, long revision, long attempt,
        LiveConnectionPolicy recovery, CancellationToken token) : IDisposable
    {
        private readonly LiveTimeline _timeline = new();
        private readonly Stopwatch _arrival = Stopwatch.StartNew();
        private FFmpegVideoDecoder? _video;
        private FFmpegAudioDecoder? _audio;
        private AudioDemand? _configuredAudio;

        public async Task Run()
        {
            if (binding is not null && FindStream(demux, AVMediaType.AVMEDIA_TYPE_VIDEO) >= 0)
                _video = await PlaybackWork.Run(() => VideoDecoderSlot.Open(software ? DecodeMode.Software : mode,
                    effective => new FFmpegVideoDecoder(demux, token, effective, binding.Device,
                        binding.Context.GraphicsDeviceType, binding.Context.UsesLinearColorspace),
                    _ => owner.SetStatus(LivePlaybackPhase.Buffering, "Using software decode.", revision)), token).ConfigureAwait(false);
            if (FindStream(demux, AVMediaType.AVMEDIA_TYPE_AUDIO) >= 0)
            {
                var format = initialAudio ?? new AudioDemand(48000, 0, 12000);
                try
                {
                    _audio = await PlaybackWork.Run(() => new FFmpegAudioDecoder(demux, format.Rate, format.Channels, token), token).ConfigureAwait(false);
                    _configuredAudio = format;
                }
                catch (Exception e) when (_video is not null && e is not OperationCanceledException)
                { owner.SetStatus(LivePlaybackPhase.Buffering, "Audio decoder unavailable; video remains active.", revision); }
            }
            if (_video is null && _audio is null) throw new NotSupportedException("No supported live stream.");
            try
            {
                await MediaPipeline.RunAsync(demux, _video?.StreamIndex ?? -1, _audio?.StreamIndex ?? -1,
                    DecodeVideo, ConvertVideo, DecodeAudio, static (_, ct) => ct.ThrowIfCancellationRequested(),
                    owner._diagnostics, token).ConfigureAwait(false);
            }
            catch (FFmpegDecodeException e) when (_video?.HardwareConfigured == true && mode == DecodeMode.Auto)
            { throw new FFmpegHardwareException(e.NativeMessage); }
        }

        private LiveTime Map(double? source, double duration, bool video)
        {
            var mapped = _timeline.Map(source, duration, _arrival.Elapsed.TotalSeconds, video);
            lock (owner._gate)
            {
                if (attempt != owner._attempt || !mapped.Accepted || mapped.Epoch < owner._epoch)
                    return mapped with { Accepted = false };
                if (mapped.Epoch > owner._epoch)
                {
                    owner._epoch = mapped.Epoch;
                    owner.ClearOutputLocked();
                    owner._diagnostics.AudioDiscontinuity();
                }
            }
            return mapped;
        }

        private async Task<double> DecodeVideo(PacketQueue packets, ChannelWriter<NativeVideoFrame> raw, CancellationToken ct)
        {
            try
            {
                await MediaDecodePump.Video(_video!, packets, async frame =>
                {
                    try
                    {
                        var mapped = Map(frame.SourceTime, frame.Duration, true);
                        if (!mapped.Accepted) { frame.Dispose(); return; }
                        frame.MapTime(mapped.Time, mapped.Epoch);
                        await raw.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                    catch { frame.Dispose(); throw; }
                }, owner._diagnostics, ct, waitForKeyframe: true).ConfigureAwait(false);
                return 0;
            }
            finally { raw.TryComplete(); }
        }

        private async Task ConvertVideo(ChannelReader<NativeVideoFrame> raw, CancellationToken ct)
        {
            while (await raw.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (!raw.TryRead(out var frame)) continue;
                try
                {
                    bool Select()
                    {
                        var now = owner._clock.Position;
                        while (raw.TryPeek(out var newer) && (newer.Epoch > frame.Epoch || newer.Time <= now))
                        {
                            if (!raw.TryRead(out newer)) break;
                            frame.Dispose(); frame = newer;
                            owner._diagnostics.FrameDropped();
                        }
                        lock (owner._gate)
                            return attempt == owner._attempt && frame.Epoch == owner._epoch && frame.Time >= owner._lastPresented;
                    }
                    while (Select())
                    {
                        ct.ThrowIfCancellationRequested();
                        var wake = owner._wake.Next;
                        bool wait;
                        lock (owner._gate) wait = owner._ready.Count >= VideoSchedulingPolicy.ReadyFrameLimit
                            || (owner._started && frame.Time > owner._clock.Position + .15);
                        if (wait)
                        { await PlaybackWait.ForChange(wake, TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false); continue; }
                        try
                        {
                            var converted = await PlaybackWork.Run(() =>
                            {
                                if (!Select()) return null;
                                var start = Stopwatch.GetTimestamp();
                                var result = _video!.Convert(frame);
                                owner._diagnostics.ConversionCompleted(Stopwatch.GetTimestamp() - start,
                                    frame.Time + frame.Duration < owner._clock.Position);
                                return result;
                            }, ct, device: binding!.Device).ConfigureAwait(false);
                            if (converted is null) break;
                            owner._diagnostics.ResourceAvailable();
                            lock (owner._gate)
                            {
                                if (attempt != owner._attempt || frame.Epoch != owner._epoch || ct.IsCancellationRequested)
                                    owner.Retire(converted);
                                else
                                {
                                    StartClock(frame.Time);
                                    owner._ready.Enqueue(new(attempt, frame.Epoch, frame.Time, converted));
                                    recovery.ObserveHealthy(_arrival.Elapsed.TotalSeconds);
                                }
                            }
                            owner.Signal();
                            break;
                        }
                        catch (ResourceUnavailableException)
                        {
                            owner._diagnostics.ResourceUnavailable();
                            await PlaybackWait.ForEitherChange(ResourceBudget.Cpu.Changed.Next,
                                ResourceBudget.ForDevice(binding!.Device).Changed.Next, TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
                        }
                    }
                }
                finally { frame.Dispose(); }
            }
        }

        private void StartClock(double time)
        {
            if (owner._started) return;
            owner._clock.Reset(time - .15, true, false);
            owner._started = true;
        }

        private async Task<double> DecodeAudio(PacketQueue packets, CancellationToken ct)
        {
            try
            {
                await MediaDecodePump.Audio(_audio!, packets, async frame =>
                {
                    AudioDemand? demand;
                    lock (owner._gate) demand = owner._audioDemand;
                    if (demand is null || demand != _configuredAudio) return;
                    var mapped = Map(frame.SourceTime, frame.SampleCount / (double)frame.SampleRate, false);
                    if (!mapped.Accepted) return;
                    AudioOutput output;
                    lock (owner._gate)
                    {
                        if (attempt != owner._attempt || mapped.Epoch != owner._epoch) return;
                        if (owner._audioOutput is null || owner._audioOutput.Demand != demand)
                        {
                            if (owner._audioOutput is not null) owner.Retire(owner._audioOutput.Buffer);
                            owner._audioOutput = new(demand, new AudioSampleBuffer(frame.SampleRate, frame.ChannelCount, demand.Capacity));
                        }
                        output = owner._audioOutput;
                        if (_video is null) StartClock(mapped.Time);
                    }
                    await output.Buffer.WriteAsync(frame with { Timecode = TimeSpan.FromSeconds(mapped.Time) },
                        () => owner._clock.Position, ct).ConfigureAwait(false);
                    if (_video is null)
                    {
                        owner.SetStatus(LivePlaybackPhase.Playing, "Live audio.", revision);
                        recovery.ObserveHealthy(_arrival.Elapsed.TotalSeconds);
                    }
                }, packet =>
                {
                    AudioDemand? demand;
                    lock (owner._gate) demand = owner._audioDemand;
                    if (demand is not null && demand != _configuredAudio)
                    { _audio!.ReconfigureOutput(demand.Rate, demand.Channels); _configuredAudio = demand; }
                    if (packet?.Discontinuity == true)
                        _audio!.Flush(TimeSpan.Zero, _audio.OutputSampleRate, _audio.OutputChannelCount, ct);
                }, ct, () => Volatile.Read(ref owner._audioDemand) is not null).ConfigureAwait(false);
            }
            catch (Exception e) when (_video is not null && e is not OperationCanceledException)
            {
                owner.SetStatus(LivePlaybackPhase.Playing, "Audio interrupted; video remains active.", revision);
                while (await packets.Read(ct).ConfigureAwait(false) is { } packet) packet.Dispose();
            }
            return 0;
        }

        private static unsafe int FindStream(FFmpegDemuxContext input, AVMediaType type)
            => ffmpeg.av_find_best_stream(input.Context, type, -1, -1, null, 0);
        public void Dispose() { _video?.Dispose(); _audio?.Dispose(); }
    }
}
