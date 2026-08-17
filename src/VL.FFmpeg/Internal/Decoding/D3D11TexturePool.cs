using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.Lib.Basics.Imaging;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class D3D11TextureLease : IDisposable
{
    private D3D11TexturePool? _pool;
    private D3D11TexturePool.Slot? _slot;

    internal D3D11TextureLease(D3D11TexturePool pool, D3D11TexturePool.Slot slot)
    {
        _pool = pool;
        _slot = slot;
        Texture = new VideoTexture(
            (nint)slot.Texture,
            pool.Width,
            pool.Height,
            PixelFormat.B8G8R8A8);
    }

    public VideoTexture Texture { get; }

    public void Dispose()
    {
        var pool = Interlocked.Exchange(ref _pool, null);
        var slot = Interlocked.Exchange(ref _slot, null);
        if (pool is not null && slot is not null)
            pool.Return(slot);
    }
}

internal unsafe sealed class D3D11TexturePool : IDisposable
{
    internal const int Capacity = 6;

    private readonly object _syncRoot = new();
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _deviceContext;
    private readonly ID3D11VideoDevice* _videoDevice;
    private readonly ID3D11VideoContext* _videoContext;
    private readonly nint _lockFunction;
    private readonly nint _unlockFunction;
    private readonly void* _lockContext;
    private readonly List<Slot> _slots = [];

    private void* _enumerator;
    private void* _processor;
    private int _inputFormat;
    private bool _disposed;

    internal D3D11TexturePool(AVD3D11VADeviceContext* context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        if (context->device is null
            || context->device_context is null
            || context->video_device is null
            || context->video_context is null)
        {
            throw new FFmpegHardwareException("FFmpeg initialized an incomplete D3D11VA device context.");
        }

        _device = context->device;
        _deviceContext = context->device_context;
        _videoDevice = context->video_device;
        _videoContext = context->video_context;
        _lockFunction = context->@lock.Pointer;
        _unlockFunction = context->unlock.Pointer;
        _lockContext = context->lock_ctx;

        D3D11Interop.AddRef(_device);
        D3D11Interop.AddRef(_deviceContext);
        D3D11Interop.AddRef(_videoDevice);
        D3D11Interop.AddRef(_videoContext);
        try
        {
            D3D11Interop.EnableMultithreadProtection(_deviceContext);
        }
        catch
        {
            D3D11Interop.Release(_videoContext);
            D3D11Interop.Release(_videoDevice);
            D3D11Interop.Release(_deviceContext);
            D3D11Interop.Release(_device);
            throw;
        }
    }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal D3D11TextureLease Convert(
        AVFrame* frame,
        (int N, int D) frameRate,
        CancellationToken cancellationToken,
        out string status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame is null || frame->data[0] is null)
            throw new FFmpegHardwareException("FFmpeg returned a D3D11VA frame without a texture.");

        var sourceTexture = (ID3D11Texture2D*)frame->data[0];
        var sourceDescription = D3D11Interop.GetDescription(sourceTexture);
        if (sourceDescription.Format is not (
                D3D11Interop.DxgiFormatNv12
                or D3D11Interop.DxgiFormatP010))
        {
            throw new FFmpegHardwareException(
                $"D3D11VA returned unsupported DXGI format {sourceDescription.Format}; expected NV12 or P010.");
        }

        Slot slot;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureConfiguration(frame->width, frame->height, sourceDescription.Format, frameRate);
            while (!TryRent(out slot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                Monitor.Wait(_syncRoot, millisecondsTimeout: 5);
            }
            slot.InUse = true;
        }

        void* inputView = null;
        try
        {
            EnterDeviceLock();
            try
            {
                inputView = D3D11Interop.CreateInputView(
                    _videoDevice,
                    sourceTexture,
                    _enumerator,
                    checked((uint)(nint)frame->data[1]));
                var fullRange = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG;
                var bt709 = frame->colorspace == AVColorSpace.AVCOL_SPC_BT709
                    || (frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED && frame->height >= 720);
                D3D11Interop.ConfigureAndBlit(
                    _videoContext,
                    _processor,
                    inputView,
                    slot.OutputView,
                    frame->width,
                    frame->height,
                    fullRange,
                    bt709);
                D3D11Interop.Flush(_deviceContext);
            }
            finally
            {
                ExitDeviceLock();
            }

            var formatName = sourceDescription.Format == D3D11Interop.DxgiFormatP010
                ? "P010"
                : "NV12";
            var hdr = frame->colorspace is AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL
                || frame->color_trc is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
                    or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
            status = hdr
                ? $"D3D11VA {formatName} -> GPU BGRA8; HDR tone mapping is not implemented"
                : $"D3D11VA {formatName} -> GPU BGRA8";
            return new D3D11TextureLease(this, slot);
        }
        catch
        {
            Return(slot);
            throw;
        }
        finally
        {
            D3D11Interop.Release(inputView);
        }
    }

    internal void Return(Slot slot)
    {
        lock (_syncRoot)
        {
            if (!slot.InUse)
                return;

            slot.InUse = false;
            if (_disposed)
            {
                D3D11Interop.Release(slot.Texture);
                slot.Texture = null;
            }
            Monitor.PulseAll(_syncRoot);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var slot in _slots)
            {
                D3D11Interop.Release(slot.OutputView);
                slot.OutputView = null;
                if (!slot.InUse)
                {
                    D3D11Interop.Release(slot.Texture);
                    slot.Texture = null;
                }
            }

            D3D11Interop.Release(_processor);
            _processor = null;
            D3D11Interop.Release(_enumerator);
            _enumerator = null;
            D3D11Interop.Release(_videoContext);
            D3D11Interop.Release(_videoDevice);
            D3D11Interop.Release(_deviceContext);
            D3D11Interop.Release(_device);
            Monitor.PulseAll(_syncRoot);
        }
    }

    private void EnsureConfiguration(
        int width,
        int height,
        int inputFormat,
        (int N, int D) frameRate)
    {
        if (_enumerator is not null)
        {
            if (Width != width || Height != height || _inputFormat != inputFormat)
                throw new FFmpegHardwareException("D3D11VA changed texture format or dimensions during playback.");
            return;
        }

        EnterDeviceLock();
        try
        {
            _enumerator = D3D11Interop.CreateVideoProcessorEnumerator(
                _videoDevice,
                width,
                height,
                frameRate);
            D3D11Interop.CheckVideoProcessorFormat(_enumerator, inputFormat, requiredFlags: 1);
            D3D11Interop.CheckVideoProcessorFormat(
                _enumerator,
                D3D11Interop.DxgiFormatB8G8R8A8Unorm,
                requiredFlags: 2);
            _processor = D3D11Interop.CreateVideoProcessor(_videoDevice, _enumerator);

            for (var index = 0; index < Capacity; index++)
            {
                var texture = D3D11Interop.CreateTexture(_device, width, height);
                void* outputView = null;
                try
                {
                    outputView = D3D11Interop.CreateOutputView(_videoDevice, texture, _enumerator);
                    _slots.Add(new Slot { Texture = texture, OutputView = outputView });
                }
                catch
                {
                    D3D11Interop.Release(outputView);
                    D3D11Interop.Release(texture);
                    throw;
                }
            }

            Width = width;
            Height = height;
            _inputFormat = inputFormat;
        }
        finally
        {
            ExitDeviceLock();
        }
    }

    private bool TryRent(out Slot slot)
    {
        foreach (var candidate in _slots)
        {
            if (candidate.InUse)
                continue;
            slot = candidate;
            return true;
        }

        slot = null!;
        return false;
    }

    private void EnterDeviceLock()
    {
        if (_lockFunction != 0)
            ((delegate* unmanaged[Cdecl]<void*, void>)_lockFunction)(_lockContext);
    }

    private void ExitDeviceLock()
    {
        if (_unlockFunction != 0)
            ((delegate* unmanaged[Cdecl]<void*, void>)_unlockFunction)(_lockContext);
    }

    internal sealed class Slot
    {
        public ID3D11Texture2D* Texture;
        public void* OutputView;
        public bool InUse;
    }
}
