using System.Runtime.InteropServices;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal sealed record SoftwareFrameLayout(
    string Name,
    AVPixelFormat PixelFormat,
    int Width,
    int Height,
    uint InputMode,
    bool HasAlpha,
    int ComponentDepth,
    int ChromaWidthShift,
    int ChromaHeightShift,
    SoftwarePlaneLayout[] Planes)
{
    public unsafe bool Matches(AVFrame* frame)
        => frame->format == (int)PixelFormat
           && frame->width == Width
           && frame->height == Height;

    public float PlaneScale(int index)
        => index < Planes.Length ? Planes[index].SampleScale : 1f;

    public static unsafe bool TryCreate(
        AVFrame* frame,
        out SoftwareFrameLayout layout,
        out string reason)
    {
        layout = null!;
        var format = (AVPixelFormat)frame->format;
        var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        if (descriptor is null)
        {
            reason = $"FFmpeg returned unknown pixel format {format}";
            return false;
        }
        var name = Marshal.PtrToStringUTF8((nint)descriptor->name) ?? format.ToString();
        if ((descriptor->flags & (ulong)(ffmpeg.AV_PIX_FMT_FLAG_BE | ffmpeg.AV_PIX_FMT_FLAG_BITSTREAM)) != 0)
        {
            reason = $"unsupported software pixel layout {name}";
            return false;
        }

        if (TryCreatePacked(frame, descriptor, format, name, out layout))
        {
            reason = string.Empty;
            return true;
        }

        var planar = (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_PLANAR) != 0;
        if (!planar || descriptor->nb_components < 3)
        {
            reason = $"unsupported software pixel layout {name}";
            return false;
        }

        var rgb = (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        var alpha = (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        var componentCount = alpha ? 4 : 3;
        var components = new AVComponentDescriptor[componentCount];
        for (uint index = 0; index < componentCount; index++)
            components[index] = descriptor->comp[index];
        var depth = components[0].depth;
        if (depth is not (8 or 10 or 12 or 16)
            || components.Any(component => component.depth != depth))
        {
            reason = $"unsupported component depth in {name}";
            return false;
        }
        var bytesPerSample = depth <= 8 ? 1 : 2;

        if (!rgb && components[1].plane == components[2].plane)
        {
            var y = components[0];
            var u = components[1];
            var v = components[2];
            if (alpha || y.step != bytesPerSample || u.step != bytesPerSample * 2
                || v.step != bytesPerSample * 2 || u.offset != 0 || v.offset != bytesPerSample)
            {
                reason = $"unsupported semiplanar layout {name}";
                return false;
            }
            var chromaWidth = CeilShift(frame->width, descriptor->log2_chroma_w);
            var chromaHeight = CeilShift(frame->height, descriptor->log2_chroma_h);
            layout = new(
                name, format, frame->width, frame->height, 1, false, depth,
                descriptor->log2_chroma_w, descriptor->log2_chroma_h,
                [
                    CreatePlane(y, frame->width, frame->height, bytesPerSample, paired: false),
                    CreatePlane(u, chromaWidth, chromaHeight, bytesPerSample, paired: true)
                ]);
            reason = string.Empty;
            return true;
        }

        var planes = new SoftwarePlaneLayout[componentCount];
        for (var semantic = 0; semantic < componentCount; semantic++)
        {
            var component = components[semantic];
            if (component.step != bytesPerSample || component.offset != 0)
            {
                reason = $"unsupported planar component layout {name}";
                return false;
            }
            var chroma = !rgb && semantic is 1 or 2;
            planes[semantic] = CreatePlane(
                component,
                chroma ? CeilShift(frame->width, descriptor->log2_chroma_w) : frame->width,
                chroma ? CeilShift(frame->height, descriptor->log2_chroma_h) : frame->height,
                bytesPerSample,
                paired: false);
        }

        layout = new(
            name, format, frame->width, frame->height, rgb ? 2u : 0u, alpha, depth,
            descriptor->log2_chroma_w, descriptor->log2_chroma_h, planes);
        reason = string.Empty;
        return true;
    }

    private static unsafe bool TryCreatePacked(
        AVFrame* frame,
        AVPixFmtDescriptor* descriptor,
        AVPixelFormat format,
        string name,
        out SoftwareFrameLayout layout)
    {
        int dxgiFormat;
        int bytesPerPixel;
        uint mode = 3;
        bool alpha;
        switch (format)
        {
            case AVPixelFormat.AV_PIX_FMT_RGBA:
                dxgiFormat = D3D11ShaderInterop.FormatR8G8B8A8Unorm;
                bytesPerPixel = 4;
                alpha = true;
                break;
            case AVPixelFormat.AV_PIX_FMT_RGB0:
                dxgiFormat = D3D11ShaderInterop.FormatR8G8B8A8Unorm;
                bytesPerPixel = 4;
                alpha = false;
                break;
            case AVPixelFormat.AV_PIX_FMT_BGRA:
                dxgiFormat = D3D11Interop.DxgiFormatB8G8R8A8Unorm;
                bytesPerPixel = 4;
                alpha = true;
                break;
            case AVPixelFormat.AV_PIX_FMT_BGR0:
                dxgiFormat = D3D11Interop.DxgiFormatB8G8R8A8Unorm;
                bytesPerPixel = 4;
                alpha = false;
                break;
            case AVPixelFormat.AV_PIX_FMT_RGBA64LE:
                dxgiFormat = D3D11ShaderInterop.FormatR16G16B16A16Unorm;
                bytesPerPixel = 8;
                alpha = true;
                break;
            case AVPixelFormat.AV_PIX_FMT_BGRA64LE:
                dxgiFormat = D3D11ShaderInterop.FormatR16G16B16A16Unorm;
                bytesPerPixel = 8;
                mode = 4;
                alpha = true;
                break;
            default:
                layout = null!;
                return false;
        }

        layout = new(
            name,
            format,
            frame->width,
            frame->height,
            mode,
            alpha,
            descriptor->comp[0].depth,
            0,
            0,
            [new SoftwarePlaneLayout(0, frame->width, frame->height, dxgiFormat, bytesPerPixel, 1f)]);
        return true;
    }

    private static SoftwarePlaneLayout CreatePlane(
        AVComponentDescriptor component,
        int width,
        int height,
        int bytesPerSample,
        bool paired)
    {
        var textureMaximum = bytesPerSample == 1 ? 255f : 65535f;
        var componentMaximum = (1u << component.depth) - 1u;
        var shiftedMaximum = componentMaximum << component.shift;
        return new(
            component.plane,
            width,
            height,
            paired
                ? bytesPerSample == 1
                    ? D3D11ShaderInterop.FormatR8G8Unorm
                    : D3D11ShaderInterop.FormatR16G16Unorm
                : bytesPerSample == 1
                    ? D3D11ShaderInterop.FormatR8Unorm
                    : D3D11ShaderInterop.FormatR16Unorm,
            bytesPerSample * (paired ? 2 : 1),
            textureMaximum / shiftedMaximum);
    }

    private static int CeilShift(int value, int shift)
        => (value + (1 << shift) - 1) >> shift;
}

internal sealed record SoftwarePlaneLayout(
    int SourcePlane,
    int Width,
    int Height,
    int DxgiFormat,
    int BytesPerPixel,
    float SampleScale);
