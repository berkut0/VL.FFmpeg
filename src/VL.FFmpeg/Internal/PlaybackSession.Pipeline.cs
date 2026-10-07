using System.Diagnostics;
using System.Threading.Channels;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

internal sealed partial class PlaybackSession
{
    private async Task WorkerLoop()
    {
        FFmpegDemuxContext? demux = null;
        using var videoSlot = new VideoDecoderSlot();
        FFmpegAudioDecoder? audio = null;
        VideoBinding? configuredVideo = null;
        DecodeMode configuredMode = DecodeMode.Auto;
        long processed = -1;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var wake = _wake.Next;
                Request request;
                CancellationTokenSource? cancellation;
                VideoBinding[] retiredBindings;
                lock (_gate)
                {
                    request = new(_generation, _options, _requestedPosition, _binding, _audioFormat, _recovering);
                    retiredBindings = _retiredBindings.ToArray();
                    _retiredBindings.Clear();
                    if (request.Generation == processed) cancellation = null;
                    else
                    {
                        processed = request.Generation;
                        _active = cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        _activeGeneration = request.Generation;
                        _opening = !string.IsNullOrWhiteSpace(request.Options.Filename)
                            && (demux is null || demux.Filename != request.Options.Filename);
                    }
                }
                foreach (var binding in retiredBindings) binding.Dispose();
                if (cancellation is null)
                {
                    await wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                using (cancellation)
                {
                    var token = cancellation.Token;
                    try
                    {
                        if (demux?.Filename != request.Options.Filename || (request.Video is null && request.Audio is null))
                        {
                            videoSlot.Dispose();
                            audio?.Dispose(); audio = null;
                            demux?.Dispose(); demux = null;
                            configuredVideo = null;
                        }
                        if (string.IsNullOrWhiteSpace(request.Options.Filename) || (request.Video is null && request.Audio is null))
                            continue;
                        if (demux is null)
                        {
                            _diagnostics.ResetReadAverage();
                            demux = await Io(() => new FFmpegDemuxContext(request.Options.Filename, token), token).ConfigureAwait(false);
                            _diagnostics.ContainerOpened();
                        }
                        demux.SetCancellation(token);
                        var duration = demux.Duration.TotalSeconds;
                        var mediaPosition = request.Options.Loop && duration > 0 ? request.Position % duration : request.Position;
                        var offset = request.Position - mediaPosition;
                        var seekSucceeded = true;
                        if (request.Position > 0 || videoSlot.Value is not null || audio is not null)
                        {
                            try { await SeekContainer(demux, mediaPosition, token).ConfigureAwait(false); }
                            catch (FFmpegDecodeException) when (request.Recovery)
                            {
                                seekSucceeded = false;
                                lock (_gate) _recoveryMessage = "Realtime recovery seek failed; continuing sequential decode.";
                            }
                        }
                        if (videoSlot.Value is not null && (configuredVideo != request.Video || configuredMode != request.Options.DecodeMode))
                        { videoSlot.Dispose(); }
                        if (request.Video is not null && videoSlot.Value is null)
                        {
                            await videoSlot.Replace(() => PlaybackWork.Run(() => CreateVideo(request, demux, request.Options.DecodeMode, token), token)).ConfigureAwait(false);
                            configuredVideo = request.Video;
                            configuredMode = request.Options.DecodeMode;
                        }
                        if (videoSlot.Value is not null)
                        {
                            if (seekSucceeded) videoSlot.Value.Flush(TimeSpan.FromSeconds(mediaPosition), token);
                            else videoSlot.Value.SetCancellation(token);
                        }
                        if (request.Audio is not null && audio is null)
                        {
                            try
                            {
                                audio = await PlaybackWork.Run(() => new FFmpegAudioDecoder(demux,
                                    request.Audio.Rate, request.Audio.Channels, token), token).ConfigureAwait(false);
                            }
                            catch (Exception e) when (e is not OperationCanceledException)
                            { lock (_gate) if (request.Generation == _generation) Volatile.Write(ref _audioFault, e); }
                        }
                        if (audio is not null && request.Audio is not null)
                            audio.Flush(TimeSpan.FromSeconds(mediaPosition), request.Audio.Rate, request.Audio.Channels, token);
                        lock (_gate)
                        {
                            if (request.Generation != _generation) continue;
                            _duration = duration > 0 ? duration : videoSlot.Value?.MediaInfo.Duration.TotalSeconds ?? audio?.MediaInfo.Duration.TotalSeconds ?? 0;
                            _frameDuration = videoSlot.Value?.MediaInfo.FrameRate.N > 0 ? videoSlot.Value.MediaInfo.FrameRate.D / (double)videoSlot.Value.MediaInfo.FrameRate.N : 0;
                            _description = videoSlot.Value is not null ? $"{videoSlot.Value.MediaInfo.VideoCodec}, {videoSlot.Value.MediaInfo.Width}x{videoSlot.Value.MediaInfo.Height}; {videoSlot.Value.DecodeStatus}" : "FFmpeg audio";
                            if (audio is not null && request.Audio is not null)
                                _audioBuffer = new AudioSampleBuffer(audio.OutputSampleRate, audio.OutputChannelCount, request.Audio.Capacity);
                            _opening = false;
                        }
                        PublishWorkerStatus(request.Generation);
                        var softwareFallback = configuredMode == DecodeMode.Software || videoSlot.Value?.HardwareConfigured != true;
                        while (!token.IsCancellationRequested)
                        {
                            double end;
                            try { end = await RunCycle(request, demux, videoSlot.Value, audio, mediaPosition, offset, token).ConfigureAwait(false); }
                            catch (Exception e) when (request.Options.DecodeMode == DecodeMode.Auto && !softwareFallback
                                && videoSlot.Value is not null && (e is FFmpegHardwareException || (e is FFmpegDecodeException && videoSlot.Value.HardwareConfigured)))
                            {
                                softwareFallback = true;
                                await videoSlot.Replace(() => PlaybackWork.Run(() => CreateVideo(request, demux, DecodeMode.Software, token), token)).ConfigureAwait(false);
                                if (!BeginSoftwareFallback(request, audio, e.Message, token)) break;
                                var target = _clock.Position;
                                mediaPosition = _duration > 0 && request.Options.Loop ? target % _duration : target;
                                offset = target - mediaPosition;
                                await SeekContainer(demux, mediaPosition, token).ConfigureAwait(false);
                                videoSlot.Value!.Flush(TimeSpan.FromSeconds(mediaPosition), token);
                                if (audio is not null && request.Audio is not null) audio.Flush(TimeSpan.FromSeconds(mediaPosition), request.Audio.Rate, request.Audio.Channels, token);
                                continue;
                            }
                            token.ThrowIfCancellationRequested();
                            bool loop;
                            lock (_gate)
                            {
                                if (request.Generation != _generation) break;
                                if (_duration <= 0 && end > 0) _duration = end;
                                loop = _options.Loop;
                                if (!loop) _ended = true;
                            }
                            if (!loop) { PublishWorkerStatus(request.Generation); break; }
                            var cycleDuration = _duration > 0 ? _duration : end;
                            if (cycleDuration <= 0) throw new InvalidDataException("Cannot loop an empty media stream.");
                            offset += cycleDuration;
                            mediaPosition = 0;
                            await SeekContainer(demux, 0, token).ConfigureAwait(false);
                            videoSlot.Value?.Flush(TimeSpan.Zero, token);
                            if (audio is not null && request.Audio is not null) audio.Flush(TimeSpan.Zero, request.Audio.Rate, request.Audio.Channels, token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        lock (_gate)
                        {
                            if (request.Generation == _generation) { _fault = e; _opening = false; }
                        }
                    }
                    finally
                    {
                        PublishWorkerStatus(request.Generation);
                        lock (_gate) { if (ReferenceEquals(_active, cancellation)) _active = null; }
                    }
                }
            }
        }
        finally { videoSlot.Dispose(); audio?.Dispose(); demux?.Dispose(); }
    }

    private bool BeginSoftwareFallback(Request request, FFmpegAudioDecoder? audio, string reason, CancellationToken token)
    {
        lock (_gate)
        {
            if (request.Generation != _generation || token.IsCancellationRequested) return false;
            while (_ready.TryDequeue(out var old)) RetireLocked(old.Frame);
            Volatile.Write(ref _audioOutput, null);
            if (_audioBuffer is not null) RetireLocked(_audioBuffer);
            _audioBuffer = audio is not null && request.Audio is not null
                ? new AudioSampleBuffer(audio.OutputSampleRate, audio.OutputChannelCount, request.Audio.Capacity) : null;
            _audioReady = false;
            _recoveryMessage = $"Hardware fallback: {reason}";
            return true;
        }
    }

    private void PublishWorkerStatus(long generation)
    {
        PlaybackStatus status;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            if (_binding is null)
            {
                var audioTime = TimeSpan.FromTicks(Interlocked.Read(ref _audioPositionTicks)).TotalSeconds;
                _position = _options.Loop && _duration > 0 ? audioTime % _duration : audioTime;
            }
            status = StatusLocked(default, _clock.Position, false);
        }
        _source.PublishPlaybackStatus(this, status);
    }

    private FFmpegVideoDecoder CreateVideo(Request request, FFmpegDemuxContext demux, DecodeMode mode, CancellationToken token)
        => VideoDecoderSlot.Open(mode, effective => new FFmpegVideoDecoder(demux, token, decodeMode: effective, graphicsDevice: request.Video!.Device,
            graphicsDeviceType: request.Video.Context.GraphicsDeviceType,
            usesLinearColorspace: request.Video.Context.UsesLinearColorspace),
            reason => { lock (_gate) if (request.Generation == _generation) _recoveryMessage = $"Hardware fallback: {reason}"; });

    private async Task<double> RunCycle(Request request, FFmpegDemuxContext demux,
        FFmpegVideoDecoder? video, FFmpegAudioDecoder? audio, double minimum, double offset, CancellationToken token)
    {
        using var videoPackets = new PacketQueue();
        using var audioPackets = new PacketQueue();
        using var cycleLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cycleToken = cycleLifetime.Token;
        var videoPreroll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demux.SetCancellation(cycleToken);
        var raw = Channel.CreateBounded<NativeVideoFrame>(new BoundedChannelOptions(VideoSchedulingPolicy.RawFrameLimit)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var videoLead = VideoSchedulingPolicy.BufferWindow + (video?.DelayFrames ?? 0) * _frameDuration;
        var io = Io(() => { Demux(demux, video?.StreamIndex ?? -1, audio?.StreamIndex ?? -1, videoPackets, audioPackets, offset, videoLead, videoPreroll.Task, cycleToken); return true; }, cycleToken);
        var decode = video is null ? Task.FromResult(0d) : DecodeVideo(video, videoPackets, raw.Writer, minimum, videoPreroll, cycleToken);
        var convert = video is null ? Task.CompletedTask : ConvertVideo(request, video, raw.Reader, offset, cycleToken);
        var sound = audio is null ? Task.FromResult(0d) : DecodeAudio(request, audio, audioPackets, offset, cycleToken);
        var tasks = new Task[] { io, decode, convert, sound };
        foreach (var task in tasks)
            _ = task.ContinueWith(_ => { try { cycleLifetime.Cancel(); } catch (ObjectDisposedException) { } }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); return Math.Max(decode.Result, sound.Result); }
        catch
        {
            var failure = tasks.Select(t => t.Exception?.GetBaseException()).FirstOrDefault(e => e is not null && e is not OperationCanceledException);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally { while (raw.Reader.TryRead(out var frame)) frame.Dispose(); demux.SetCancellation(token); }
    }

    private unsafe void Demux(FFmpegDemuxContext demux, int videoIndex, int audioIndex,
        PacketQueue video, PacketQueue audio, double offset, double videoLead, Task videoPreroll, CancellationToken token)
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
                var result = demux.Read(pointer);
                var readTicks = Stopwatch.GetTimestamp() - started;
                _diagnostics.ReadCompleted(readTicks);
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
                        _diagnostics.VideoPacketRead(readTicks, packet.Size);
                        var timestamp = pointer->dts != ffmpeg.AV_NOPTS_VALUE ? pointer->dts : pointer->pts;
                        var timeBase = demux.Context->streams[videoIndex]->time_base;
                        if (timestamp != ffmpeg.AV_NOPTS_VALUE && timeBase.den > 0)
                        {
                            var timeline = timestamp * (double)timeBase.num / timeBase.den - demux.OriginSeconds + offset;
                            WaitForReadAhead(timeline, videoLead, videoPreroll, token);
                        }
                    }
                    if (queue == audio && videoIndex >= 0)
                    {
                        packet.Discontinuity = discontinuity;
                        transferred = queue.TryWrite(packet);
                        if (!transferred) { queue.Clear(); discontinuity = true; _diagnostics.AudioDiscontinuity(); }
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

    // Runs on the dedicated I/O owner, so waiting here never consumes a decode worker.
    private void WaitForReadAhead(double timeline, double lead, Task videoPreroll, CancellationToken token)
    {
        // Selecting the containing seek frame can require the next timestamp, even when
        // it lies beyond normal read-ahead (low/variable FPS). Queues still bound this work.
        while (videoPreroll.IsCompleted && !_clock.Waiting)
        {
            token.ThrowIfCancellationRequested();
            var wake = _wake.Next;
            var ahead = timeline - _clock.Position - lead;
            if (ahead <= 0) return;
            if (Volatile.Read(ref _options).Play)
                PlaybackWait.ForChange(wake, TimeSpan.FromSeconds(Math.Min(ahead, 1)), token).GetAwaiter().GetResult();
            else
                wake.WaitAsync(token).GetAwaiter().GetResult();
        }
    }

    private async Task<double> DecodeVideo(FFmpegVideoDecoder decoder, PacketQueue packets,
        ChannelWriter<NativeVideoFrame> output, double minimum, TaskCompletionSource videoPreroll, CancellationToken token)
    {
        NativeVideoFrame? preroll = null;
        var pastPreroll = false;
        var end = 0d;
        async Task Receive()
        {
            while (true)
            {
                var frame = await PlaybackWork.Run(() =>
                {
                    var start = Stopwatch.GetTimestamp();
                    var value = decoder.ReceiveRaw();
                    _diagnostics.DecodeCompleted(Stopwatch.GetTimestamp() - start);
                    return value;
                }, token, decoder.ThreadCount).ConfigureAwait(false);
                if (frame is null) return;
                end = Math.Max(end, frame.Time + frame.Duration);
                if (!pastPreroll && frame.Time <= minimum)
                { preroll?.Dispose(); preroll = frame; continue; }
                if (!pastPreroll)
                {
                    pastPreroll = true;
                    videoPreroll.TrySetResult();
                    if (preroll is not null)
                    {
                        var previous = preroll; preroll = null;
                        try { await output.WriteAsync(previous, token).ConfigureAwait(false); }
                        catch { previous.Dispose(); frame.Dispose(); throw; }
                    }
                }
                try { await output.WriteAsync(frame, token).ConfigureAwait(false); }
                catch { frame.Dispose(); throw; }
            }
        }
        try
        {
            while (true)
            {
                using var packet = await packets.Read(token).ConfigureAwait(false);
                while (await PlaybackWork.Run(() => SendVideoTimed(decoder, packet), token, decoder.ThreadCount).ConfigureAwait(false) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    await Receive().ConfigureAwait(false);
                await Receive().ConfigureAwait(false);
                if (packet is null) break;
            }
            if (preroll is not null)
            {
                var frame = preroll; preroll = null;
                try { await output.WriteAsync(frame, token).ConfigureAwait(false); }
                catch { frame.Dispose(); throw; }
            }
            return end;
        }
        finally { videoPreroll.TrySetResult(); preroll?.Dispose(); output.TryComplete(); }
    }

    private async Task ConvertVideo(Request request, FFmpegVideoDecoder decoder, ChannelReader<NativeVideoFrame> raw,
        double offset, CancellationToken token)
    {
        while (await raw.WaitToReadAsync(token).ConfigureAwait(false))
        {
            if (!raw.TryRead(out var frame)) continue;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var wake = _wake.Next;
                    if (!SelectVideoCandidate(request, raw, ref frame, offset, out var preview, out var target))
                        break;
                    bool space;
                    lock (_gate)
                    {
                        if (request.Generation != _generation) return;
                        space = _ready.Count < VideoSchedulingPolicy.ReadyFrameLimit;
                    }
                    var ahead = frame.Time + offset - target - VideoSchedulingPolicy.BufferWindow;
                    if (!space || (!preview && ahead > 0))
                    {
                        if (ahead > 0 && _options.Play) await PlaybackWait.ForChange(wake, TimeSpan.FromSeconds(Math.Min(ahead, 1)), token).ConfigureAwait(false);
                        else await wake.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    var cpu = ResourceBudget.Cpu.Changed.Next;
                    var gpu = ResourceBudget.ForDevice(request.Video!.Device).Changed.Next;
                    DecodedVideoFrame? converted;
                    try
                    {
                        converted = await PlaybackWork.Run(() =>
                        {
                            // Admission can take longer than a frame interval. Re-select only
                            // after owning CPU/device access, before output allocation or upload.
                            if (!SelectVideoCandidate(request, raw, ref frame, offset, out preview, out target))
                                return null;
                            var late = !preview && VideoSchedulingPolicy.IsLate(frame.Time + offset, frame.Duration, target);
                            var start = Stopwatch.GetTimestamp();
                            var value = decoder.Convert(frame);
                            _diagnostics.ConversionCompleted(Stopwatch.GetTimestamp() - start, late);
                            return value;
                        }, token, device: request.Video.Device).ConfigureAwait(false);
                    }
                    catch (ResourceUnavailableException)
                    {
                        _diagnostics.ResourceUnavailable();
                        var waitStarted = Stopwatch.GetTimestamp();
                        await Task.WhenAny(wake, cpu, gpu).WaitAsync(token).ConfigureAwait(false);
                        _health.ObserveQueueWaitDuration(Stopwatch.GetElapsedTime(waitStarted));
                        continue;
                    }
                    _diagnostics.ResourceAvailable();
                    if (converted is null) break;
                    lock (_gate)
                    {
                        if (request.Generation != _generation || token.IsCancellationRequested) RetireLocked(converted);
                        else
                        {
                            _ready.Enqueue(new(request.Generation, frame.Time + offset, converted));
                            _clock.Ready();
                        }
                    }
                    _wake.Pulse();
                    break;
                }
            }
            finally { frame.Dispose(); }
        }
    }

    private bool SelectVideoCandidate(Request request, ChannelReader<NativeVideoFrame> raw,
        ref NativeVideoFrame frame, double offset, out bool preview, out double target)
    {
        target = _clock.Position;
        lock (_gate)
        {
            preview = VideoSchedulingPolicy.NeedsPreview(_preview, _presented, _ready.Count, _options.Play, _clock.Waiting);
            if (request.Generation != _generation) return false;
        }
        if (!preview)
        {
            while (raw.TryPeek(out var newer) && newer.Time + offset <= target)
            {
                if (!raw.TryRead(out newer)) break;
                frame.Dispose();
                frame = newer;
                _diagnostics.FrameDropped();
            }
        }
        lock (_gate)
        {
            if (request.Generation != _generation) return false;
            if (!preview && VideoSchedulingPolicy.IsObsolete(frame.Time + offset,
                _presented ? _presentedTimeline : null, target, offset, _duration))
            {
                _diagnostics.FrameDropped();
                return false;
            }
        }
        return true;
    }

    private async Task<double> DecodeAudio(Request request, FFmpegAudioDecoder decoder, PacketQueue packets,
        double offset, CancellationToken token)
    {
        var end = 0d;
        async Task Publish(DecodedAudioFrame frame)
        {
            end = Math.Max(end, frame.Timecode.TotalSeconds + frame.SampleCount / (double)frame.SampleRate);
            while (frame.Timecode.TotalSeconds + offset > _clock.Position + .250 && !_clock.Waiting)
            {
                var wake = _wake.Next;
                var delay = frame.Timecode.TotalSeconds + offset - _clock.Position - .250;
                await PlaybackWait.ForChange(wake, TimeSpan.FromSeconds(Math.Clamp(delay, .001, 1)), token).ConfigureAwait(false);
            }
            var buffer = Volatile.Read(ref _audioBuffer);
            if (buffer is null) return;
            var written = await buffer.WriteAsync(frame with { Timecode = frame.Timecode + TimeSpan.FromSeconds(offset) },
                () => _clock.Position, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (request.Generation != _generation || !written) return;
                if (!_audioReady) Volatile.Write(ref _audioOutput, new AudioOutput(request.Audio!, buffer));
                _audioReady = true;
                if (request.Video is null) _clock.Ready();
            }
        }
        async Task Receive()
        {
            while (true)
            {
                var frame = await PlaybackWork.Run(decoder.Receive, token).ConfigureAwait(false);
                if (frame is null) return;
                await Publish(frame).ConfigureAwait(false);
            }
        }
        try
        {
            while (true)
            {
                using var packet = await packets.Read(token).ConfigureAwait(false);
                if (packet?.Discontinuity == true)
                    decoder.Flush(TimeSpan.FromSeconds(Math.Max(0, _clock.Position - offset)), request.Audio!.Rate, request.Audio.Channels, token);
                while (await PlaybackWork.Run(() => Send(decoder, packet), token).ConfigureAwait(false) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    await Receive().ConfigureAwait(false);
                await Receive().ConfigureAwait(false);
                if (packet is null) break;
            }
            var tail = await PlaybackWork.Run(() =>
            {
                var frames = new List<DecodedAudioFrame>();
                decoder.FlushResampler(frame => { frames.Add(frame); return true; });
                return frames;
            }, token).ConfigureAwait(false);
            foreach (var frame in tail) await Publish(frame).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate) if (request.Generation == _generation) Volatile.Write(ref _audioFault, e);
            // Keep draining packets so an audio-only I/O owner can finish/cancel.
            while (await packets.Read(token).ConfigureAwait(false) is { } packet) packet.Dispose();
        }
        return end;
    }

    private Task SeekContainer(FFmpegDemuxContext demux, double position, CancellationToken token)
        => Io(() =>
        {
            demux.Seek(TimeSpan.FromSeconds(position));
            _diagnostics.SeekCompleted();
            return true;
        }, token);

    private static Task<T> Io<T>(Func<T> action, CancellationToken token)
        => Task.Factory.StartNew(() => { token.ThrowIfCancellationRequested(); return action(); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private int SendVideoTimed(FFmpegVideoDecoder decoder, NativePacket? packet)
    {
        var start = Stopwatch.GetTimestamp();
        try { return Send(decoder, packet); }
        finally { _diagnostics.DecodeCompleted(Stopwatch.GetTimestamp() - start); }
    }
    private static unsafe int Send(FFmpegVideoDecoder decoder, NativePacket? packet)
    {
        var result = decoder.SendPacket(packet is null ? null : packet.Pointer);
        if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_EOF) FFmpegDemuxContext.Check(result, "send video packet");
        return result;
    }
    private static unsafe int Send(FFmpegAudioDecoder decoder, NativePacket? packet)
    {
        var result = decoder.SendPacket(packet is null ? null : packet.Pointer);
        if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_EOF) FFmpegDemuxContext.Check(result, "send audio packet");
        return result;
    }
}
