using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>Conversion resources have an independent lifetime from packet decode and demux.</summary>
internal unsafe sealed class VideoFrameConverter : IDisposable
{
    private readonly nint _graphicsDevice;
    private readonly GraphicsDeviceType _graphicsDeviceType;
    private readonly bool _usesLinearColorspace;
    private readonly DecodeMode _decodeMode;
    private AVD3D11VADeviceContext* _hardwareContext;
    private string _decodeStatus = "";
    private (int Format, VideoColorInfo Color, bool Fallback, string? Reason)? _cpuStatusKey;
    public bool DeclaresAlpha { get; set; }
    public string? Status => _decodeStatus.Length == 0 ? null : _decodeStatus;
    private readonly byte*[] _sourceData = new byte*[8];
    private readonly int[] _sourceLines = new int[8];
    private readonly byte*[] _destinationData = new byte*[8];
    private readonly int[] _destinationLines = new int[8];
    private SwsContext* _swsContext;
    private readonly CpuFramePool _cpuPool = new(ResourceBudget.Cpu);
    private D3D11TexturePool? _texturePool;
    private SoftwareD3D11FrameConverter? _softwareGpuConverter;
    private string? _softwareGpuUnavailableReason;
    private string? _softwareGpuFrameStatus;
    private (int Format, int Width, int Height, VideoColorInfo Color)? _softwareKey;
    public VideoFrameConverter(nint device, GraphicsDeviceType type, bool linear, DecodeMode mode)
    { _graphicsDevice = device; _graphicsDeviceType = type; _usesLinearColorspace = linear; _decodeMode = mode; }

    public void ConfigureHardware(AVD3D11VADeviceContext* context)
    {
        ClearHardware();
        _hardwareContext = context;
        _texturePool = new D3D11TexturePool(context, _usesLinearColorspace);
    }
    public void ClearHardware()
    { _texturePool?.Dispose(); _texturePool = null; _hardwareContext = null; }

    public void Dispose()
    {
        ClearHardware();
        _softwareGpuConverter?.Dispose(); _softwareGpuConverter = null;
        if (_swsContext is not null) { ffmpeg.sws_freeContext(_swsContext); _swsContext = null; }
        _cpuPool.Dispose();
    }

    public DecodedVideoFrame Convert(AVFrame* frame, TimeSpan timecode, (int N, int D) frameRate,
        bool hardwareConfigured, bool hardwareSelected, bool hardwareOffered, CancellationToken token)
    {
        if (frame->width <= 0 || frame->height <= 0)
            throw new InvalidDataException("FFmpeg returned a video frame with invalid dimensions.");

        var pixelFormat = (AVPixelFormat)frame->format;
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            if (_texturePool is null)
                throw new FFmpegHardwareException("D3D11VA selected without a GPU texture pool.");

            if (!_texturePool.Matches(frame))
            {
                _texturePool.Dispose();
                _texturePool = new D3D11TexturePool(_hardwareContext, _usesLinearColorspace);
            }
            var lease = _texturePool.Convert(
                frame,
                frameRate,
                token,
                out var hardwareStatus);
            _decodeStatus = WithAlphaStatus(frame, hardwareStatus);
            return new GpuDecodedVideoFrame(
                lease,
                timecode,
                frameRate,
                _decodeStatus);
        }

        if (_decodeMode == DecodeMode.Hardware)
        {
            var reason = hardwareOffered
                ? $"FFmpeg selected software pixel format {pixelFormat} instead of D3D11VA."
                : "The selected codec did not offer AV_PIX_FMT_D3D11.";
            throw new FFmpegHardwareException(reason);
        }

        var fallbackStatus = hardwareConfigured && !hardwareSelected
            ? $"Hardware fallback; D3D11VA was not selected ({pixelFormat}); "
            : string.Empty;
        var color = VideoColorInfo.Resolve(
            frame->colorspace,
            frame->color_range,
            frame->color_trc,
            frame->height);
        if (_usesLinearColorspace && color.IsHdr)
            throw new NotSupportedException("HDR tone mapping is not implemented for linear output.");

        var key = ((int)pixelFormat, frame->width, frame->height, color);
        if (_softwareKey != key)
        {
            _softwareGpuConverter?.Dispose();
            _softwareGpuConverter = null;
            _softwareGpuUnavailableReason = null;
            _softwareGpuFrameStatus = null;
            _cpuStatusKey = null;
            _softwareKey = key;
        }
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
                        token,
                        out var gpuLease,
                        out var gpuStatus))
                {
                    if (!ReferenceEquals(_softwareGpuFrameStatus, gpuStatus))
                    {
                        _softwareGpuFrameStatus = gpuStatus;
                        _decodeStatus = WithAlphaStatus(frame, fallbackStatus + gpuStatus);
                    }
                    return new GpuDecodedVideoFrame(
                        gpuLease!,
                        timecode,
                        frameRate,
                        _decodeStatus,
                        DecodePath.SoftwareGpuTexture);
                }

                _softwareGpuUnavailableReason = gpuStatus;
                _softwareGpuConverter.Dispose();
                _softwareGpuConverter = null;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or ResourceUnavailableException))
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
        var cpuLease = _cpuPool.TryRent(checked(stride * frame->height)) ?? throw new ResourceUnavailableException();
        var pixels = cpuLease.Buffer;
        try
        {
            fixed (byte* destination = pixels)
            {
                for (var index = 0; index < 8; index++)
                {
                    _sourceData[index] = frame->data[(uint)index];
                    _sourceLines[index] = frame->linesize[(uint)index];
                }

                _destinationData[0] = destination;
                _destinationLines[0] = stride;

                var scaledHeight = ffmpeg.sws_scale(
                    _swsContext,
                    _sourceData,
                    _sourceLines,
                    0,
                    frame->height,
                    _destinationData,
                    _destinationLines);
                if (scaledHeight != frame->height)
                {
                    if (scaledHeight < 0)
                        Throw(scaledHeight, "convert a decoded frame to BGRA");
                    throw new InvalidDataException(
                        $"FFmpeg converted {scaledHeight} rows; expected {frame->height}.");
                }
            }

            var statusKey = ((int)pixelFormat, color, hardwareConfigured && !hardwareSelected, _softwareGpuUnavailableReason);
            if (_cpuStatusKey != statusKey)
            {
                var outputDescription = _usesLinearColorspace ? "linear RGBA16F" : "nonlinear BGRA8";
                var softwareFallback = _softwareGpuUnavailableReason is null
                    ? string.Empty
                    : $"Software CPU fallback; {_softwareGpuUnavailableReason}; ";
                var inputColorDescription = sourceIsRgb ? color.RgbDescription : color.Description;
                _decodeStatus = $"{fallbackStatus}{softwareFallback}Software {inputColorDescription} -> {outputDescription}; transfer {color.TransferName}";
                if (color.IsHdr)
                    _decodeStatus += "; HDR tone mapping is not implemented";
                _decodeStatus = WithAlphaStatus(frame, _decodeStatus);
                _cpuStatusKey = statusKey;
            }
            if (_usesLinearColorspace)
                color.LinearizeRgba64(pixels);

            return new CpuDecodedVideoFrame(
                pixels: pixels,
                width: frame->width,
                height: frame->height,
                timecode: timecode,
                frameRate: frameRate,
                decodeStatus: _decodeStatus,
                linear: _usesLinearColorspace,
                lease: cpuLease);
        }
        catch { cpuLease.Dispose(); throw; }
    }

    private string WithAlphaStatus(AVFrame* frame, string status)
    {
        if (!DeclaresAlpha)
            return status;

        var descriptor = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        if (descriptor is not null
            && (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0)
        {
            return status;
        }

        return $"{status}; alpha declared but unavailable; output is opaque";
    }

    private static void Throw(int code, string operation) => FFmpegDemuxContext.Check(code, operation);
}
