using System.Runtime.InteropServices;
using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests;

public sealed class VideoColorInfoTests
{
    [Test]
    public void ResolvesBt709FullRangeMetadata()
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_BT709,
            AVColorRange.AVCOL_RANGE_JPEG,
            AVColorTransferCharacteristic.AVCOL_TRC_BT709,
            height: 1080);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(color.SwsColorSpace, Is.EqualTo(ffmpeg.SWS_CS_ITU709));
            Assert.That(color.FullRange, Is.True);
            Assert.That(color.GetDxgiInputColorSpace(), Is.EqualTo(9));
            Assert.That(color.Description, Is.EqualTo("BT.709 full"));
        }
    }

    [Test]
    public void UnspecifiedHdMetadataUsesReportedBt709LimitedAssumption()
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_UNSPECIFIED,
            AVColorRange.AVCOL_RANGE_UNSPECIFIED,
            AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED,
            height: 1080);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(color.SwsColorSpace, Is.EqualTo(ffmpeg.SWS_CS_ITU709));
            Assert.That(color.FullRange, Is.False);
            Assert.That(color.Description, Does.Contain("metadata assumed"));
        }
    }

    [Test]
    public void Bt709TransferIsConvertedToLinearHalfFloats()
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_BT709,
            AVColorRange.AVCOL_RANGE_MPEG,
            AVColorTransferCharacteristic.AVCOL_TRC_BT709,
            height: 1080);
        var rgba = new byte[8];
        var encoded = MemoryMarshal.Cast<byte, ushort>(rgba);
        encoded[0] = encoded[1] = encoded[2] = 32768;
        encoded[3] = ushort.MaxValue;

        color.LinearizeRgba64(rgba);

        var linear = MemoryMarshal.Cast<byte, Half>(rgba);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((float)linear[0], Is.EqualTo(0.26f).Within(0.002f));
            Assert.That((float)linear[1], Is.EqualTo((float)linear[0]));
            Assert.That((float)linear[2], Is.EqualTo((float)linear[0]));
            Assert.That((float)linear[3], Is.EqualTo(1f));
        }
    }

    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_LINEAR, 0.5f)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1, 0.214f)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22, 0.218f)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28, 0.144f)]
    public void CommonTransfersAreConvertedToExpectedLinearHalfFloats(
        AVColorTransferCharacteristic transfer,
        float expected)
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_BT709,
            AVColorRange.AVCOL_RANGE_MPEG,
            transfer,
            height: 1080);
        var rgba = new byte[8];
        var encoded = MemoryMarshal.Cast<byte, ushort>(rgba);
        encoded[0] = encoded[1] = encoded[2] = 32768;
        encoded[3] = ushort.MaxValue;

        color.LinearizeRgba64(rgba);

        var linear = MemoryMarshal.Cast<byte, Half>(rgba);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((float)linear[0], Is.EqualTo(expected).Within(0.002f));
            Assert.That((float)linear[1], Is.EqualTo(expected).Within(0.002f));
            Assert.That((float)linear[2], Is.EqualTo(expected).Within(0.002f));
            Assert.That((float)linear[3], Is.EqualTo(1f));
        }
    }

    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_BT709)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28)]
    [TestCase(AVColorTransferCharacteristic.AVCOL_TRC_LINEAR)]
    public void LinearizationMatchesTransferReferenceBitForBit(
        AVColorTransferCharacteristic transfer)
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_BT709,
            AVColorRange.AVCOL_RANGE_MPEG,
            transfer,
            height: 1080);
        var rgba = new byte[checked((ushort.MaxValue + 1) * 8)];
        var encoded = MemoryMarshal.Cast<byte, ushort>(rgba);
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var pixel = value * 4;
            encoded[pixel] = encoded[pixel + 1] = encoded[pixel + 2] = encoded[pixel + 3] = (ushort)value;
        }

        color.LinearizeRgba64(rgba);

        var converted = MemoryMarshal.Cast<byte, ushort>(rgba);
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var pixel = value * 4;
            var expectedColor = BitConverter.HalfToUInt16Bits((Half)ToLinearReference((ushort)value, transfer));
            var expectedAlpha = BitConverter.HalfToUInt16Bits((Half)(value / 65535f));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(converted[pixel], Is.EqualTo(expectedColor));
                Assert.That(converted[pixel + 1], Is.EqualTo(expectedColor));
                Assert.That(converted[pixel + 2], Is.EqualTo(expectedColor));
                Assert.That(converted[pixel + 3], Is.EqualTo(expectedAlpha));
            }
        }
    }

    [Test]
    public void HdrTransferIsRejectedForLinearOutput()
    {
        var color = VideoColorInfo.Resolve(
            AVColorSpace.AVCOL_SPC_BT2020_NCL,
            AVColorRange.AVCOL_RANGE_MPEG,
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
            height: 2160);

        Assert.Throws<NotSupportedException>(
            (Action)(() => color.LinearizeRgba64(new byte[8])));
    }

    private static float ToLinearReference(
        ushort encoded,
        AVColorTransferCharacteristic transfer)
    {
        var value = encoded / 65535f;
        return transfer switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_LINEAR => value,
            AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f),
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22 => MathF.Pow(value, 2.2f),
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28 => MathF.Pow(value, 2.8f),
            _ => value < 0.081f
                ? value / 4.5f
                : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f)
        };
    }
}
