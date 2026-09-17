using System.Runtime.InteropServices;
using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests;

public sealed unsafe class SoftwareD3D11ConverterTests
{
    [Test]
    [Platform("Win")]
    public void PlanarTwelveBitAlphaIsPreserved()
    {
        EnsureRuntime();
        var createResult = D3D11CreateDevice(
            0, 1, 0, 0x20, 0, 0, 7,
            out var devicePointer,
            out _,
            out var immediateContextPointer);
        if (createResult < 0 || devicePointer == 0)
            Assert.Ignore($"No hardware D3D11 device is available (HRESULT 0x{createResult:X8}).");

        AVFrame* frame = null;
        SoftwareD3D11FrameConverter? converter = null;
        SoftwareD3D11TextureLease? lease = null;
        try
        {
            frame = ffmpeg.av_frame_alloc();
            Assert.That((nint)frame, Is.Not.EqualTo(nint.Zero));
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUVA444P12LE;
            frame->width = 4;
            frame->height = 1;
            frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_LINEAR;
            Assert.That(ffmpeg.av_frame_get_buffer(frame, 32), Is.GreaterThanOrEqualTo(0));

            for (var component = 0; component < 3; component++)
            {
                var values = (ushort*)frame->data[(uint)component];
                for (var x = 0; x < frame->width; x++)
                    values[x] = 2048;
            }
            var alpha = (ushort*)frame->data[3];
            alpha[0] = 0;
            alpha[1] = 1365;
            alpha[2] = 2730;
            alpha[3] = 4095;

            converter = new SoftwareD3D11FrameConverter(devicePointer, linearOutput: false);
            var color = VideoColorInfo.Resolve(
                frame->colorspace,
                frame->color_range,
                frame->color_trc,
                frame->height);
            var converted = converter.TryConvert(
                frame,
                color,
                CancellationToken.None,
                out lease,
                out var status);
            Assert.That(converted, Is.True, status);

            var output = ReadBgra(
                (ID3D11Device*)devicePointer,
                (ID3D11DeviceContext*)immediateContextPointer,
                (ID3D11Texture2D*)lease!.Texture.NativePointer,
                4,
                1);
            Assert.That(output[3], Is.EqualTo(0).Within(2));
            Assert.That(output[7], Is.EqualTo(85).Within(2));
            Assert.That(output[11], Is.EqualTo(170).Within(2));
            Assert.That(output[15], Is.EqualTo(255).Within(2));
        }
        finally
        {
            lease?.Dispose();
            converter?.Dispose();
            if (frame is not null)
                ffmpeg.av_frame_free(&frame);
            if (immediateContextPointer != 0)
                Marshal.Release(immediateContextPointer);
            if (devicePointer != 0)
                Marshal.Release(devicePointer);
        }
    }

    [Test]
    [Platform("Win")]
    public void LimitedRangeYuvIsConvertedOnTheGpu()
    {
        EnsureRuntime();
        var createResult = D3D11CreateDevice(
            0, 1, 0, 0x20, 0, 0, 7,
            out var devicePointer,
            out _,
            out var immediateContextPointer);
        if (createResult < 0 || devicePointer == 0)
            Assert.Ignore($"No hardware D3D11 device is available (HRESULT 0x{createResult:X8}).");

        AVFrame* frame = null;
        SoftwareD3D11FrameConverter? converter = null;
        SoftwareD3D11TextureLease? lease = null;
        try
        {
            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = 4;
            frame->height = 2;
            frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            Assert.That(ffmpeg.av_frame_get_buffer(frame, 32), Is.GreaterThanOrEqualTo(0));

            for (var y = 0; y < 2; y++)
            {
                var row = frame->data[0] + y * frame->linesize[0];
                row[0] = 16;
                row[1] = 64;
                row[2] = 128;
                row[3] = 235;
            }
            frame->data[1][0] = 128;
            frame->data[1][1] = 128;
            frame->data[2][0] = 128;
            frame->data[2][1] = 128;

            converter = new SoftwareD3D11FrameConverter(devicePointer, linearOutput: false);
            var color = VideoColorInfo.Resolve(
                frame->colorspace,
                frame->color_range,
                frame->color_trc,
                frame->height);
            Assert.That(converter.TryConvert(
                frame,
                color,
                CancellationToken.None,
                out lease,
                out var status), Is.True, status);

            var output = ReadBgra(
                (ID3D11Device*)devicePointer,
                (ID3D11DeviceContext*)immediateContextPointer,
                (ID3D11Texture2D*)lease!.Texture.NativePointer,
                4,
                2);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output[0], Is.LessThanOrEqualTo(2));
                Assert.That(output[1], Is.LessThanOrEqualTo(2));
                Assert.That(output[2], Is.LessThanOrEqualTo(2));
                Assert.That(output[12], Is.GreaterThanOrEqualTo(253));
                Assert.That(output[13], Is.GreaterThanOrEqualTo(253));
                Assert.That(output[14], Is.GreaterThanOrEqualTo(253));
            }
        }
        finally
        {
            lease?.Dispose();
            converter?.Dispose();
            if (frame is not null)
                ffmpeg.av_frame_free(&frame);
            if (immediateContextPointer != 0)
                Marshal.Release(immediateContextPointer);
            if (devicePointer != 0)
                Marshal.Release(devicePointer);
        }
    }

    [Test]
    [Platform("Win")]
    public void PackedRgbaIsLinearizedOnTheGpu()
    {
        EnsureRuntime();
        var createResult = D3D11CreateDevice(
            0, 1, 0, 0x20, 0, 0, 7,
            out var devicePointer,
            out _,
            out var immediateContextPointer);
        if (createResult < 0 || devicePointer == 0)
            Assert.Ignore($"No hardware D3D11 device is available (HRESULT 0x{createResult:X8}).");

        AVFrame* frame = null;
        SoftwareD3D11FrameConverter? converter = null;
        SoftwareD3D11TextureLease? lease = null;
        try
        {
            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            frame->width = 1;
            frame->height = 1;
            frame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
            Assert.That(ffmpeg.av_frame_get_buffer(frame, 32), Is.GreaterThanOrEqualTo(0));
            frame->data[0][0] = 128;
            frame->data[0][1] = 64;
            frame->data[0][2] = 32;
            frame->data[0][3] = 128;

            converter = new SoftwareD3D11FrameConverter(devicePointer, linearOutput: true);
            var color = VideoColorInfo.Resolve(
                frame->colorspace,
                frame->color_range,
                frame->color_trc,
                frame->height);
            Assert.That(converter.TryConvert(
                frame,
                color,
                CancellationToken.None,
                out lease,
                out var status), Is.True, status);

            var bytes = ReadTexture(
                (ID3D11Device*)devicePointer,
                (ID3D11DeviceContext*)immediateContextPointer,
                (ID3D11Texture2D*)lease!.Texture.NativePointer,
                1,
                1,
                8);
            var values = MemoryMarshal.Cast<byte, ushort>(bytes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That((float)BitConverter.UInt16BitsToHalf(values[0]), Is.EqualTo(0.2159f).Within(0.002f));
                Assert.That((float)BitConverter.UInt16BitsToHalf(values[1]), Is.EqualTo(0.0513f).Within(0.002f));
                Assert.That((float)BitConverter.UInt16BitsToHalf(values[2]), Is.EqualTo(0.0144f).Within(0.002f));
                Assert.That((float)BitConverter.UInt16BitsToHalf(values[3]), Is.EqualTo(128f / 255f).Within(0.002f));
            }
        }
        finally
        {
            lease?.Dispose();
            converter?.Dispose();
            if (frame is not null)
                ffmpeg.av_frame_free(&frame);
            if (immediateContextPointer != 0)
                Marshal.Release(immediateContextPointer);
            if (devicePointer != 0)
                Marshal.Release(devicePointer);
        }
    }

    private static byte[] ReadBgra(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        ID3D11Texture2D* source,
        int width,
        int height)
        => ReadTexture(device, context, source, width, height, 4);

    private static byte[] ReadTexture(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        ID3D11Texture2D* source,
        int width,
        int height,
        int bytesPerPixel)
    {
        var sourceDescription = D3D11Interop.GetDescription(source);
        var stagingDescription = sourceDescription;
        stagingDescription.Usage = 3;
        stagingDescription.BindFlags = 0;
        stagingDescription.CpuAccessFlags = 0x20000;
        ID3D11Texture2D* staging = null;
        D3D11Interop.Check(
            ((delegate* unmanaged[Stdcall]<ID3D11Device*, D3D11Texture2DDesc*, void*, ID3D11Texture2D**, int>)
                device->lpVtbl->CreateTexture2D)(device, &stagingDescription, null, &staging),
            "create test readback texture");
        try
        {
            ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, void*, void>)
                context->lpVtbl->CopyResource)(context, staging, source);
            D3D11MappedSubresource mapped;
            D3D11Interop.Check(
                ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)
                    context->lpVtbl->Map)(context, staging, 0, 1, 0, &mapped),
                "map test readback texture");
            try
            {
                var rowBytes = checked(width * bytesPerPixel);
                var result = new byte[checked(rowBytes * height)];
                for (var y = 0; y < height; y++)
                {
                    var row = (byte*)mapped.Data + y * mapped.RowPitch;
                    Marshal.Copy((nint)row, result, y * rowBytes, rowBytes);
                }
                return result;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, void*, uint, void>)
                    context->lpVtbl->Unmap)(context, staging, 0);
            }
        }
        finally
        {
            D3D11Interop.Release(staging);
        }
    }

    private static void EnsureRuntime()
    {
        var result = FFmpegRuntime.Probe(FindRepositoryRuntime());
        Assert.That(result.Available, Is.True, result.Status);
    }

    private static string FindRepositoryRuntime()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return Path.Combine(directory.FullName, "runtimes", "win-x64", "native");
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository runtime directory was not found.");
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        uint driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint selectedFeatureLevel,
        out nint immediateContext);
}
