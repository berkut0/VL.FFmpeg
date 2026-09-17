using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Interop;

internal static unsafe class D3D11ShaderInterop
{
    internal const int FormatR16G16B16A16Unorm = 11;
    internal const int FormatR8G8B8A8Unorm = 28;
    internal const int FormatR16G16Unorm = 35;
    internal const int FormatR8G8Unorm = 49;
    internal const int FormatR16Unorm = 56;
    internal const int FormatR8Unorm = 61;

    internal const uint BindConstantBuffer = 0x4;
    internal const uint CpuAccessWrite = 0x10000;
    internal const uint UsageDynamic = 2;
    internal const uint MapWriteDiscardFlag = 4;
    internal const uint PrimitiveTopologyTriangleList = 4;

    internal static ID3D11DeviceContext* GetImmediateContext(ID3D11Device* device)
    {
        ID3D11DeviceContext* context = null;
        ((delegate* unmanaged[Stdcall]<ID3D11Device*, ID3D11DeviceContext**, void>)
            device->lpVtbl->GetImmediateContext)(device, &context);
        if (context is null)
            throw new FFmpegHardwareException("The D3D11 device returned no immediate context.");
        return context;
    }

    internal static ID3D11DeviceContext* CreateDeferredContext(ID3D11Device* device)
    {
        ID3D11DeviceContext* context = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, uint, ID3D11DeviceContext**, int>)
                device->lpVtbl->CreateDeferredContext)(device, 0, &context),
            "create D3D11 deferred context");
        return context;
    }

    internal static void* CreateVertexShader(ID3D11Device* device, ReadOnlySpan<byte> bytecode)
    {
        void* shader = null;
        fixed (byte* pointer = bytecode)
        {
            D3D11Interop.Check(
                ((delegate* unmanaged[Stdcall]<ID3D11Device*, void*, nuint, void*, void**, int>)
                    device->lpVtbl->CreateVertexShader)(device, pointer, (nuint)bytecode.Length, null, &shader),
                "create D3D11 vertex shader");
        }
        return shader;
    }

    internal static void* CreatePixelShader(ID3D11Device* device, ReadOnlySpan<byte> bytecode)
    {
        void* shader = null;
        fixed (byte* pointer = bytecode)
        {
            D3D11Interop.Check(
                ((delegate* unmanaged[Stdcall]<ID3D11Device*, void*, nuint, void*, void**, int>)
                    device->lpVtbl->CreatePixelShader)(device, pointer, (nuint)bytecode.Length, null, &shader),
                "create D3D11 pixel shader");
        }
        return shader;
    }

    internal static void* CreateSampler(ID3D11Device* device)
    {
        var description = new D3D11SamplerDesc
        {
            Filter = 0x15,
            AddressU = 3,
            AddressV = 3,
            AddressW = 3,
            ComparisonFunction = 8,
            MaxLod = float.MaxValue
        };
        void* sampler = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, D3D11SamplerDesc*, void**, int>)
                device->lpVtbl->CreateSamplerState)(device, &description, &sampler),
            "create D3D11 sampler");
        return sampler;
    }

    internal static void* CreateConstantBuffer(ID3D11Device* device, uint byteWidth)
    {
        var description = new D3D11BufferDesc
        {
            ByteWidth = (byteWidth + 15u) & ~15u,
            Usage = UsageDynamic,
            BindFlags = BindConstantBuffer,
            CpuAccessFlags = CpuAccessWrite
        };
        void* buffer = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, D3D11BufferDesc*, void*, void**, int>)
                device->lpVtbl->CreateBuffer)(device, &description, null, &buffer),
            "create D3D11 constant buffer");
        return buffer;
    }

    internal static ID3D11Texture2D* CreateUploadTexture(
        ID3D11Device* device,
        int width,
        int height,
        int format)
    {
        var description = new D3D11Texture2DDesc
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new DxgiSampleDesc { Count = 1 },
            Usage = 0,
            BindFlags = D3D11Interop.BindShaderResource
        };
        ID3D11Texture2D* texture = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, D3D11Texture2DDesc*, void*, ID3D11Texture2D**, int>)
                device->lpVtbl->CreateTexture2D)(device, &description, null, &texture),
            "create D3D11 upload texture");
        return texture;
    }

    internal static void UpdateTexture(
        ID3D11DeviceContext* context,
        ID3D11Texture2D* texture,
        void* source,
        uint sourceRowPitch)
        => ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, uint, void*, void*, uint, uint, void>)
            context->lpVtbl->UpdateSubresource)(
                context,
                texture,
                0,
                null,
                source,
                sourceRowPitch,
                0);

    internal static void* CreateShaderResourceView(ID3D11Device* device, ID3D11Texture2D* texture)
    {
        void* view = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, void*, void*, void**, int>)
                device->lpVtbl->CreateShaderResourceView)(device, texture, null, &view),
            "create D3D11 shader resource view");
        return view;
    }

    internal static void* CreateRenderTargetView(ID3D11Device* device, ID3D11Texture2D* texture)
    {
        void* view = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, void*, void*, void**, int>)
                device->lpVtbl->CreateRenderTargetView)(device, texture, null, &view),
            "create D3D11 render target view");
        return view;
    }

    internal static D3D11MappedSubresource MapWriteDiscard(
        ID3D11DeviceContext* context,
        void* resource)
    {
        D3D11MappedSubresource mapped;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)
                context->lpVtbl->Map)(context, resource, 0, MapWriteDiscardFlag, 0, &mapped),
            "map D3D11 upload resource");
        return mapped;
    }

    internal static void Unmap(ID3D11DeviceContext* context, void* resource)
        => ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, uint, void>)
            context->lpVtbl->Unmap)(context, resource, 0);

    internal static void Draw(
        ID3D11DeviceContext* context,
        void* vertexShader,
        void* pixelShader,
        void* sampler,
        void* constantBuffer,
        void** shaderResourceViews,
        void* renderTarget,
        int width,
        int height)
    {
        var viewport = new D3D11Viewport
        {
            Width = width,
            Height = height,
            MaxDepth = 1f
        };
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, void>)
            context->lpVtbl->IASetPrimitiveTopology)(context, PrimitiveTopologyTriangleList);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, D3D11Viewport*, void>)
            context->lpVtbl->RSSetViewports)(context, 1, &viewport);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, void**, uint, void>)
            context->lpVtbl->VSSetShader)(context, vertexShader, null, 0);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, void**, uint, void>)
            context->lpVtbl->PSSetShader)(context, pixelShader, null, 0);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, uint, void**, void>)
            context->lpVtbl->PSSetShaderResources)(context, 0, 4, shaderResourceViews);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, uint, void**, void>)
            context->lpVtbl->PSSetSamplers)(context, 0, 1, &sampler);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, uint, void**, void>)
            context->lpVtbl->PSSetConstantBuffers)(context, 0, 1, &constantBuffer);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, void**, void*, void>)
            context->lpVtbl->OMSetRenderTargets)(context, 1, &renderTarget, null);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, uint, void>)
            context->lpVtbl->Draw)(context, 3, 0);

        void* nullView = null;
        for (var index = 0; index < 4; index++)
            shaderResourceViews[index] = null;
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, uint, void**, void>)
            context->lpVtbl->PSSetShaderResources)(context, 0, 4, shaderResourceViews);
        ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, uint, void**, void*, void>)
            context->lpVtbl->OMSetRenderTargets)(context, 1, &nullView, null);
    }

    internal static void* FinishCommandList(ID3D11DeviceContext* deferredContext)
    {
        void* commandList = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, int, void**, int>)
                deferredContext->lpVtbl->FinishCommandList)(deferredContext, 0, &commandList),
            "finish D3D11 command list");
        return commandList;
    }

    internal static void ExecuteCommandList(ID3D11DeviceContext* context, void* commandList)
        => ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, int, void>)
            context->lpVtbl->ExecuteCommandList)(context, commandList, 1);
}

internal struct D3D11BufferDesc
{
    public uint ByteWidth;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
    public uint StructureByteStride;
}

internal unsafe struct D3D11MappedSubresource
{
    public void* Data;
    public uint RowPitch;
    public uint DepthPitch;
}

internal struct D3D11SamplerDesc
{
    public int Filter;
    public int AddressU;
    public int AddressV;
    public int AddressW;
    public float MipLodBias;
    public uint MaxAnisotropy;
    public int ComparisonFunction;
    public float BorderColor0;
    public float BorderColor1;
    public float BorderColor2;
    public float BorderColor3;
    public float MinLod;
    public float MaxLod;
}

internal struct D3D11Viewport
{
    public float TopLeftX;
    public float TopLeftY;
    public float Width;
    public float Height;
    public float MinDepth;
    public float MaxDepth;
}
