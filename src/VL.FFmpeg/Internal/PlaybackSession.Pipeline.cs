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
                CancellationTokenSource cancellation;
                VideoBinding[] retiredBindings;
                lock (_gate)
                {
                    request = new(_generation, _options, _requestedPosition, _binding, _audioFormat, _recovering);
                    retiredBindings = _retiredBindings.ToArray();
                    _retiredBindings.Clear();
                    if (request.Generation == processed) cancellation = null!;
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
                            Volatile.Write(ref _recentVideoReadTicks, 0);
                            Volatile.Write(ref _recentVideoPacketBytes, 0);
                            demux = await Io(() => new FFmpegDemuxContext(request.Options.Filename, token), token).ConfigureAwait(false);
                            Interlocked.Increment(ref _containerOpens);
                        }
                        demux.SetCancellation(token);
                        var duration = demux.Duration.TotalSeconds;
                        var mediaPosition = request.Options.Loop && duration > 0 ? request.Position % duration : request.Position;
                        var offset = request.Position - mediaPosition;
                        var seekSucceeded = true;
                        if (request.Position > 0 || videoSlot.Value is not null || audio is not null)
                        {
                            try { await Io(() => { demux.Seek(TimeSpan.FromSeconds(mediaPosition)); Interlocked.Increment(ref _seeks); return true; }, token).ConfigureAwait(false); }
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
                                audio = await PlaybackWork.Run(() => new FFmpegAudioDecoder(request.Options.Filename,
                                    TimeSpan.Zero, request.Audio.Rate, request.Audio.Channels, token, demux: demux), token).ConfigureAwait(false);
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
                        var cycle = 0L;
                        var softwareFallback = configuredMode == DecodeMode.Software || videoSlot.Value?.HardwareConfigured != true;
                        while (!token.IsCancellationRequested)
                        {
                            double end;
                            try { end = await RunCycle(request, demux, videoSlot.Value, audio, mediaPosition, offset, cycle, token).ConfigureAwait(false); }
                            catch (Exception e) when (request.Options.DecodeMode == DecodeMode.Auto && !softwareFallback
                                && videoSlot.Value is not null && (e is FFmpegHardwareException || (e is FFmpegDecodeException && videoSlot.Value.HardwareConfigured)))
                            {
                                softwareFallback = true;
                                await videoSlot.Replace(() => PlaybackWork.Run(() => CreateVideo(request, demux, DecodeMode.Software, token), token)).ConfigureAwait(false);
                                if (!BeginSoftwareFallback(request, audio, e.Message, token)) break;
                                var target = _clock.Position;
                                mediaPosition = _duration > 0 && request.Options.Loop ? target % _duration : target;
                                offset = target - mediaPosition;
                                await Io(() => { demux.Seek(TimeSpan.FromSeconds(mediaPosition)); Interlocked.Increment(ref _seeks); return true; }, token).ConfigureAwait(false);
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
                            cycle++;
                            mediaPosition = 0;
                            await Io(() => { demux.Seek(TimeSpan.Zero); Interlocked.Increment(ref _seeks); return true; }, token).ConfigureAwait(false);
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
        => VideoDecoderSlot.Open(mode, effective => new FFmpegVideoDecoder(request.Options.Filename,
            TimeSpan.Zero, token, decodeMode: effective, graphicsDevice: request.Video!.Device,
            graphicsDeviceType: request.Video.Context.GraphicsDeviceType,
            usesLinearColorspace: request.Video.Context.UsesLinearColorspace, demux: demux),
            reason => { lock (_gate) if (request.Generation == _generation) _recoveryMessage = $"Hardware fallback: {reason}"; });

    private async Task<double> RunCycle(Request request, FFmpegDemuxContext demux,
        FFmpegVideoDecoder? video, FFmpegAudioDecoder? audio, double minimum, double offset, long cycle, CancellationToken token)
    {
        using var videoPackets = new PacketQueue();
        using var audioPackets = new PacketQueue();
        using var cycleLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cycleToken = cycleLifetime.Token;
        demux.SetCancellation(cycleToken);
        var raw = Channel.CreateBounded<NativeVideoFrame>(new BoundedChannelOptions(2)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var videoLead = VideoFrameDeadline.Window + (video?.DelayFrames ?? 0) * _frameDuration;
        var io = Io(() => { Demux(demux, video?.StreamIndex ?? -1, audio?.StreamIndex ?? -1, videoPackets, audioPackets, offset, videoLead, cycleToken); return true; }, cycleToken);
        var decode = video is null ? Task.FromResult(0d) : DecodeVideo(video, videoPackets, raw.Writer, minimum, cycleToken);
        var convert = video is null ? Task.CompletedTask : ConvertVideo(request, video, raw.Reader, offset, cycle, cycleToken);
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
        PacketQueue video, PacketQueue audio, double offset, double videoLead, CancellationToken token)
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
                Maximum(ref _ioTicks, readTicks);
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
                        var weight = _recentVideoPacketBytes == 0 ? 1d : 1d / 16;
                        Volatile.Write(ref _recentVideoReadTicks, _recentVideoReadTicks + (readTicks - _recentVideoReadTicks) * weight);
                        Volatile.Write(ref _recentVideoPacketBytes, _recentVideoPacketBytes + (packet.Size - _recentVideoPacketBytes) * weight);
                        var timestamp = pointer->dts != ffmpeg.AV_NOPTS_VALUE ? pointer->dts : pointer->pts;
                        var timeBase = demux.Context->streams[videoIndex]->time_base;
                        if (timestamp != ffmpeg.AV_NOPTS_VALUE && timeBase.den > 0)
                        {
                            var timeline = timestamp * (double)timeBase.num / timeBase.den - demux.OriginSeconds + offset;
                            while (!_clock.Waiting)
                            {
                                token.ThrowIfCancellationRequested();
                                var wake = _wake.Next;
                                var ahead = timeline - _clock.Position - videoLead;
                                if (ahead <= 0) break;
                                if (Volatile.Read(ref _options).Play)
                                    PlaybackWait.ForChange(wake, TimeSpan.FromSeconds(Math.Min(ahead, 1)), token).GetAwaiter().GetResult();
                                else wake.WaitAsync(token).GetAwaiter().GetResult();
                            }
                        }
                    }
                    if (queue == audio && videoIndex >= 0)
                    {
                        packet.Discontinuity = discontinuity;
                        transferred = queue.TryWrite(packet);
                        if (!transferred) { queue.Clear(); discontinuity = true; Interlocked.Increment(ref _audioDiscontinuities); }
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

    private async Task<double> DecodeVideo(FFmpegVideoDecoder decoder, PacketQueue packets,
        ChannelWriter<NativeVideoFrame> output, double minimum, CancellationToken token)
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
                    Maximum(ref _decodeTicks, Stopwatch.GetTimestamp() - start);
                    return value;
                }, token, decoder.ThreadCount).ConfigureAwait(false);
                if (frame is null) return;
                end = Math.Max(end, frame.Time + frame.Duration);
                if (!pastPreroll && frame.Time <= minimum)
                { preroll?.Dispose(); preroll = frame; continue; }
                if (!pastPreroll)
                {
                    pastPreroll = true;
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
        finally { preroll?.Dispose(); output.TryComplete(); }
    }

    private async Task ConvertVideo(Request request, FFmpegVideoDecoder decoder, ChannelReader<NativeVideoFrame> raw,
        double offset, long cycle, CancellationToken token)
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
                    var target = _clock.Position;
                    bool preview;
                    bool progressNeeded;
                    lock (_gate)
                    {
                        preview = _preview && !_presented && _ready.Count == 0 && (!_options.Play || _clock.Waiting);
                        // Under sustained decode/I/O overload every frame may miss its deadline.
                        // Still publish the newest available image periodically, without slowing time
                        // or spending conversion on every expired frame. Do not resurrect an ended cycle.
                        progressNeeded = _ready.Count == 0 && (_duration <= 0 || target < offset + _duration)
                            && (!_presented || Stopwatch.GetElapsedTime(_lastPresentedStamp).TotalMilliseconds >= 250);
                    }
                    if (!preview)
                    {
                        while (raw.TryPeek(out var newer) && newer.Time + offset <= target)
                        {
                            if (!raw.TryRead(out newer)) break;
                            frame.Dispose(); frame = newer;
                            Interlocked.Increment(ref _droppedBeforeConvert);
                        }
                    }
                    var expired = !preview && VideoFrameDeadline.IsExpired(frame.Time + offset, frame.Duration, target);
                    if (expired && !progressNeeded)
                    {
                        Interlocked.Increment(ref _droppedBeforeConvert);
                        break;
                    }
                    bool space;
                    lock (_gate)
                    {
                        if (request.Generation != _generation) return;
                        space = _ready.Count < 6;
                    }
                    var ahead = frame.Time + offset - target - VideoFrameDeadline.Window;
                    if (!space || (!preview && ahead > 0))
                    {
                        if (ahead > 0 && _options.Play) await PlaybackWait.ForChange(wake, TimeSpan.FromSeconds(Math.Min(ahead, 1)), token).ConfigureAwait(false);
                        else await wake.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    var cpu = ResourceBudget.Cpu.Changed.Next;
                    var gpu = ResourceBudget.ForDevice(request.Video!.Device).Changed.Next;
                    DecodedVideoFrame converted;
                    try
                    {
                        converted = await PlaybackWork.Run(() =>
                        {
                            var start = Stopwatch.GetTimestamp();
                            var value = decoder.Convert(frame);
                            Interlocked.Increment(ref _conversions);
                            if (expired) Interlocked.Increment(ref _starvationConversions);
                            Maximum(ref _convertTicks, Stopwatch.GetTimestamp() - start);
                            return value;
                        }, token, device: request.Video.Device).ConfigureAwait(false);
                    }
                    catch (ResourceUnavailableException)
                    {
                        Interlocked.Increment(ref _resourceWaits);
                        Volatile.Write(ref _resourceBlocked, 1);
                        var waitStarted = Stopwatch.GetTimestamp();
                        await Task.WhenAny(wake, cpu, gpu).WaitAsync(token).ConfigureAwait(false);
                        _health.ObserveQueueWaitDuration(Stopwatch.GetElapsedTime(waitStarted));
                        continue;
                    }
                    Volatile.Write(ref _resourceBlocked, 0);
                    lock (_gate)
                    {
                        if (request.Generation != _generation || token.IsCancellationRequested) RetireLocked(converted);
                        else
                        {
                            _ready.Enqueue(new(request.Generation, cycle, frame.Time + offset, converted));
                            _clock.Ready();
                            _health.ObserveProducerDuration(TimeSpan.FromSeconds(Milliseconds(_decodeTicks + _convertTicks) / 1000));
                        }
                    }
                    _wake.Pulse();
                    break;
                }
            }
            finally { frame.Dispose(); }
        }
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

    private static Task<T> Io<T>(Func<T> action, CancellationToken token)
        => Task.Factory.StartNew(() => { token.ThrowIfCancellationRequested(); return action(); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private int SendVideoTimed(FFmpegVideoDecoder decoder, NativePacket? packet)
    {
        var start = Stopwatch.GetTimestamp();
        try { return Send(decoder, packet); }
        finally { Maximum(ref _decodeTicks, Stopwatch.GetTimestamp() - start); }
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
