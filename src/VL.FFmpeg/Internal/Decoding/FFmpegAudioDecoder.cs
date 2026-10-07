using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>Owns an audio codec and resampler.</summary>
/// <remarks>The caller must keep the demux context alive until this decoder is disposed.</remarks>
internal unsafe sealed class FFmpegAudioDecoder : IDisposable
{
    private CancellationToken _cancellationToken;
    private readonly FFmpegDemuxContext _demux;
    private double? _nextOutputTime;
    private int _requestedSampleRate;
    private int _requestedChannelCount;
    private TimeSpan _minimumTimecode;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private SwrContext* _swrContext;
    private AVStream* _audioStream;
    private int _audioStreamIndex = -1;
    private int _inputSampleRate;
    private int _inputChannelCount;
    private AVSampleFormat _inputSampleFormat;
    private long _decodedSampleCount;
    private bool _disposed;

    public FFmpegAudioDecoder(
        FFmpegDemuxContext demux,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demux);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount < 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _cancellationToken = cancellationToken;
        _requestedSampleRate = sampleRate;
        _requestedChannelCount = channelCount;
        _demux = demux;

        try
        {
            MediaInfo = Open();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public FFmpegAudioMediaInfo MediaInfo { get; }

    public int OutputSampleRate => _requestedSampleRate;

    public int OutputChannelCount => _requestedChannelCount > 0
        ? _requestedChannelCount
        : MediaInfo.ChannelCount;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_swrContext is not null)
        {
            var swrContext = _swrContext;
            ffmpeg.swr_free(&swrContext);
            _swrContext = swrContext;
        }
        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = frame;
        }
        if (_codecContext is not null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = codecContext;
        }
    }

    private DecodedAudioFrame? ConvertFrame(AVFrame* frame)
    {
        if (frame->nb_samples <= 0 || frame->sample_rate <= 0)
            throw new InvalidDataException("FFmpeg returned an invalid audio frame.");
        if (frame->extended_data is null)
            throw new InvalidDataException("FFmpeg returned audio without sample planes.");

        ConfigureResampler(frame);
        var timestamp = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        double? sourceTime = timestamp == ffmpeg.AV_NOPTS_VALUE ? null
            : timestamp * ToDouble(_audioStream->time_base)
                - ffmpeg.swr_get_delay(_swrContext, frame->sample_rate) / (double)frame->sample_rate;
        var outputCapacity = ffmpeg.swr_get_out_samples(_swrContext, frame->nb_samples);
        FFmpegDemuxContext.Check(outputCapacity, "calculate resampled audio capacity");
        outputCapacity = Math.Max(1, outputCapacity);

        var channelCount = OutputChannelCount;
        var samples = new float[checked(channelCount * outputCapacity)];
        var outputData = stackalloc byte*[channelCount];
        int converted;
        fixed (float* samplesPointer = samples)
        {
            for (var channel = 0; channel < channelCount; channel++)
                outputData[channel] = (byte*)(samplesPointer + channel * outputCapacity);
            converted = ffmpeg.swr_convert(
                _swrContext,
                outputData,
                outputCapacity,
                frame->extended_data,
                frame->nb_samples);
        }
        FFmpegDemuxContext.Check(converted, "resample a decoded audio frame");
        if (converted == 0)
            return null;

        var timecode = TimeSpan.FromSeconds(_nextOutputTime ?? ReadTimecode(frame).TotalSeconds);
        _nextOutputTime = timecode.TotalSeconds + converted / (double)OutputSampleRate;
        _decodedSampleCount += converted;
        var sampleOffset = 0;
        if (timecode < _minimumTimecode)
        {
            sampleOffset = checked((int)Math.Ceiling(
                (_minimumTimecode - timecode).TotalSeconds * OutputSampleRate));
            if (sampleOffset >= converted)
                return null;
            timecode += TimeSpan.FromSeconds(sampleOffset / (double)OutputSampleRate);
        }

        return new DecodedAudioFrame(
            samples,
            channelCount,
            converted - sampleOffset,
            sampleOffset,
            OutputSampleRate,
            timecode) { SourceTime = sourceTime + sampleOffset / (double)OutputSampleRate };
    }

    private void ConfigureResampler(AVFrame* frame)
    {
        var inputFormat = (AVSampleFormat)frame->format;
        var inputChannels = frame->ch_layout.nb_channels;
        if (inputChannels <= 0)
            inputChannels = MediaInfo.ChannelCount;
        if (_swrContext is not null
            && _inputSampleRate == frame->sample_rate
            && _inputChannelCount == inputChannels
            && _inputSampleFormat == inputFormat)
        {
            return;
        }

        if (_swrContext is not null)
        {
            var oldContext = _swrContext;
            ffmpeg.swr_free(&oldContext);
            _swrContext = oldContext;
        }

        var inputLayout = default(AVChannelLayout);
        var outputLayout = default(AVChannelLayout);
        try
        {
            if (frame->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                ffmpeg.av_channel_layout_default(&inputLayout, inputChannels);
            else
                FFmpegDemuxContext.Check(
                    ffmpeg.av_channel_layout_copy(&inputLayout, &frame->ch_layout),
                    "copy the decoded audio channel layout");
            ffmpeg.av_channel_layout_default(&outputLayout, OutputChannelCount);

            var context = _swrContext;
            var configureResult = ffmpeg.swr_alloc_set_opts2(
                &context,
                &outputLayout,
                AVSampleFormat.AV_SAMPLE_FMT_FLTP,
                OutputSampleRate,
                &inputLayout,
                inputFormat,
                frame->sample_rate,
                0,
                null);
            _swrContext = context;
            FFmpegDemuxContext.Check(configureResult, "configure the audio resampler");
            if (_swrContext is null)
                throw new OutOfMemoryException("FFmpeg could not allocate an audio resampler.");
            FFmpegDemuxContext.Check(ffmpeg.swr_init(_swrContext), "initialize the audio resampler");
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outputLayout);
            ffmpeg.av_channel_layout_uninit(&inputLayout);
        }

        _inputSampleRate = frame->sample_rate;
        _inputChannelCount = inputChannels;
        _inputSampleFormat = inputFormat;
    }

    private TimeSpan ReadTimecode(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(_decodedSampleCount / (double)OutputSampleRate);

        var seconds = timestamp * ToDouble(_audioStream->time_base) - _demux.OriginSeconds;
        return TimeSpan.FromSeconds(Math.Max(0d, seconds));
    }

    public int StreamIndex => _audioStreamIndex;
    // Called by the audio owner; neither a container seek nor a codec flush is needed.
    public void ReconfigureOutput(int rate, int channels)
    {
        _requestedSampleRate = rate;
        _requestedChannelCount = channels;
        _nextOutputTime = null;
        if (_swrContext is not null)
        {
            var context = _swrContext;
            ffmpeg.swr_free(&context);
            _swrContext = null;
        }
    }
    public int SendPacket(AVPacket* packet) => ffmpeg.avcodec_send_packet(_codecContext, packet);
    public DecodedAudioFrame? Receive()
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var result = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (result == Again || result == ffmpeg.AVERROR_EOF) return null;
            FFmpegDemuxContext.Check(result, "receive audio frame");
            try
            {
                var frame = ConvertFrame(_frame);
                if (frame is not null) return frame;
            }
            finally { ffmpeg.av_frame_unref(_frame); }
        }
    }

    public void Flush(TimeSpan position, int rate, int channels, CancellationToken token)
    {
        var outputChanged = _requestedSampleRate != rate || _requestedChannelCount != channels;
        _cancellationToken = token;
        _requestedSampleRate = rate;
        _requestedChannelCount = channels;
        _minimumTimecode = position;
        _nextOutputTime = null;
        _decodedSampleCount = (long)(position.TotalSeconds * rate);
        ffmpeg.avcodec_flush_buffers(_codecContext);
        if (_swrContext is not null)
        {
            if (outputChanged)
            {
                var context = _swrContext;
                ffmpeg.swr_free(&context);
                _swrContext = null;
            }
            else
            {
                ffmpeg.swr_close(_swrContext);
                FFmpegDemuxContext.Check(ffmpeg.swr_init(_swrContext), "reset audio resampler");
            }
        }
    }

    public bool FlushResampler(Func<DecodedAudioFrame, bool> accept)
    {
        if (_swrContext is null) return true;
        var planes = stackalloc byte*[OutputChannelCount];
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var capacity = Math.Max(1, ffmpeg.swr_get_out_samples(_swrContext, 0));
            var samples = new float[checked(capacity * OutputChannelCount)];
            int count;
            fixed (float* p = samples)
            {
                for (var c = 0; c < OutputChannelCount; c++) planes[c] = (byte*)(p + c * capacity);
                count = ffmpeg.swr_convert(_swrContext, planes, capacity, null, 0);
            }
            FFmpegDemuxContext.Check(count, "flush audio resampler");
            if (count == 0) return true;
            var time = _nextOutputTime ?? 0;
            _nextOutputTime = time + count / (double)OutputSampleRate;
            if (!accept(new DecodedAudioFrame(samples, OutputChannelCount, count, 0, OutputSampleRate, TimeSpan.FromSeconds(time))))
                return false;
        }
    }

    private static TimeSpan ReadDuration(AVFormatContext* formatContext, AVStream* stream)
    {
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(stream->duration * ToDouble(stream->time_base));
        if (formatContext->duration > 0 && formatContext->duration != ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(formatContext->duration / (double)ffmpeg.AV_TIME_BASE);
        return TimeSpan.Zero;
    }

    private static double ToDouble(AVRational rational)
        => rational.den == 0 ? 0d : rational.num / (double)rational.den;

    private static int Again => ffmpeg.AVERROR(ffmpeg.EAGAIN);

    private FFmpegAudioMediaInfo Open()
    {
        var formatContext = _demux.Context;
        AVCodec* decoder = null;
        _audioStreamIndex = ffmpeg.av_find_best_stream(
            formatContext,
            AVMediaType.AVMEDIA_TYPE_AUDIO,
            -1,
            -1,
            &decoder,
            0);
        FFmpegDemuxContext.Check(_audioStreamIndex, "find an audio stream");
        if (decoder is null)
            throw new NotSupportedException("FFmpeg did not provide a decoder for the selected audio stream.");

        _audioStream = formatContext->streams[_audioStreamIndex];
        if (_audioStream is null || _audioStream->codecpar is null)
            throw new InvalidDataException("The selected FFmpeg audio stream has no codec parameters.");

        _codecContext = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codecContext is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVCodecContext.");
        FFmpegDemuxContext.Check(
            ffmpeg.avcodec_parameters_to_context(_codecContext, _audioStream->codecpar),
            "copy audio codec parameters");
        FFmpegDemuxContext.Check(ffmpeg.avcodec_open2(_codecContext, decoder, null), "open the audio decoder");

        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVFrame.");

        var parameters = _audioStream->codecpar;
        var duration = ReadDuration(formatContext, _audioStream);
        return new FFmpegAudioMediaInfo(
            Duration: duration,
            ChannelCount: parameters->ch_layout.nb_channels,
            SampleRate: parameters->sample_rate,
            AudioCodec: ffmpeg.avcodec_get_name(parameters->codec_id));
    }
}
