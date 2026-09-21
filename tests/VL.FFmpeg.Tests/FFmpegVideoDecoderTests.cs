using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Tests;

public sealed class FFmpegVideoDecoderTests
{
    [TestCase("vp8-alpha.webm", DecodeMode.Auto)]
    [TestCase("vp9-alpha.webm", DecodeMode.Auto)]
    [TestCase("vp9-alpha.webm", DecodeMode.Software)]
    public void WebMAlphaIsPreserved(string fixture, DecodeMode decodeMode)
    {
        var filename = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "VL.FFmpeg.Tests",
            "TestData",
            fixture);
        DecodedVideoFrame? decodedFrame = null;
        using var decoder = new FFmpegVideoDecoder(
            filename,
            TimeSpan.Zero,
            CancellationToken.None,
            FindRepositoryRuntime(),
            decodeMode: decodeMode);

        decoder.Decode(frame =>
        {
            decodedFrame = frame;
            return false;
        });

        Assert.That(decodedFrame, Is.Not.Null);
        using var handle = decodedFrame!.CreateProvider().GetHandle();
        Assert.That(handle.Resource.TryGetMemory(out var memory), Is.True);
        var alpha = memory.ToArray().Where((_, index) => index % 4 == 3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alpha, Has.All.EqualTo(63));
            Assert.That(decodedFrame.DecodeStatus, Does.Not.Contain("alpha declared but unavailable"));
        }
        decodedFrame.Dispose();
    }

    [Test]
    public void DeclaredAlphaWithoutDecodedAlphaReportsOpaqueFallback()
    {
        var filename = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "VL.FFmpeg.Tests",
            "TestData",
            "vp9-declared-alpha-without-payload.webm");
        DecodedVideoFrame? decodedFrame = null;
        using var decoder = new FFmpegVideoDecoder(
            filename,
            TimeSpan.Zero,
            CancellationToken.None,
            FindRepositoryRuntime(),
            decodeMode: DecodeMode.Software);

        decoder.Decode(frame =>
        {
            decodedFrame = frame;
            return false;
        });

        Assert.That(decodedFrame, Is.Not.Null);
        using var handle = decodedFrame!.CreateProvider().GetHandle();
        Assert.That(handle.Resource.TryGetMemory(out var memory), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                memory.ToArray().Where((_, index) => index % 4 == 3),
                Has.All.EqualTo(255));
            Assert.That(decodedFrame.DecodeStatus, Does.Contain("alpha declared but unavailable"));
            Assert.That(decodedFrame.DecodeStatus, Does.Contain("output is opaque"));
        }
        decodedFrame.Dispose();
    }

    [Test]
    public void SeekDiscardsKeyframePrerollBeforePublishing()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        DecodedVideoFrame? firstFrame = null;
        var seekPosition = TimeSpan.FromSeconds(0.5d);
        using var decoder = new FFmpegVideoDecoder(
            filename!,
            seekPosition,
            CancellationToken.None,
            FindRepositoryRuntime());

        decoder.Decode(frame =>
        {
            firstFrame = frame;
            return false;
        });

        Assert.That(firstFrame, Is.Not.Null);
        Assert.That(firstFrame!.Timecode,
            Is.GreaterThanOrEqualTo(seekPosition - TimeSpan.FromMilliseconds(1)));
        firstFrame.Dispose();
    }

    [Test]
    public void DecodesBgraFramesFromGammaReferenceClip()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        var frames = new List<DecodedVideoFrame>();
        using var decoder = new FFmpegVideoDecoder(
            filename!,
            TimeSpan.Zero,
            CancellationToken.None,
            FindRepositoryRuntime());

        decoder.Decode(frame =>
        {
            frames.Add(frame);
            return frames.Count < 4;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.MediaInfo.Width, Is.GreaterThan(0));
            Assert.That(decoder.MediaInfo.Height, Is.GreaterThan(0));
            Assert.That(decoder.MediaInfo.Duration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(decoder.MediaInfo.FrameRate.N, Is.GreaterThan(0));
            Assert.That(decoder.MediaInfo.VideoCodec, Is.Not.Empty);
            Assert.That(frames, Has.Count.EqualTo(4));
            Assert.That(frames.Select(frame => frame.Timecode), Is.Ordered);
        }

        foreach (var frame in frames)
        {
            using var handle = frame.CreateProvider().GetHandle();
            var hasMemory = handle.Resource.TryGetMemory(out var memory);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(frame.Width, Is.EqualTo(decoder.MediaInfo.Width));
                Assert.That(frame.Height, Is.EqualTo(decoder.MediaInfo.Height));
                Assert.That(hasMemory, Is.True);
                Assert.That(memory.Length, Is.EqualTo(checked(frame.Width * frame.Height * 4)));
                Assert.That(memory.ToArray().Where((_, index) => index % 4 == 3),
                    Has.Some.EqualTo(255));
            }
            frame.Dispose();
        }
    }

    [Test]
    public void LinearConsumerReceivesRgba16fSoftwareFrame()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        DecodedVideoFrame? decodedFrame = null;
        using var decoder = new FFmpegVideoDecoder(
            filename,
            TimeSpan.Zero,
            CancellationToken.None,
            FindRepositoryRuntime(),
            usesLinearColorspace: true);

        decoder.Decode(frame =>
        {
            decodedFrame = frame;
            return false;
        });

        using var handle = decodedFrame!.CreateProvider().GetHandle();
        var hasMemory = handle.Resource.TryGetMemory(out var memory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handle.Resource.PixelFormat,
                Is.EqualTo(VL.Lib.Basics.Imaging.PixelFormat.R16G16B16A16F));
            Assert.That(hasMemory, Is.True);
            Assert.That(memory.Length,
                Is.EqualTo(checked(decodedFrame.Width * decodedFrame.Height * 8)));
            Assert.That(decodedFrame.DecodeStatus, Does.Contain("linear RGBA16F"));
        }
        decodedFrame.Dispose();
    }

    private static string? FindGammaReferenceClip()
    {
        const string vvvvRoot = @"C:\Program Files\vvvv";
        if (!Directory.Exists(vvvvRoot))
            return null;

        return Directory
            .EnumerateDirectories(vvvvRoot, "vvvv_gamma_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "packs", "VL.Video", "help", "Birds_H264.mp4"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindRepositoryRuntime()
    {
        var candidate = Path.Combine(FindRepositoryRoot(), "runtimes", "win-x64", "native");
        return File.Exists(Path.Combine(candidate, "avcodec-62.dll")) ? candidate : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
