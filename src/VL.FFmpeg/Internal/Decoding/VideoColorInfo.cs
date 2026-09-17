using System.Runtime.InteropServices;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal enum VideoTransferFunction
{
    Bt709,
    Srgb,
    Gamma22,
    Gamma28,
    Linear,
    Hdr,
    Unsupported
}

internal readonly record struct VideoColorInfo(
    int SwsColorSpace,
    string MatrixName,
    bool FullRange,
    bool MatrixAssumed,
    bool RangeAssumed,
    VideoTransferFunction Transfer,
    string TransferName,
    bool TransferAssumed)
{
    public string Description
        => $"{MatrixName} {(FullRange ? "full" : "limited")}{Assumptions}";

    public string RgbDescription
        => $"RGB full{(TransferAssumed ? " (transfer metadata assumed)" : string.Empty)}";

    public bool IsHdr => Transfer == VideoTransferFunction.Hdr;

    public int GetDxgiInputColorSpace()
        => SwsColorSpace switch
        {
            ffmpeg.SWS_CS_ITU601 => FullRange ? 7 : 6,
            ffmpeg.SWS_CS_ITU709 => FullRange ? 9 : 8,
            ffmpeg.SWS_CS_BT2020 => FullRange ? 11 : 10,
            _ => throw new NotSupportedException(
                $"D3D11 video processing does not support the {MatrixName} matrix explicitly.")
        };

    private string Assumptions
        => MatrixAssumed || RangeAssumed || TransferAssumed ? " (metadata assumed)" : string.Empty;

    public static VideoColorInfo Resolve(
        AVColorSpace colorSpace,
        AVColorRange colorRange,
        AVColorTransferCharacteristic transfer,
        int height)
    {
        var matrixAssumed = colorSpace == AVColorSpace.AVCOL_SPC_UNSPECIFIED;
        var matrix = colorSpace switch
        {
            AVColorSpace.AVCOL_SPC_RGB => (ffmpeg.SWS_CS_DEFAULT, "RGB"),
            AVColorSpace.AVCOL_SPC_BT709 => (ffmpeg.SWS_CS_ITU709, "BT.709"),
            AVColorSpace.AVCOL_SPC_FCC => (ffmpeg.SWS_CS_FCC, "FCC"),
            AVColorSpace.AVCOL_SPC_BT470BG => (ffmpeg.SWS_CS_ITU601, "BT.601"),
            AVColorSpace.AVCOL_SPC_SMPTE170M => (ffmpeg.SWS_CS_SMPTE170M, "BT.601"),
            AVColorSpace.AVCOL_SPC_SMPTE240M => (ffmpeg.SWS_CS_SMPTE240M, "SMPTE 240M"),
            AVColorSpace.AVCOL_SPC_BT2020_NCL => (ffmpeg.SWS_CS_BT2020, "BT.2020 NCL"),
            AVColorSpace.AVCOL_SPC_UNSPECIFIED when height >= 720
                => (ffmpeg.SWS_CS_ITU709, "BT.709"),
            AVColorSpace.AVCOL_SPC_UNSPECIFIED
                => (ffmpeg.SWS_CS_ITU601, "BT.601"),
            _ => throw new NotSupportedException($"Unsupported video color matrix: {colorSpace}.")
        };

        var rangeAssumed = colorRange == AVColorRange.AVCOL_RANGE_UNSPECIFIED;
        var fullRange = colorRange == AVColorRange.AVCOL_RANGE_JPEG
            || (colorSpace == AVColorSpace.AVCOL_SPC_RGB && rangeAssumed);
        var transferAssumed = transfer == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED;
        var transferInfo = transfer switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_LINEAR
                => (VideoTransferFunction.Linear, "linear"),
            AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1
                => (VideoTransferFunction.Srgb, "sRGB"),
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22
                => (VideoTransferFunction.Gamma22, "gamma 2.2"),
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28
                => (VideoTransferFunction.Gamma28, "gamma 2.8"),
            AVColorTransferCharacteristic.AVCOL_TRC_BT709
                or AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M
                or AVColorTransferCharacteristic.AVCOL_TRC_SMPTE240M
                or AVColorTransferCharacteristic.AVCOL_TRC_BT2020_10
                or AVColorTransferCharacteristic.AVCOL_TRC_BT2020_12
                or AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
                => (VideoTransferFunction.Bt709, "BT.709"),
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
                or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67
                => (VideoTransferFunction.Hdr, transfer.ToString()),
            _ => (VideoTransferFunction.Unsupported, transfer.ToString())
        };

        return new VideoColorInfo(
            matrix.Item1,
            matrix.Item2,
            fullRange,
            matrixAssumed,
            rangeAssumed,
            transferInfo.Item1,
            transferInfo.Item2,
            transferAssumed);
    }

    public void LinearizeRgba64(Span<byte> rgba)
    {
        if (IsHdr)
            throw new NotSupportedException("HDR tone mapping is not implemented for linear output.");
        if (Transfer == VideoTransferFunction.Unsupported)
            throw new NotSupportedException($"Unsupported video transfer characteristic: {TransferName}.");

        var values = MemoryMarshal.Cast<byte, ushort>(rgba);
        for (var index = 0; index < values.Length; index += 4)
        {
            var red = values[index] / 65535f;
            var green = values[index + 1] / 65535f;
            var blue = values[index + 2] / 65535f;
            var alpha = values[index + 3] / 65535f;
            values[index] = BitConverter.HalfToUInt16Bits((Half)ToLinear(red));
            values[index + 1] = BitConverter.HalfToUInt16Bits((Half)ToLinear(green));
            values[index + 2] = BitConverter.HalfToUInt16Bits((Half)ToLinear(blue));
            values[index + 3] = BitConverter.HalfToUInt16Bits((Half)alpha);
        }
    }

    private float ToLinear(float value)
        => Transfer switch
        {
            VideoTransferFunction.Linear => value,
            VideoTransferFunction.Srgb => value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f),
            VideoTransferFunction.Gamma22 => MathF.Pow(value, 2.2f),
            VideoTransferFunction.Gamma28 => MathF.Pow(value, 2.8f),
            _ => value < 0.081f
                ? value / 4.5f
                : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f)
        };
}
