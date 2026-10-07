using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>
/// Owns one video codec and its reusable converter; the playback session supplies the container.
/// </summary>
/// <remarks>
/// This type deliberately has no playback clock or renderer knowledge. It is a
/// synchronous worker primitive and must be called off the Gamma render thread.
/// The caller owns the demux context and must keep it alive until this decoder is disposed.
/// </remarks>
internal unsafe sealed class FFmpegVideoDecoder : IDisposable
{
    private CancellationToken _cancellationToken;
    private readonly FFmpegDemuxContext _demux;
    private readonly VideoFrameConverter _converter;
    private readonly DecodeMode _decodeMode;
    private readonly nint _graphicsDevice;
    private readonly GraphicsDeviceType _graphicsDeviceType;
    private readonly bool _usesLinearColorspace;
    private readonly AVCodecContext_get_format _getFormatCallback;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVBufferRef* _hardwareDeviceReference;
    private AVStream* _videoStream;
    private int _videoStreamIndex = -1;
    private bool _sourceDeclaresAlpha;
    private long _decodedFrameCount;
    private string _decodeStatus = "Software BGRA8";
    private bool _hardwareConfigured;
    private bool _hardwareFormatOffered;
    private bool _hardwareFormatSelected;
    private bool _disposed;
    private int _threadCount;
    public int ThreadCount => _threadCount;
    // FFmpeg already includes the frame-thread queue in delay. Reordering is
    // additional for frame-threaded decode; requested thread count is not another queue.
    public int DelayFrames => (_codecContext->active_thread_type & ffmpeg.FF_THREAD_FRAME) != 0
        ? Math.Max(0, _codecContext->delay) + Math.Max(0, _codecContext->has_b_frames)
        : Math.Max(_codecContext->delay, _codecContext->has_b_frames);

    public FFmpegVideoDecoder(
        FFmpegDemuxContext demux,
        CancellationToken cancellationToken,
        DecodeMode decodeMode = DecodeMode.Software,
        nint graphicsDevice = default,
        GraphicsDeviceType graphicsDeviceType = GraphicsDeviceType.None,
        bool usesLinearColorspace = false)
    {
        ArgumentNullException.ThrowIfNull(demux);

        _cancellationToken = cancellationToken;
        _getFormatCallback = SelectPixelFormat;
        _decodeMode = decodeMode;
        _graphicsDevice = graphicsDevice;
        _graphicsDeviceType = graphicsDeviceType;
        _usesLinearColorspace = usesLinearColorspace;
        _converter = new VideoFrameConverter(graphicsDevice, graphicsDeviceType, usesLinearColorspace, decodeMode);
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

    public FFmpegMediaInfo MediaInfo { get; }

    public string DecodeStatus => _converter.Status ?? _decodeStatus;

    public bool HardwareConfigured => _hardwareConfigured;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_threadCount > 0) { PlaybackWork.UnregisterDecoder(); _threadCount = 0; }
        _converter.Dispose();

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

        if (_hardwareDeviceReference is not null)
        {
            var hardwareDeviceReference = _hardwareDeviceReference;
            ffmpeg.av_buffer_unref(&hardwareDeviceReference);
            _hardwareDeviceReference = hardwareDeviceReference;
        }

        // The codec stores the pixel-format callback as a native function pointer.
        GC.KeepAlive(_getFormatCallback);
    }

    private FFmpegMediaInfo Open()
    {
        var formatContext = _demux.Context;

        AVCodec* decoder = null;
        _videoStreamIndex = ffmpeg.av_find_best_stream(
            formatContext,
            AVMediaType.AVMEDIA_TYPE_VIDEO,
            -1,
            -1,
            &decoder,
            0);
        FFmpegDemuxContext.Check(_videoStreamIndex, "find a video stream");

        if (decoder is null)
            throw new NotSupportedException("FFmpeg did not provide a decoder for the selected video stream.");

        _videoStream = formatContext->streams[_videoStreamIndex];
        if (_videoStream is null || _videoStream->codecpar is null)
            throw new InvalidDataException("The selected FFmpeg video stream has no codec parameters.");

        _sourceDeclaresAlpha = SourceDeclaresAlpha(_videoStream);
        _converter.DeclaresAlpha = _sourceDeclaresAlpha;
        decoder = SelectVideoDecoder(decoder);

        _codecContext = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codecContext is null)
            throw new OutOfMemoryException("FFmpeg could not allocate AVCodecContext.");

        FFmpegDemuxContext.Check(
            ffmpeg.avcodec_parameters_to_context(_codecContext, _videoStream->codecpar),
            "copy video codec parameters");
        // Auto threading can retain too many UHD software frames; eight keeps
        // decode parallel while bounding native frame memory.
        _threadCount = PlaybackWork.RegisterDecoder();
        if ((decoder->capabilities & (ffmpeg.AV_CODEC_CAP_FRAME_THREADS
            | ffmpeg.AV_CODEC_CAP_SLICE_THREADS | ffmpeg.AV_CODEC_CAP_OTHER_THREADS)) == 0)
            _threadCount = 1;
        _codecContext->thread_count = _threadCount;
        ConfigureHardwareDecoder();
        var codecOpenResult = ffmpeg.avcodec_open2(_codecContext, decoder, null);
        if (codecOpenResult < 0 && _hardwareConfigured)
            ThrowHardware(codecOpenResult, "open the D3D11VA video decoder");
        FFmpegDemuxContext.Check(codecOpenResult, "open the video decoder");

        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("FFmpeg could not allocate AVFrame.");

        var frameRate = NormalizeFrameRate(_videoStream->avg_frame_rate);
        if (frameRate.N == 0)
            frameRate = NormalizeFrameRate(_videoStream->r_frame_rate);

        var duration = ReadDuration(formatContext, _videoStream);
        var codecParameters = _videoStream->codecpar;

        return new FFmpegMediaInfo(
            Width: codecParameters->width,
            Height: codecParameters->height,
            Duration: duration,
            FrameRate: frameRate,
            VideoCodec: ffmpeg.avcodec_get_name(codecParameters->codec_id));
    }

    private AVCodec* SelectVideoDecoder(AVCodec* defaultDecoder)
    {
        if (!_sourceDeclaresAlpha || _decodeMode == DecodeMode.Hardware)
            return defaultDecoder;

        var decoderName = _videoStream->codecpar->codec_id switch
        {
            AVCodecID.AV_CODEC_ID_VP8 => "libvpx",
            AVCodecID.AV_CODEC_ID_VP9 => "libvpx-vp9",
            _ => null
        };
        if (decoderName is null)
            return defaultDecoder;

        var preferredDecoder = ffmpeg.avcodec_find_decoder_by_name(decoderName);
        return preferredDecoder is null ? defaultDecoder : preferredDecoder;
    }

    private static bool SourceDeclaresAlpha(AVStream* stream)
    {
        if (stream->codecpar->alpha_mode != AVAlphaMode.AVALPHA_MODE_UNSPECIFIED)
            return true;

        var entry = ffmpeg.av_dict_get(stream->metadata, "alpha_mode", null, 0);
        if (entry is null)
            return false;

        var value = Marshal.PtrToStringUTF8((nint)entry->value);
        return !string.IsNullOrWhiteSpace(value) && value != "0";
    }

    private void ConfigureHardwareDecoder()
    {
        if (_decodeMode == DecodeMode.Software)
        {
            _decodeStatus = _usesLinearColorspace
                ? "Software linear RGBA16F"
                : "Software nonlinear BGRA8";
            return;
        }

        var devicePointer = _graphicsDevice;
        if (_graphicsDeviceType != GraphicsDeviceType.Direct3D11 || devicePointer == nint.Zero)
        {
            if (_decodeMode == DecodeMode.Hardware)
                throw new FFmpegHardwareException("Hardware mode requires a Direct3D11 consumer with Prefer GPU enabled.");
            _decodeStatus = _usesLinearColorspace
                ? "Software linear RGBA16F fallback; consumer supplied no Direct3D11 device"
                : "Software nonlinear BGRA8 fallback; consumer supplied no Direct3D11 device";
            return;
        }

        try
        {
            _hardwareDeviceReference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (_hardwareDeviceReference is null)
                throw new FFmpegHardwareException("FFmpeg could not allocate a D3D11VA device context.");

            var hardwareContext = (AVHWDeviceContext*)_hardwareDeviceReference->data;
            if (hardwareContext is null || hardwareContext->hwctx is null)
                throw new FFmpegHardwareException("FFmpeg returned an invalid D3D11VA device context.");

            var d3d11Context = (AVD3D11VADeviceContext*)hardwareContext->hwctx;
            D3D11Interop.AddRef((void*)devicePointer);
            d3d11Context->device = (ID3D11Device*)devicePointer;

            var initResult = ffmpeg.av_hwdevice_ctx_init(_hardwareDeviceReference);
            if (initResult < 0)
                ThrowHardware(initResult, "initialize D3D11VA on the consumer device");

            var codecReference = ffmpeg.av_buffer_ref(_hardwareDeviceReference);
            if (codecReference is null)
                throw new OutOfMemoryException("FFmpeg could not reference the D3D11VA device context.");

            _codecContext->get_format = _getFormatCallback;
            _codecContext->hw_device_ctx = codecReference;
            _codecContext->extra_hw_frames = 8;
            _converter.ConfigureHardware(d3d11Context);
            _hardwareConfigured = true;
            _decodeStatus = "D3D11VA configured on the consumer device";
        }
        catch (Exception exception) when (
            _decodeMode == DecodeMode.Auto
            && exception is not OperationCanceledException)
        {
            _converter.ClearHardware();
            if (_hardwareDeviceReference is not null)
            {
                var reference = _hardwareDeviceReference;
                ffmpeg.av_buffer_unref(&reference);
                _hardwareDeviceReference = reference;
            }
            if (_codecContext->hw_device_ctx is not null)
            {
                var codecReference = _codecContext->hw_device_ctx;
                ffmpeg.av_buffer_unref(&codecReference);
                _codecContext->hw_device_ctx = codecReference;
            }
            _codecContext->get_format = default;
            _hardwareConfigured = false;
            var output = _usesLinearColorspace ? "linear RGBA16F" : "nonlinear BGRA8";
            _decodeStatus = $"Software {output} fallback; {exception.Message}";
        }
    }

    private AVPixelFormat SelectPixelFormat(AVCodecContext* codecContext, AVPixelFormat* formats)
    {
        for (var current = formats; current is not null && *current != AVPixelFormat.AV_PIX_FMT_NONE; current++)
        {
            if (*current != AVPixelFormat.AV_PIX_FMT_D3D11)
                continue;

            _hardwareFormatOffered = true;
            _hardwareFormatSelected = true;
            return AVPixelFormat.AV_PIX_FMT_D3D11;
        }

        _hardwareFormatSelected = false;
        return ffmpeg.avcodec_default_get_format(codecContext, formats);
    }

    private static void ThrowHardware(int errorCode, string operation)
    {
        Span<byte> buffer = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* pointer = buffer)
        {
            ffmpeg.av_strerror(errorCode, pointer, (ulong)buffer.Length);
            var message = Marshal.PtrToStringUTF8((nint)pointer) ?? "Unknown FFmpeg error";
            throw new FFmpegHardwareException($"Failed to {operation}: {message} ({errorCode}).");
        }
    }

    private TimeSpan ReadTimecode(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;

        double seconds;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
        {
            var frameRate = MediaInfo.FrameRate;
            seconds = frameRate.N > 0
                ? _decodedFrameCount * (double)frameRate.D / frameRate.N
                : 0d;
        }
        else
        {
            seconds = timestamp * ToDouble(_videoStream->time_base) - _demux.OriginSeconds;
        }

        return TimeSpan.FromSeconds(Math.Max(0d, seconds));
    }

    public int StreamIndex => _videoStreamIndex;
    public void SetCancellation(CancellationToken token) => _cancellationToken = token;

    public void Flush(TimeSpan position, CancellationToken token)
    {
        _cancellationToken = token;
        ffmpeg.avcodec_flush_buffers(_codecContext);
        _decodedFrameCount = MediaInfo.FrameRate.N > 0
            ? (long)(position.TotalSeconds * MediaInfo.FrameRate.N / MediaInfo.FrameRate.D) : 0;
    }

    public int SendPacket(AVPacket* packet) => ffmpeg.avcodec_send_packet(_codecContext, packet);

    public NativeVideoFrame? ReceiveRaw()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var result = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (result == Again || result == ffmpeg.AVERROR_EOF) return null;
        FFmpegDemuxContext.Check(result, "receive video frame");
        try
        {
            var time = ReadTimecode(_frame).TotalSeconds;
            var duration = _frame->duration > 0 ? _frame->duration * ToDouble(_videoStream->time_base)
                : MediaInfo.FrameRate.N > 0 ? MediaInfo.FrameRate.D / (double)MediaInfo.FrameRate.N : 0;
            _decodedFrameCount++;
            var timestamp = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? _frame->best_effort_timestamp : _frame->pts;
            double? sourceTime = timestamp == ffmpeg.AV_NOPTS_VALUE ? null : timestamp * ToDouble(_videoStream->time_base);
            return new NativeVideoFrame(_frame, time, duration, sourceTime);
        }
        finally { ffmpeg.av_frame_unref(_frame); }
    }

    public DecodedVideoFrame Convert(NativeVideoFrame frame)
        => _converter.Convert(frame.Frame, TimeSpan.FromSeconds(frame.Time), MediaInfo.FrameRate,
            _hardwareConfigured, _hardwareFormatSelected, _hardwareFormatOffered, _cancellationToken);

    private static TimeSpan ReadDuration(AVFormatContext* formatContext, AVStream* stream)
    {
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(stream->duration * ToDouble(stream->time_base));

        if (formatContext->duration > 0 && formatContext->duration != ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.FromSeconds(formatContext->duration / (double)ffmpeg.AV_TIME_BASE);

        return TimeSpan.Zero;
    }

    private static (int N, int D) NormalizeFrameRate(AVRational rational)
        => rational.num > 0 && rational.den > 0
            ? (rational.num, rational.den)
            : default;

    private static double ToDouble(AVRational rational)
        => rational.den == 0 ? 0d : rational.num / (double)rational.den;

    private static int Again => ffmpeg.AVERROR(ffmpeg.EAGAIN);
}
