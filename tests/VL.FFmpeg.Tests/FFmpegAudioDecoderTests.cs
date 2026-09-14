using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;

namespace VL.FFmpeg.Tests;

public sealed class FFmpegAudioDecoderTests
{
    [Test]
    public void DecodesPlanarFloatAudioFromGammaReferenceClip()
    {
        var filename = FindGammaAudioReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Audio reference clip is not installed on this machine.");

        var frames = new List<DecodedAudioFrame>();
        using var decoder = new FFmpegAudioDecoder(
            filename,
            TimeSpan.Zero,
            sampleRate: 48_000,
            channelCount: 0,
            CancellationToken.None,
            FindRepositoryRuntime());
        decoder.Decode(frame =>
        {
            frames.Add(frame);
            return frames.Count < 4;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.MediaInfo.ChannelCount, Is.GreaterThan(0));
            Assert.That(decoder.MediaInfo.SampleRate, Is.GreaterThan(0));
            Assert.That(decoder.OutputSampleRate, Is.EqualTo(48_000));
            Assert.That(frames, Has.Count.EqualTo(4));
            Assert.That(frames.Select(frame => frame.Timecode), Is.Ordered);
            Assert.That(frames.All(frame => frame.ChannelCount == decoder.OutputChannelCount), Is.True);
            Assert.That(frames.All(frame => frame.SampleCount > 0), Is.True);
            Assert.That(frames.SelectMany(frame => frame.Samples).Any(sample => sample != 0f), Is.True);
        }
    }

    [Test]
    public void SeekDiscardsAudioPrerollBeforePublishing()
    {
        var filename = FindGammaAudioReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Audio reference clip is not installed on this machine.");

        DecodedAudioFrame? firstFrame = null;
        var seekPosition = TimeSpan.FromSeconds(0.5d);
        using var decoder = new FFmpegAudioDecoder(
            filename,
            seekPosition,
            sampleRate: 48_000,
            channelCount: 2,
            CancellationToken.None,
            FindRepositoryRuntime());
        decoder.Decode(frame =>
        {
            firstFrame = frame;
            return false;
        });

        Assert.That(firstFrame, Is.Not.Null);
        Assert.That(firstFrame!.Timecode,
            Is.GreaterThanOrEqualTo(seekPosition - TimeSpan.FromTicks(1)));
    }

    private static string? FindGammaAudioReferenceClip()
    {
        const string vvvvRoot = @"C:\Program Files\vvvv";
        if (!Directory.Exists(vvvvRoot))
            return null;
        return Directory
            .EnumerateDirectories(vvvvRoot, "vvvv_gamma_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "packs", "VL.Audio", "help", "vvvv.mp3"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindRepositoryRuntime()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                var candidate = Path.Combine(directory.FullName, "runtimes", "win-x64", "native");
                return File.Exists(Path.Combine(candidate, "avcodec-62.dll")) ? candidate : null;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
