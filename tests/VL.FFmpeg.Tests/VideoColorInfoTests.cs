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
}
