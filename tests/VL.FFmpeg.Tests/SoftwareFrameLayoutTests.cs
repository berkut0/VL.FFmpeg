using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests;

public sealed unsafe class SoftwareFrameLayoutTests
{
    [TestCase(AVPixelFormat.AV_PIX_FMT_YUV420P, 3, false, 8)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_YUV422P10LE, 3, false, 10)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE, 4, true, 12)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_GBRAP16LE, 4, true, 16)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_NV12, 2, false, 8)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_P010LE, 2, false, 10)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_RGBA, 1, true, 8)]
    [TestCase(AVPixelFormat.AV_PIX_FMT_BGRA64LE, 1, true, 16)]
    public void SupportedLayoutsAreDerivedFromPixelFormat(
        AVPixelFormat format,
        int planeCount,
        bool alpha,
        int depth)
    {
        EnsureRuntime();
        var frame = new AVFrame { format = (int)format, width = 17, height = 9 };

        var supported = SoftwareFrameLayout.TryCreate(&frame, out var layout, out var reason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(supported, Is.True, reason);
            Assert.That(layout.Planes, Has.Length.EqualTo(planeCount));
            Assert.That(layout.HasAlpha, Is.EqualTo(alpha));
            Assert.That(layout.ComponentDepth, Is.EqualTo(depth));
            Assert.That(layout.Width, Is.EqualTo(17));
            Assert.That(layout.Height, Is.EqualTo(9));
        }
    }

    [Test]
    public void BigEndianLayoutUsesCpuFallback()
    {
        EnsureRuntime();
        var frame = new AVFrame
        {
            format = (int)AVPixelFormat.AV_PIX_FMT_YUV422P10BE,
            width = 16,
            height = 8
        };

        var supported = SoftwareFrameLayout.TryCreate(&frame, out _, out var reason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(supported, Is.False);
            Assert.That(reason, Does.Contain("unsupported software pixel layout"));
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
}
