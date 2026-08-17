using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Interop;

internal static unsafe class D3D11Interop
{
    internal const int DxgiFormatB8G8R8A8Unorm = 87;
    internal const int DxgiFormatNv12 = 103;
    internal const int DxgiFormatP010 = 104;

    internal const uint BindShaderResource = 0x8;
    internal const uint BindRenderTarget = 0x20;

    private static readonly Guid Id3D11Multithread =
        new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    internal static uint AddRef(void* instance)
    {
        if (instance is null)
            return 0;
        var vtable = *(void***)instance;
        return ((delegate* unmanaged[Stdcall]<void*, uint>)vtable[1])(instance);
    }

    internal static uint Release(void* instance)
    {
        if (instance is null)
            return 0;
        var vtable = *(void***)instance;
        return ((delegate* unmanaged[Stdcall]<void*, uint>)vtable[2])(instance);
    }

    internal static void EnableMultithreadProtection(ID3D11DeviceContext* context)
    {
        if (context is null)
            throw new FFmpegHardwareException("The D3D11 device has no immediate context.");

        void* multithread = null;
        var iid = Id3D11Multithread;
        var query = (delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, Guid*, void**, int>)
            context->lpVtbl->QueryInterface;
        var result = query(context, &iid, &multithread);
        Check(result, "query ID3D11Multithread");

        try
        {
            var vtable = *(void***)multithread;
            ((delegate* unmanaged[Stdcall]<void*, int, int>)vtable[5])(multithread, 1);
        }
        finally
        {
            Release(multithread);
        }
    }

    internal static D3D11Texture2DDesc GetDescription(ID3D11Texture2D* texture)
    {
        D3D11Texture2DDesc description;
        ((delegate* unmanaged[Stdcall]<ID3D11Texture2D*, D3D11Texture2DDesc*, void>)
            texture->lpVtbl->GetDesc)(texture, &description);
        return description;
    }

    internal static ID3D11Texture2D* CreateTexture(
        ID3D11Device* device,
        int width,
        int height)
    {
        var description = new D3D11Texture2DDesc
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatB8G8R8A8Unorm,
            SampleDescription = new DxgiSampleDesc { Count = 1 },
            Usage = 0,
            BindFlags = BindShaderResource | BindRenderTarget
        };
        ID3D11Texture2D* texture = null;
        var create = (delegate* unmanaged[Stdcall]<
            ID3D11Device*,
            D3D11Texture2DDesc*,
            void*,
            ID3D11Texture2D**,
            int>)device->lpVtbl->CreateTexture2D;
        Check(create(device, &description, null, &texture), "create D3D11 output texture");
        return texture;
    }

    internal static void Flush(ID3D11DeviceContext* context)
        => ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void>)
            context->lpVtbl->Flush)(context);

    internal static void* CreateVideoProcessorEnumerator(
        ID3D11VideoDevice* device,
        int width,
        int height,
        (int N, int D) frameRate)
    {
        var rate = new DxgiRational
        {
            Numerator = frameRate.N > 0 ? checked((uint)frameRate.N) : 30,
            Denominator = frameRate.D > 0 ? checked((uint)frameRate.D) : 1
        };
        var description = new D3D11VideoProcessorContentDesc
        {
            InputFrameFormat = 0,
            InputFrameRate = rate,
            InputWidth = checked((uint)width),
            InputHeight = checked((uint)height),
            OutputFrameRate = rate,
            OutputWidth = checked((uint)width),
            OutputHeight = checked((uint)height),
            Usage = 0
        };
        void* enumerator = null;
        var create = (delegate* unmanaged[Stdcall]<
            ID3D11VideoDevice*,
            D3D11VideoProcessorContentDesc*,
            void**,
            int>)device->lpVtbl->CreateVideoProcessorEnumerator;
        Check(create(device, &description, &enumerator), "create D3D11 video processor enumerator");
        return enumerator;
    }

    internal static void CheckVideoProcessorFormat(void* enumerator, int format, uint requiredFlags)
    {
        var vtable = *(void***)enumerator;
        uint flags = 0;
        var check = (delegate* unmanaged[Stdcall]<void*, int, uint*, int>)vtable[8];
        Check(check(enumerator, format, &flags), "check D3D11 video processor format");
        if ((flags & requiredFlags) != requiredFlags)
        {
            throw new FFmpegHardwareException(
                $"D3D11 VideoProcessor does not support format {format} with flags 0x{requiredFlags:X}.");
        }
    }

    internal static void* CreateVideoProcessor(ID3D11VideoDevice* device, void* enumerator)
    {
        void* processor = null;
        var create = (delegate* unmanaged[Stdcall]<ID3D11VideoDevice*, void*, uint, void**, int>)
            device->lpVtbl->CreateVideoProcessor;
        Check(create(device, enumerator, 0, &processor), "create D3D11 video processor");
        return processor;
    }

    internal static void* CreateInputView(
        ID3D11VideoDevice* device,
        ID3D11Texture2D* texture,
        void* enumerator,
        uint arraySlice)
    {
        var description = new D3D11VideoProcessorInputViewDesc
        {
            Dimension = 1,
            MipSlice = 0,
            ArraySlice = arraySlice
        };
        void* view = null;
        var create = (delegate* unmanaged[Stdcall]<
            ID3D11VideoDevice*,
            ID3D11Texture2D*,
            void*,
            D3D11VideoProcessorInputViewDesc*,
            void**,
            int>)device->lpVtbl->CreateVideoProcessorInputView;
        Check(create(device, texture, enumerator, &description, &view), "create D3D11 video processor input view");
        return view;
    }

    internal static void* CreateOutputView(
        ID3D11VideoDevice* device,
        ID3D11Texture2D* texture,
        void* enumerator)
    {
        var description = new D3D11VideoProcessorOutputViewDesc
        {
            Dimension = 1,
            MipSlice = 0
        };
        void* view = null;
        var create = (delegate* unmanaged[Stdcall]<
            ID3D11VideoDevice*,
            ID3D11Texture2D*,
            void*,
            D3D11VideoProcessorOutputViewDesc*,
            void**,
            int>)device->lpVtbl->CreateVideoProcessorOutputView;
        Check(create(device, texture, enumerator, &description, &view), "create D3D11 video processor output view");
        return view;
    }

    internal static void ConfigureAndBlit(
        ID3D11VideoContext* context,
        void* processor,
        void* inputView,
        void* outputView,
        int width,
        int height,
        bool fullRange,
        bool bt709)
    {
        var rectangle = new D3D11Rect { Right = width, Bottom = height };
        var inputColorSpace = new D3D11VideoProcessorColorSpace
        {
            Value = (bt709 ? 1u << 2 : 0u) | ((fullRange ? 2u : 1u) << 4)
        };
        var outputColorSpace = new D3D11VideoProcessorColorSpace
        {
            Value = 2u << 4
        };

        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, int, D3D11Rect*, void>)
            context->lpVtbl->VideoProcessorSetOutputTargetRect)(context, processor, 1, &rectangle);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, D3D11VideoProcessorColorSpace*, void>)
            context->lpVtbl->VideoProcessorSetOutputColorSpace)(context, processor, &outputColorSpace);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, uint, uint, void>)
            context->lpVtbl->VideoProcessorSetStreamFrameFormat)(context, processor, 0, 0);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, uint, D3D11VideoProcessorColorSpace*, void>)
            context->lpVtbl->VideoProcessorSetStreamColorSpace)(context, processor, 0, &inputColorSpace);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, uint, int, D3D11Rect*, void>)
            context->lpVtbl->VideoProcessorSetStreamSourceRect)(context, processor, 0, 1, &rectangle);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, uint, int, D3D11Rect*, void>)
            context->lpVtbl->VideoProcessorSetStreamDestRect)(context, processor, 0, 1, &rectangle);
        ((delegate* unmanaged[Stdcall]<ID3D11VideoContext*, void*, uint, int, void>)
            context->lpVtbl->VideoProcessorSetStreamAutoProcessingMode)(context, processor, 0, 0);

        var stream = new D3D11VideoProcessorStream
        {
            Enable = 1,
            InputSurface = inputView
        };
        var blit = (delegate* unmanaged[Stdcall]<
            ID3D11VideoContext*,
            void*,
            void*,
            uint,
            uint,
            D3D11VideoProcessorStream*,
            int>)context->lpVtbl->VideoProcessorBlt;
        Check(blit(context, processor, outputView, 0, 1, &stream), "convert a D3D11VA frame to BGRA8");
    }

    internal static void Check(int result, string operation)
    {
        if (result < 0)
            throw new FFmpegHardwareException($"Failed to {operation} (HRESULT 0x{result:X8}).");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiRational
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSampleDesc
{
    public uint Count;
    public uint Quality;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public int Format;
    public DxgiSampleDesc SampleDescription;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorContentDesc
{
    public uint InputFrameFormat;
    public DxgiRational InputFrameRate;
    public uint InputWidth;
    public uint InputHeight;
    public DxgiRational OutputFrameRate;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint Usage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorInputViewDesc
{
    public uint FourCc;
    public uint Dimension;
    public uint MipSlice;
    public uint ArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorOutputViewDesc
{
    public uint Dimension;
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorColorSpace
{
    public uint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex;
    public uint InputFrameOrField;
    public uint PastFrames;
    public uint FutureFrames;
    public void** PastSurfaces;
    public void* InputSurface;
    public void** FutureSurfaces;
    public void** PastSurfacesRight;
    public void* InputSurfaceRight;
    public void** FutureSurfacesRight;
}
