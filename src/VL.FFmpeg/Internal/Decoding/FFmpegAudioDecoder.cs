using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class FFmpegAudioDecoder : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly AVIOInterruptCB_callback _interruptCallback;
    private readonly int _requestedSampleRate;
    private readonly int _requestedChannelCount;
    private readonly TimeSpan _minimumTimecode;
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVPacket* _packet;
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
        string filename,
        TimeSpan initialPosition,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken,
        string? nativeRuntimePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (initialPosition < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialPosition));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount < 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _cancellationToken = cancellationToken;
        _interruptCallback = Interrupt;
        _requestedSampleRate = sampleRate;
        _requestedChannelCount = channelCount;
        _minimumTimecode = initialPosition;

        try
        {
            MediaInfo = Open(filename, initialPosition, nativeRuntimePath);
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

    public void Decode(Func<DecodedAudioFrame, bool> acceptFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(acceptFrame);

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var readResult = ffmpeg.av_read_frame(_formatContext, _packet);
            if (readResult == ffmpeg.AVERROR_EOF)
            {
                DrainDecoder(acceptFrame);
                return;
            }
            if (readResult < 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Throw(readResult, "read the next audio packet");
            }

            try
            {
                if (_packet->stream_index != _audioStreamIndex)
                    continue;

                int sendResult;
                while ((sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet)) == Again)
                {
                    if (!ReceiveFrames(acceptFrame))
                        return;
                }
                if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
                    Throw(sendResult, "send an audio packet to the decoder");
                if (!ReceiveFrames(acceptFrame))
                    return;
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

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
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = packet;
        }
        if (_codecContext is not null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = codecContext;
        }
        if (_formatContext is not null)
        {
            var formatContext = _formatContext;
            ffmpeg.avformat_close_input(&formatContext);
            _formatContext = formatContext;
        }
        GC.KeepAlive(_interruptCallback);
    }

    private bool ReceiveFrames(Func<DecodedAudioFrame, bool> acceptFrame)
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == Again || receiveResult == ffmpeg.AVERROR_EOF)
                return true;
            if (receiveResult < 0)
                Throw(receiveResult, "receive a decoded audio frame");

            try
            {
                var frame = ConvertFrame(_frame);
                if (frame is not null && !acceptFrame(frame))
                    return false;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private DecodedAudioFrame? ConvertFrame(AVFrame* frame)
    {
        if (frame->nb_samples <= 0 || frame->sample_rate <= 0)
            throw new InvalidDataException("FFmpeg returned an invalid audio frame.");
        if (frame->extended_data is null)
            throw new InvalidDataException("FFmpeg returned audio without sample planes.");

        ConfigureResampler(frame);
        var outputCapacity = ffmpeg.swr_get_out_samples(_swrContext, frame->nb_samples);
        if (outputCapacity < 0)
            Throw(outputCapacity, "calculate resampled audio capacity");
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
        if (converted < 0)
            Throw(converted, "resample a decoded audio frame");
        if (converted == 0)
            return null;

        var timecode = ReadTimecode(frame);
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
            timecode);
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
                Check(
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
            Check(configureResult, "configure the audio resampler");
            if (_swrContext is null)
                throw new OutOfMemoryException("FFmpeg could not allocate an audio resampler.");
            Check(ffmpeg.swr_init(_swrContext), "initialize the audio resampler");
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

    private bool DrainDecoder(Func<DecodedAudioFrame, bool> acceptFrame)
    {
        int result;
        while ((result = ffmpeg.avcodec_send_packet(_codecContext, null)) == Again)
        {
            if (!ReceiveFrames(acceptFrame))
                return false;
        }
        if (result < 0 && result != Again && result != ffmpeg.AVERROR_EOF)
            Throw(result, "drain the audio decoder");
        return ReceiveFrames(acceptFrame);
    }

    private TimeSpan ReadTimecode(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(_decodedSampleCount / (double)OutputSampleRate);

        var streamStart = _audioStream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0L
            : _audioStream->start_time;
        var seconds = (timestamp - streamStart) * ToDouble(_audioStream->time_base);
        return TimeSpan.FromSeconds(Math.Max(0d, seconds));
    }

    private void Seek(TimeSpan position)
    {
        var timeBase = ToDouble(_audioStream->time_base);
        if (timeBase <= 0d)
            throw new NotSupportedException("The audio stream has no usable time base for seeking.");
        var streamStart = _audioStream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0L
            : _audioStream->start_time;
        var target = checked(streamStart + (long)Math.Round(position.TotalSeconds / timeBase));
        Check(
            ffmpeg.av_seek_frame(
                _formatContext,
                _audioStreamIndex,
                target,
                ffmpeg.AVSEEK_FLAG_BACKWARD),
            $"seek audio to {position.TotalSeconds:0.###} seconds");
        ffmpeg.avcodec_flush_buffers(_codecContext);
        _decodedSampleCount = checked((long)Math.Round(position.TotalSeconds * OutputSampleRate));
    }

    private int Interrupt(void* _)
        => _cancellationToken.IsCancellationRequested ? 1 : 0;

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

    private static void Check(int result, string operation)
    {
        if (result < 0)
            Throw(result, operation);
    }

    private static void Throw(int errorCode, string operation)
    {
        Span<byte> buffer = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* pointer = buffer)
        {
            ffmpeg.av_strerror(errorCode, pointer, (ulong)buffer.Length);
            var message = Marshal.PtrToStringUTF8((nint)pointer) ?? "Unknown FFmpeg error";
            throw new FFmpegDecodeException(operation, errorCode, message);
        }
    }

    private FFmpegAudioMediaInfo Open(
        string filename,
        TimeSpan initialPosition,
        string? nativeRuntimePath)
    {
        var runtime = FFmpegRuntime.Probe(nativeRuntimePath);
        if (!runtime.Available)
            throw new InvalidOperationException(runtime.Status);

        _cancellationToken.ThrowIfCancellationRequested();
        _formatContext = ffmpeg.avformat_alloc_context();
        if (_formatContext is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVFormatContext.");
        _formatContext->interrupt_callback = new AVIOInterruptCB
        {
            callback = _interruptCallback,
            opaque = null
        };

        var formatContext = _formatContext;
        var openResult = ffmpeg.avformat_open_input(&formatContext, filename, null, null);
        _formatContext = formatContext;
        if (openResult < 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Throw(openResult, $"open '{filename}' for audio");
        }

        Check(ffmpeg.avformat_find_stream_info(_formatContext, null), "inspect audio stream information");
        AVCodec* decoder = null;
        _audioStreamIndex = ffmpeg.av_find_best_stream(
            _formatContext,
            AVMediaType.AVMEDIA_TYPE_AUDIO,
            -1,
            -1,
            &decoder,
            0);
        Check(_audioStreamIndex, "find an audio stream");
        if (decoder is null)
            throw new NotSupportedException("FFmpeg did not provide a decoder for the selected audio stream.");

        _audioStream = _formatContext->streams[_audioStreamIndex];
        if (_audioStream is null || _audioStream->codecpar is null)
            throw new InvalidDataException("The selected FFmpeg audio stream has no codec parameters.");

        _codecContext = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codecContext is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVCodecContext.");
        Check(
            ffmpeg.avcodec_parameters_to_context(_codecContext, _audioStream->codecpar),
            "copy audio codec parameters");
        Check(ffmpeg.avcodec_open2(_codecContext, decoder, null), "open the audio decoder");

        _packet = ffmpeg.av_packet_alloc();
        if (_packet is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVPacket.");
        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("FFmpeg could not allocate an audio AVFrame.");

        var parameters = _audioStream->codecpar;
        var duration = ReadDuration(_formatContext, _audioStream);
        if (initialPosition > TimeSpan.Zero)
            Seek(initialPosition);

        return new FFmpegAudioMediaInfo(
            Duration: duration,
            ChannelCount: parameters->ch_layout.nb_channels,
            SampleRate: parameters->sample_rate,
            AudioCodec: ffmpeg.avcodec_get_name(parameters->codec_id));
    }
}
