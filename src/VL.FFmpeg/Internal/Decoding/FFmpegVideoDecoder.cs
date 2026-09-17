using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>
/// Owns one libavformat/libavcodec video decode pipeline.
/// </summary>
/// <remarks>
/// This type deliberately has no playback clock or renderer knowledge. It is a
/// synchronous worker primitive and must be called off the Gamma render thread.
/// </remarks>
internal unsafe sealed class FFmpegVideoDecoder : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly AVIOInterruptCB_callback _interruptCallback;
    private readonly DecodeMode _decodeMode;
    private readonly nint _graphicsDevice;
    private readonly GraphicsDeviceType _graphicsDeviceType;
    private readonly bool _usesLinearColorspace;
    private readonly AVCodecContext_get_format _getFormatCallback;
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _swsContext;
    private AVBufferRef* _hardwareDeviceReference;
    private D3D11TexturePool? _texturePool;
    private SoftwareD3D11FrameConverter? _softwareGpuConverter;
    private string? _softwareGpuUnavailableReason;
    private string? _softwareGpuFrameStatus;
    private AVStream* _videoStream;
    private int _videoStreamIndex = -1;
    private long _decodedFrameCount;
    private TimeSpan _minimumTimecode;
    private string _decodeStatus = "Software BGRA8";
    private bool _hardwareConfigured;
    private bool _hardwareFormatOffered;
    private bool _hardwareFormatSelected;
    private bool _disposed;

    public FFmpegVideoDecoder(
        string filename,
        TimeSpan initialPosition,
        CancellationToken cancellationToken,
        string? nativeRuntimePath = null,
        DecodeMode decodeMode = DecodeMode.Software,
        nint graphicsDevice = default,
        GraphicsDeviceType graphicsDeviceType = GraphicsDeviceType.None,
        bool usesLinearColorspace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (initialPosition < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialPosition));

        _cancellationToken = cancellationToken;
        _interruptCallback = Interrupt;
        _getFormatCallback = SelectPixelFormat;
        _decodeMode = decodeMode;
        _graphicsDevice = graphicsDevice;
        _graphicsDeviceType = graphicsDeviceType;
        _usesLinearColorspace = usesLinearColorspace;
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

    public FFmpegMediaInfo MediaInfo { get; }

    public string DecodeStatus => _decodeStatus;

    public bool HardwareConfigured => _hardwareConfigured;

    /// <summary>
    /// Decodes until EOF, cancellation, or until <paramref name="acceptFrame"/>
    /// returns false. Returning false is a normal early stop. The callback owns
    /// every frame it receives.
    /// </summary>
    public void Decode(Func<DecodedVideoFrame, bool> acceptFrame)
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
                Throw(readResult, "read the next packet");
            }

            try
            {
                if (_packet->stream_index != _videoStreamIndex)
                    continue;

                int sendResult;
                while ((sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet)) == Again)
                {
                    if (!ReceiveFrames(acceptFrame))
                        return;
                }

                if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
                    Throw(sendResult, "send a video packet to the decoder");

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

        if (_swsContext is not null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        _texturePool?.Dispose();
        _texturePool = null;

        _softwareGpuConverter?.Dispose();
        _softwareGpuConverter = null;

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

        if (_hardwareDeviceReference is not null)
        {
            var hardwareDeviceReference = _hardwareDeviceReference;
            ffmpeg.av_buffer_unref(&hardwareDeviceReference);
            _hardwareDeviceReference = hardwareDeviceReference;
        }

        if (_formatContext is not null)
        {
            var formatContext = _formatContext;
            ffmpeg.avformat_close_input(&formatContext);
            _formatContext = formatContext;
        }

        // The native AVIOInterruptCB stores only a function pointer.
        GC.KeepAlive(_interruptCallback);
        GC.KeepAlive(_getFormatCallback);
    }

    private FFmpegMediaInfo Open(
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
            throw new OutOfMemoryException("FFmpeg could not allocate AVFormatContext.");

        _formatContext->interrupt_callback = new AVIOInterruptCB
        {
            callback = _interruptCallback,
            opaque = null
        };

        var formatContext = _formatContext;
        var openResult = ffmpeg.avformat_open_input(
            &formatContext,
            filename,
            null,
            null);
        _formatContext = formatContext;
        if (openResult < 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Throw(openResult, $"open '{filename}'");
        }

        Check(ffmpeg.avformat_find_stream_info(_formatContext, null), "inspect stream information");

        AVCodec* decoder = null;
        _videoStreamIndex = ffmpeg.av_find_best_stream(
            _formatContext,
            AVMediaType.AVMEDIA_TYPE_VIDEO,
            -1,
            -1,
            &decoder,
            0);
        Check(_videoStreamIndex, "find a video stream");

        if (decoder is null)
            throw new NotSupportedException("FFmpeg did not provide a decoder for the selected video stream.");

        _videoStream = _formatContext->streams[_videoStreamIndex];
        if (_videoStream is null || _videoStream->codecpar is null)
            throw new InvalidDataException("The selected FFmpeg video stream has no codec parameters.");

        _codecContext = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codecContext is null)
            throw new OutOfMemoryException("FFmpeg could not allocate AVCodecContext.");

        Check(
            ffmpeg.avcodec_parameters_to_context(_codecContext, _videoStream->codecpar),
            "copy video codec parameters");
        // Auto threading can retain too many UHD software frames; eight keeps
        // decode parallel while bounding native frame memory.
        _codecContext->thread_count = Math.Min(Environment.ProcessorCount, 8);
        ConfigureHardwareDecoder();
        var codecOpenResult = ffmpeg.avcodec_open2(_codecContext, decoder, null);
        if (codecOpenResult < 0 && _hardwareConfigured)
            ThrowHardware(codecOpenResult, "open the D3D11VA video decoder");
        Check(codecOpenResult, "open the video decoder");

        _packet = ffmpeg.av_packet_alloc();
        if (_packet is null)
            throw new OutOfMemoryException("FFmpeg could not allocate AVPacket.");

        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("FFmpeg could not allocate AVFrame.");

        var frameRate = NormalizeFrameRate(_videoStream->avg_frame_rate);
        if (frameRate.N == 0)
            frameRate = NormalizeFrameRate(_videoStream->r_frame_rate);

        var duration = ReadDuration(_formatContext, _videoStream);
        var codecParameters = _videoStream->codecpar;

        if (initialPosition > TimeSpan.Zero)
            Seek(initialPosition);

        return new FFmpegMediaInfo(
            Width: codecParameters->width,
            Height: codecParameters->height,
            Duration: duration,
            FrameRate: frameRate,
            VideoCodec: ffmpeg.avcodec_get_name(codecParameters->codec_id));
    }

    private bool ReceiveFrames(Func<DecodedVideoFrame, bool> acceptFrame)
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == Again || receiveResult == ffmpeg.AVERROR_EOF)
                return true;
            if (receiveResult < 0)
                Throw(receiveResult, "receive a decoded video frame");

            try
            {
                var frame = ConvertFrame(_frame);
                _decodedFrameCount++;
                if (frame.Timecode + TimeSpan.FromMilliseconds(1) < _minimumTimecode)
                {
                    frame.Dispose();
                    continue;
                }
                if (!acceptFrame(frame))
                    return false;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private DecodedVideoFrame ConvertFrame(AVFrame* frame)
    {
        if (frame->width <= 0 || frame->height <= 0)
            throw new InvalidDataException("FFmpeg returned a video frame with invalid dimensions.");

        var pixelFormat = (AVPixelFormat)frame->format;
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            if (_texturePool is null)
                throw new FFmpegHardwareException("D3D11VA selected without a GPU texture pool.");

            var lease = _texturePool.Convert(
                frame,
                MediaInfo.FrameRate,
                _cancellationToken,
                out var hardwareStatus);
            _decodeStatus = hardwareStatus;
            return new GpuDecodedVideoFrame(
                lease,
                ReadTimecode(frame),
                MediaInfo.FrameRate,
                hardwareStatus);
        }

        if (_decodeMode == DecodeMode.Hardware)
        {
            var reason = _hardwareFormatOffered
                ? $"FFmpeg selected software pixel format {pixelFormat} instead of D3D11VA."
                : "The selected codec did not offer AV_PIX_FMT_D3D11.";
            throw new FFmpegHardwareException(reason);
        }

        var fallbackStatus = _hardwareConfigured && !_hardwareFormatSelected
            ? $"Hardware fallback; D3D11VA was not selected ({pixelFormat}); "
            : string.Empty;
        var color = VideoColorInfo.Resolve(
            frame->colorspace,
            frame->color_range,
            frame->color_trc,
            frame->height);
        if (_usesLinearColorspace && color.IsHdr)
            throw new NotSupportedException("HDR tone mapping is not implemented for linear output.");

        if (_graphicsDeviceType == GraphicsDeviceType.Direct3D11
            && _graphicsDevice != nint.Zero
            && _softwareGpuUnavailableReason is null)
        {
            try
            {
                _softwareGpuConverter ??= new SoftwareD3D11FrameConverter(
                    _graphicsDevice,
                    _usesLinearColorspace);
                if (_softwareGpuConverter.TryConvert(
                        frame,
                        color,
                        _cancellationToken,
                        out var gpuLease,
                        out var gpuStatus))
                {
                    if (!ReferenceEquals(_softwareGpuFrameStatus, gpuStatus))
                    {
                        _softwareGpuFrameStatus = gpuStatus;
                        _decodeStatus = fallbackStatus + gpuStatus;
                    }
                    return new GpuDecodedVideoFrame(
                        gpuLease!,
                        ReadTimecode(frame),
                        MediaInfo.FrameRate,
                        _decodeStatus,
                        DecodePath.SoftwareGpuTexture);
                }

                _softwareGpuUnavailableReason = gpuStatus;
                _softwareGpuConverter.Dispose();
                _softwareGpuConverter = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _softwareGpuUnavailableReason = exception.Message;
                _softwareGpuConverter?.Dispose();
                _softwareGpuConverter = null;
            }
        }

        var outputPixelFormat = _usesLinearColorspace
            ? AVPixelFormat.AV_PIX_FMT_RGBA64LE
            : AVPixelFormat.AV_PIX_FMT_BGRA;
        _swsContext = ffmpeg.sws_getCachedContext(
            _swsContext,
            frame->width,
            frame->height,
            pixelFormat,
            frame->width,
            frame->height,
            outputPixelFormat,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);
        if (_swsContext is null)
            throw new InvalidOperationException($"FFmpeg could not convert pixel format {pixelFormat} to BGRA.");

        var pixelDescriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        var sourceIsRgb = pixelDescriptor is not null
            && (pixelDescriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        if (!sourceIsRgb)
        {
            var coefficients = *(int_array4*)ffmpeg.sws_getCoefficients(color.SwsColorSpace);
            var colorspaceResult = ffmpeg.sws_setColorspaceDetails(
                _swsContext,
                in coefficients,
                color.FullRange ? 1 : 0,
                in coefficients,
                dstRange: 1,
                brightness: 0,
                contrast: 1 << 16,
                saturation: 1 << 16);
            if (colorspaceResult < 0)
                Throw(colorspaceResult, $"configure {color.Description} software color conversion");
        }

        var stride = checked(frame->width * (_usesLinearColorspace ? 8 : 4));
        var pixels = new byte[checked(stride * frame->height)];
        fixed (byte* destination = pixels)
        {
            var destinationData = new byte*[8];
            destinationData[0] = destination;
            var destinationLines = new int[8];
            destinationLines[0] = stride;

            var scaledHeight = ffmpeg.sws_scale(
                _swsContext,
                frame->data.ToArray(),
                frame->linesize.ToArray(),
                0,
                frame->height,
                destinationData,
                destinationLines);
            if (scaledHeight != frame->height)
            {
                if (scaledHeight < 0)
                    Throw(scaledHeight, "convert a decoded frame to BGRA");
                throw new InvalidDataException(
                    $"FFmpeg converted {scaledHeight} rows; expected {frame->height}.");
            }
        }

        var outputDescription = _usesLinearColorspace ? "linear RGBA16F" : "nonlinear BGRA8";
        var softwareFallback = _softwareGpuUnavailableReason is null
            ? string.Empty
            : $"Software CPU fallback; {_softwareGpuUnavailableReason}; ";
        var inputColorDescription = sourceIsRgb ? color.RgbDescription : color.Description;
        _decodeStatus = $"{fallbackStatus}{softwareFallback}Software {inputColorDescription} -> {outputDescription}; transfer {color.TransferName}";
        if (color.IsHdr)
            _decodeStatus += "; HDR tone mapping is not implemented";
        if (_usesLinearColorspace)
            color.LinearizeRgba64(pixels);

        return new CpuDecodedVideoFrame(
            pixels: pixels,
            width: frame->width,
            height: frame->height,
            timecode: ReadTimecode(frame),
            frameRate: MediaInfo.FrameRate,
            decodeStatus: _decodeStatus,
            linear: _usesLinearColorspace);
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
            _texturePool = new D3D11TexturePool(d3d11Context, _usesLinearColorspace);
            _hardwareConfigured = true;
            _decodeStatus = "D3D11VA configured on the consumer device";
        }
        catch (Exception exception) when (
            _decodeMode == DecodeMode.Auto
            && exception is not OperationCanceledException)
        {
            _texturePool?.Dispose();
            _texturePool = null;
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
            var startTimestamp = _videoStream->start_time == ffmpeg.AV_NOPTS_VALUE
                ? 0L
                : _videoStream->start_time;
            seconds = (timestamp - startTimestamp) * ToDouble(_videoStream->time_base);
        }

        return TimeSpan.FromSeconds(Math.Max(0d, seconds));
    }

    private void Seek(TimeSpan position)
    {
        var timeBase = ToDouble(_videoStream->time_base);
        if (timeBase <= 0d)
            throw new NotSupportedException("The video stream has no usable time base for seeking.");

        var streamStart = _videoStream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0L
            : _videoStream->start_time;
        var target = checked(streamStart + (long)Math.Round(position.TotalSeconds / timeBase));
        Check(
            ffmpeg.av_seek_frame(
                _formatContext,
                _videoStreamIndex,
                target,
                ffmpeg.AVSEEK_FLAG_BACKWARD),
            $"seek to {position.TotalSeconds:0.###} seconds");
        ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    private bool DrainDecoder(Func<DecodedVideoFrame, bool> acceptFrame)
    {
        int result;
        while ((result = ffmpeg.avcodec_send_packet(_codecContext, null)) == Again)
        {
            if (!ReceiveFrames(acceptFrame))
                return false;
        }

        if (result < 0 && result != Again && result != ffmpeg.AVERROR_EOF)
            Throw(result, "drain the video decoder");

        return ReceiveFrames(acceptFrame);
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

    private static (int N, int D) NormalizeFrameRate(AVRational rational)
        => rational.num > 0 && rational.den > 0
            ? (rational.num, rational.den)
            : default;

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
}
