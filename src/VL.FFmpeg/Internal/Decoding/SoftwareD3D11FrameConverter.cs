using System.Reflection;
using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.Lib.Basics.Imaging;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class SoftwareD3D11TextureLease : ID3D11TextureLease
{
    private SoftwareD3D11FrameConverter? _owner;
    private SoftwareD3D11FrameConverter.Slot? _slot;

    internal SoftwareD3D11TextureLease(
        SoftwareD3D11FrameConverter owner,
        SoftwareD3D11FrameConverter.Slot slot)
    {
        _owner = owner;
        _slot = slot;
        Texture = new VideoTexture(
            (nint)slot.OutputTexture,
            owner.Width,
            owner.Height,
            owner.PixelFormat);
    }

    public VideoTexture Texture { get; }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        var slot = Interlocked.Exchange(ref _slot, null);
        if (owner is not null && slot is not null)
            owner.Return(slot);
    }
}

internal unsafe sealed class SoftwareD3D11FrameConverter : IDisposable
{
    internal const int Capacity = 3;

    private readonly object _syncRoot = new();
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _immediateContext;
    private readonly ID3D11DeviceContext* _deferredContext;
    private readonly bool _linearOutput;
    private readonly int _outputFormat;
    private readonly void* _vertexShader;
    private readonly void* _pixelShader;
    private readonly void* _sampler;
    private readonly List<Slot> _slots = [];

    private SoftwareFrameLayout? _layout;
    private int _nextSlotIndex;
    private VideoColorInfo? _statusColor;
    private string? _status;
    private bool _disposed;

    internal SoftwareD3D11FrameConverter(nint devicePointer, bool linearOutput)
    {
        if (devicePointer == 0)
            throw new ArgumentException("A D3D11 device is required.", nameof(devicePointer));

        _device = (ID3D11Device*)devicePointer;
        D3D11Interop.AddRef(_device);
        try
        {
            _immediateContext = D3D11ShaderInterop.GetImmediateContext(_device);
            D3D11Interop.EnableMultithreadProtection(_immediateContext);
            _deferredContext = D3D11ShaderInterop.CreateDeferredContext(_device);
            _linearOutput = linearOutput;
            _outputFormat = linearOutput
                ? D3D11Interop.DxgiFormatR16G16B16A16Float
                : D3D11Interop.DxgiFormatB8G8R8A8Unorm;
            _vertexShader = D3D11ShaderInterop.CreateVertexShader(
                _device,
                LoadShader("VideoConvert.vs.cso"));
            _pixelShader = D3D11ShaderInterop.CreatePixelShader(
                _device,
                LoadShader("VideoConvert.ps.cso"));
            _sampler = D3D11ShaderInterop.CreateSampler(_device);
        }
        catch
        {
            D3D11Interop.Release(_sampler);
            D3D11Interop.Release(_pixelShader);
            D3D11Interop.Release(_vertexShader);
            D3D11Interop.Release(_deferredContext);
            D3D11Interop.Release(_immediateContext);
            D3D11Interop.Release(_device);
            throw;
        }
    }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal PixelFormat PixelFormat => _linearOutput
        ? PixelFormat.R16G16B16A16F
        : PixelFormat.B8G8R8A8;

    internal bool TryConvert(
        AVFrame* frame,
        VideoColorInfo color,
        CancellationToken cancellationToken,
        out SoftwareD3D11TextureLease? lease,
        out string status)
    {
        lease = null;
        status = string.Empty;
        ObjectDisposedException.ThrowIf(_disposed, this);

        SoftwareFrameLayout layout;
        if (_layout is { } configuredLayout)
        {
            if (!configuredLayout.Matches(frame))
            {
                status = "software pixel layout or dimensions changed during playback";
                return false;
            }
            layout = configuredLayout;
        }
        else if (!SoftwareFrameLayout.TryCreate(frame, out layout, out var reason))
        {
            status = reason;
            return false;
        }

        if (_linearOutput && color.Transfer == VideoTransferFunction.Unsupported)
        {
            status = $"unsupported video transfer characteristic: {color.TransferName}";
            return false;
        }

        foreach (var plane in layout.Planes)
        {
            if (frame->linesize[(uint)plane.SourcePlane] <= 0)
            {
                status = $"unsupported negative or empty linesize in plane {plane.SourcePlane}";
                return false;
            }
        }

        Slot slot;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_layout is null)
                Configure(layout);

            while (!TryRent(out slot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                Monitor.Wait(_syncRoot, millisecondsTimeout: 5);
            }
            slot.InUse = true;
        }

        void* commandList = null;
        try
        {
            UploadPlanes(frame, layout, slot);
            UploadConstants(frame, layout, color, slot.ConstantBuffer);

            var views = stackalloc void*[4];
            for (var index = 0; index < 4; index++)
                views[index] = slot.Planes[index] is { } plane ? plane.View : null;
            D3D11ShaderInterop.Draw(
                _deferredContext,
                _vertexShader,
                _pixelShader,
                _sampler,
                slot.ConstantBuffer,
                views,
                slot.OutputView,
                frame->width,
                frame->height);
            commandList = D3D11ShaderInterop.FinishCommandList(_deferredContext);
            D3D11ShaderInterop.ExecuteCommandList(_immediateContext, commandList);

            if (_status is null || _statusColor != color)
            {
                var output = _linearOutput ? "linear GPU RGBA16F" : "nonlinear GPU BGRA8";
                var inputColor = layout.InputMode >= 2 ? color.RgbDescription : color.Description;
                _status = $"Software {layout.Name}, {inputColor} -> D3D11 {output}; transfer {color.TransferName}";
                _statusColor = color;
            }
            status = _status;
            lease = new SoftwareD3D11TextureLease(this, slot);
            return true;
        }
        catch
        {
            Return(slot);
            throw;
        }
        finally
        {
            D3D11Interop.Release(commandList);
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
                D3D11Interop.Release(slot.OutputTexture);
                slot.OutputTexture = null;
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
                foreach (var plane in slot.Planes)
                    plane?.Dispose();
                D3D11Interop.Release(slot.ConstantBuffer);
                slot.ConstantBuffer = null;
                D3D11Interop.Release(slot.OutputView);
                slot.OutputView = null;
                if (!slot.InUse)
                {
                    D3D11Interop.Release(slot.OutputTexture);
                    slot.OutputTexture = null;
                }
            }

            D3D11Interop.Release(_sampler);
            D3D11Interop.Release(_pixelShader);
            D3D11Interop.Release(_vertexShader);
            D3D11Interop.Release(_deferredContext);
            D3D11Interop.Release(_immediateContext);
            D3D11Interop.Release(_device);
            Monitor.PulseAll(_syncRoot);
        }
    }

    private void Configure(SoftwareFrameLayout layout)
    {
        for (var slotIndex = 0; slotIndex < Capacity; slotIndex++)
        {
            var slot = new Slot();
            try
            {
                for (var planeIndex = 0; planeIndex < layout.Planes.Length; planeIndex++)
                {
                    var plane = layout.Planes[planeIndex];
                    slot.Planes[planeIndex] = new UploadPlane(
                        _device,
                        plane.Width,
                        plane.Height,
                        plane.DxgiFormat);
                }
                slot.ConstantBuffer = D3D11ShaderInterop.CreateConstantBuffer(
                    _device,
                    checked((uint)Marshal.SizeOf<ShaderConstants>()));
                slot.OutputTexture = D3D11Interop.CreateTexture(
                    _device,
                    layout.Width,
                    layout.Height,
                    _outputFormat);
                slot.OutputView = D3D11ShaderInterop.CreateRenderTargetView(_device, slot.OutputTexture);
                _slots.Add(slot);
            }
            catch
            {
                slot.Dispose();
                throw;
            }
        }
        Width = layout.Width;
        Height = layout.Height;
        _layout = layout;
    }

    private void UploadPlanes(AVFrame* frame, SoftwareFrameLayout layout, Slot slot)
    {
        for (var index = 0; index < layout.Planes.Length; index++)
        {
            var plane = layout.Planes[index];
            var upload = slot.Planes[index]!;
            var source = frame->data[(uint)plane.SourcePlane];
            var sourceStride = frame->linesize[(uint)plane.SourcePlane];
            if (source is null || sourceStride == 0)
                throw new InvalidDataException($"FFmpeg returned an empty plane {plane.SourcePlane}.");

            var rowBytes = checked(plane.Width * plane.BytesPerPixel);
            if (sourceStride < rowBytes)
                throw new InvalidDataException("The FFmpeg plane stride is smaller than its visible row.");
            D3D11ShaderInterop.UpdateTexture(
                _deferredContext,
                upload.Texture,
                source,
                checked((uint)sourceStride));
        }
    }

    private void UploadConstants(
        AVFrame* frame,
        SoftwareFrameLayout layout,
        VideoColorInfo color,
        void* constantBuffer)
    {
        var constants = BuildConstants(frame, layout, color);
        var mapped = D3D11ShaderInterop.MapWriteDiscard(_deferredContext, constantBuffer);
        try
        {
            *(ShaderConstants*)mapped.Data = constants;
        }
        finally
        {
            D3D11ShaderInterop.Unmap(_deferredContext, constantBuffer);
        }
    }

    private ShaderConstants BuildConstants(
        AVFrame* frame,
        SoftwareFrameLayout layout,
        VideoColorInfo color)
    {
        var transfer = color.Transfer switch
        {
            VideoTransferFunction.Linear => 1u,
            VideoTransferFunction.Srgb => 2u,
            VideoTransferFunction.Gamma22 => 3u,
            VideoTransferFunction.Gamma28 => 4u,
            _ => 0u
        };

        var (kr, kb) = color.SwsColorSpace switch
        {
            ffmpeg.SWS_CS_ITU709 => (0.2126f, 0.0722f),
            ffmpeg.SWS_CS_FCC => (0.30f, 0.11f),
            ffmpeg.SWS_CS_SMPTE240M => (0.212f, 0.087f),
            ffmpeg.SWS_CS_BT2020 => (0.2627f, 0.0593f),
            _ => (0.299f, 0.114f)
        };
        var kg = 1f - kr - kb;
        var matrixR = new Float4(1f, 0f, 2f * (1f - kr), 0f);
        var matrixG = new Float4(
            1f,
            -2f * kb * (1f - kb) / kg,
            -2f * kr * (1f - kr) / kg,
            0f);
        var matrixB = new Float4(1f, 2f * (1f - kb), 0f, 0f);

        var depth = layout.ComponentDepth;
        var maximum = (1u << depth) - 1u;
        var codeScale = 1u << Math.Max(0, depth - 8);
        var yOffset = color.FullRange ? 0f : 16f * codeScale / maximum;
        var yScale = color.FullRange ? 1f : maximum / (219f * codeScale);
        var cOffset = (1u << (depth - 1)) / (float)maximum;
        var cScale = color.FullRange ? 1f : maximum / (224f * codeScale);

        var chromaOffsetX = 0f;
        var chromaOffsetY = 0f;
        if (layout.ChromaWidthShift > 0 && frame->chroma_location is
            AVChromaLocation.AVCHROMA_LOC_LEFT or
            AVChromaLocation.AVCHROMA_LOC_TOPLEFT or
            AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT)
        {
            chromaOffsetX = -0.5f / frame->width;
        }
        if (layout.ChromaHeightShift > 0 && frame->chroma_location is
            AVChromaLocation.AVCHROMA_LOC_TOPLEFT or
            AVChromaLocation.AVCHROMA_LOC_TOP)
        {
            chromaOffsetY = -0.5f / frame->height;
        }
        else if (layout.ChromaHeightShift > 0 && frame->chroma_location is
                 AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT or
                 AVChromaLocation.AVCHROMA_LOC_BOTTOM)
        {
            chromaOffsetY = 0.5f / frame->height;
        }

        return new ShaderConstants
        {
            InputMode = layout.InputMode,
            TransferMode = transfer,
            LinearOutput = _linearOutput ? 1u : 0u,
            HasAlpha = layout.HasAlpha ? 1u : 0u,
            PlaneScale = new Float4(
                layout.PlaneScale(0),
                layout.PlaneScale(1),
                layout.PlaneScale(2),
                layout.PlaneScale(3)),
            YuvRange = new Float4(yOffset, yScale, cOffset, cScale),
            MatrixR = matrixR,
            MatrixG = matrixG,
            MatrixB = matrixB,
            ChromaOffset = new Float4(chromaOffsetX, chromaOffsetY, 0f, 0f)
        };
    }

    private bool TryRent(out Slot slot)
    {
        for (var offset = 0; offset < _slots.Count; offset++)
        {
            var index = (_nextSlotIndex + offset) % _slots.Count;
            var candidate = _slots[index];
            if (candidate.InUse)
                continue;
            _nextSlotIndex = (index + 1) % _slots.Count;
            slot = candidate;
            return true;
        }
        slot = null!;
        return false;
    }

    private static byte[] LoadShader(string filename)
    {
        var assembly = typeof(SoftwareD3D11FrameConverter).Assembly;
        var suffix = $".Shaders.{filename}";
        var name = assembly.GetManifestResourceNames().SingleOrDefault(
            candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException($"Embedded shader '{filename}' was not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded shader '{filename}' could not be opened.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public uint InputMode;
        public uint TransferMode;
        public uint LinearOutput;
        public uint HasAlpha;
        public Float4 PlaneScale;
        public Float4 YuvRange;
        public Float4 MatrixR;
        public Float4 MatrixG;
        public Float4 MatrixB;
        public Float4 ChromaOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Float4(float X, float Y, float Z, float W);

    internal sealed class Slot : IDisposable
    {
        public UploadPlane?[] Planes { get; } = new UploadPlane?[4];
        public void* ConstantBuffer;
        public ID3D11Texture2D* OutputTexture;
        public void* OutputView;
        public bool InUse;

        public void Dispose()
        {
            foreach (var plane in Planes)
                plane?.Dispose();
            D3D11Interop.Release(ConstantBuffer);
            ConstantBuffer = null;
            D3D11Interop.Release(OutputView);
            OutputView = null;
            D3D11Interop.Release(OutputTexture);
            OutputTexture = null;
        }
    }

    internal sealed class UploadPlane : IDisposable
    {
        public UploadPlane(ID3D11Device* device, int width, int height, int format)
        {
            Texture = D3D11ShaderInterop.CreateUploadTexture(device, width, height, format);
            try
            {
                View = D3D11ShaderInterop.CreateShaderResourceView(device, Texture);
            }
            catch
            {
                D3D11Interop.Release(Texture);
                Texture = null;
                throw;
            }
        }

        public ID3D11Texture2D* Texture;
        public void* View;

        public void Dispose()
        {
            D3D11Interop.Release(View);
            View = null;
            D3D11Interop.Release(Texture);
            Texture = null;
        }
    }
}
