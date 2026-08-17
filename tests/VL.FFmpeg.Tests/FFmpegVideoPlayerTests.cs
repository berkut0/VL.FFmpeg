using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Reactive.Linq;
using VL.Core;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Nodes;
using VL.Lib.Animation;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed class FFmpegVideoPlayerTests
{
    [Test]
    public void PublicNodeSurfaceContainsOnlyImplementedTransportControls()
    {
        var update = typeof(FFmpegVideoPlayer).GetMethod(nameof(FFmpegVideoPlayer.Update));
        var parameters = update!.GetParameters().Select(parameter => parameter.Name).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(update.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(parameters, Does.Contain("videoSource"));
            Assert.That(parameters, Does.Contain("filename"));
            Assert.That(parameters, Does.Contain("play"));
            Assert.That(parameters, Does.Contain("loop"));
            Assert.That(parameters, Does.Contain("seekTime"));
            Assert.That(parameters, Does.Contain("seek"));
            Assert.That(parameters, Does.Contain("decodeMode"));
            Assert.That(parameters, Does.Contain("onEnd"));
            Assert.That(parameters, Does.Not.Contain("volume"));
            Assert.That(parameters, Does.Not.Contain("hardwareDecode"));
            Assert.That(parameters, Does.Not.Contain("audioTrack"));
            Assert.That(parameters, Does.Not.Contain("audioAvailable"));
        }
    }

    [Test]
    public void OnEndIsARisingEdge()
    {
        var factory = new TestSessionFactory();
        using var source = new FFmpegVideoPlayer(factory);
        var session = ((IVideoSource2)source).Start(CreateContext());

        source.PublishStatus(session!, PlaybackStatus.Idle with
        {
            Phase = FFmpegPlaybackPhase.Ended,
            IsEnded = true
        });

        var first = UpdateAndGetOnEnd(source);
        var held = UpdateAndGetOnEnd(source);
        source.PublishStatus(session!, PlaybackStatus.Idle);
        var cleared = UpdateAndGetOnEnd(source);
        source.PublishStatus(session!, PlaybackStatus.Idle with
        {
            Phase = FFmpegPlaybackPhase.Ended,
            IsEnded = true
        });
        var second = UpdateAndGetOnEnd(source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.True);
            Assert.That(held, Is.False);
            Assert.That(cleared, Is.False);
            Assert.That(second, Is.True);
        }
    }

    [Test]
    public void VideoSourceSessionReturnsDecodedCpuFrame()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        var runtimePath = FindRepositoryRuntime();
        var previousRuntimePath = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        if (runtimePath is not null)
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", runtimePath);

        try
        {
            using var source = new FFmpegVideoPlayer();
            Update(source, filename!, play: true);
            var clock = new TestFrameClock { Time = 0.25d };
            using var session = ((IVideoSource2)source).Start(
                new VideoPlaybackContext(clock, NullLogger.Instance));

            IResourceProvider<VideoFrame>? provider = null;
            for (var attempt = 0; attempt < 100 && provider is null; attempt++)
            {
                provider = session!.GrabVideoFrame();
                if (provider is null)
                    Thread.Sleep(10);
            }

            Assert.That(provider, Is.Not.Null);
            using var handle = provider!.GetHandle();
            var frame = handle.Resource;
            var hasMemory = frame.TryGetMemory(out var memory);

            Update(
                source,
                filename!,
                play: true,
                out var position,
                out var duration,
                out var phase,
                out var decodePath);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hasMemory, Is.True);
                Assert.That(frame.Width, Is.GreaterThan(0));
                Assert.That(frame.Height, Is.GreaterThan(0));
                Assert.That(memory.Length, Is.EqualTo(frame.Width * frame.Height * 4));
                Assert.That(duration, Is.GreaterThan(0d));
                Assert.That(position, Is.GreaterThanOrEqualTo(0d));
                Assert.That(phase, Is.EqualTo(FFmpegPlaybackPhase.Playing));
                Assert.That(decodePath, Is.EqualTo(FFmpegDecodePath.Software));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", previousRuntimePath);
        }
    }

    [Test]
    public void HardwareModeWithoutGpuContextPublishesFault()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        var runtimePath = FindRepositoryRuntime();
        var previousRuntimePath = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        if (runtimePath is not null)
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", runtimePath);

        try
        {
            using var source = new FFmpegVideoPlayer();
            source.Update(
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                decodeMode: FFmpegDecodeMode.Hardware);
            using var session = ((IVideoSource2)source).Start(CreateContext());

            var phase = FFmpegPlaybackPhase.Idle;
            var status = string.Empty;
            for (var attempt = 0; attempt < 100 && phase != FFmpegPlaybackPhase.Faulted; attempt++)
            {
                source.Update(
                    out _, out _, out _, out _, out _, out _, out _, out phase, out _, out status,
                    filename: filename,
                    decodeMode: FFmpegDecodeMode.Hardware);
                if (phase != FFmpegPlaybackPhase.Faulted)
                    Thread.Sleep(5);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(phase, Is.EqualTo(FFmpegPlaybackPhase.Faulted));
                Assert.That(status, Does.Contain("Direct3D11"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", previousRuntimePath);
        }
    }

    [Test]
    public void UpdatePublishesChangedOptionsOnlyOnce()
    {
        using var source = new FFmpegVideoPlayer();

        Update(source, seek: false);
        var first = source.Options;

        Update(source, seek: false);
        var second = source.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.Revision, Is.EqualTo(0));
            Assert.That(second.SeekRequestId, Is.EqualTo(0));
        }
    }

    [Test]
    public void SeekUsesRisingEdgeGeneration()
    {
        using var source = new FFmpegVideoPlayer();

        Update(source, seek: false);
        Update(source, seek: true);
        var afterFirstEdge = source.Options;
        Update(source, seek: true);
        var whileHeld = source.Options;
        Update(source, seek: false);
        Update(source, seek: true);
        var afterSecondEdge = source.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirstEdge.SeekRequestId, Is.EqualTo(1));
            Assert.That(whileHeld, Is.SameAs(afterFirstEdge));
            Assert.That(afterSecondEdge.SeekRequestId, Is.EqualTo(2));
        }
    }

    [Test]
    public void DecodeModeChangeCreatesOneNewOptionsRevision()
    {
        using var source = new FFmpegVideoPlayer();

        Update(source, seek: false, decodeMode: FFmpegDecodeMode.Software);
        var software = source.Options;
        Update(source, seek: false, decodeMode: FFmpegDecodeMode.Software);
        var unchanged = source.Options;
        Update(source, seek: false, decodeMode: FFmpegDecodeMode.Hardware);
        var hardware = source.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(software.DecodeMode, Is.EqualTo(FFmpegDecodeMode.Software));
            Assert.That(unchanged, Is.SameAs(software));
            Assert.That(hardware.DecodeMode, Is.EqualTo(FFmpegDecodeMode.Hardware));
            Assert.That(hardware.Revision, Is.EqualTo(software.Revision + 1));
        }
    }

    [Test]
    public void SourceAllowsOneSessionAndSignalsRetryAfterDispose()
    {
        using var source = new FFmpegVideoPlayer();
        var videoSource = (IVideoSource2)source;
        var context = CreateContext();

        var first = videoSource.Start(context);
        var rejected = videoSource.Start(context);
        var beforeDisposeTicket = videoSource.ChangedTicket;
        first!.Dispose();
        var afterDisposeTicket = videoSource.ChangedTicket;
        var replacement = videoSource.Start(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(rejected, Is.Null);
            Assert.That(afterDisposeTicket, Is.EqualTo(beforeDisposeTicket + 1));
            Assert.That(replacement, Is.Not.Null);
        }

        replacement!.Dispose();
    }

    [Test]
    public void DisposedSourceCannotBeStartedAgain()
    {
        var source = new FFmpegVideoPlayer();
        var videoSource = (IVideoSource2)source;
        var context = CreateContext();

        var session = videoSource.Start(context);
        ((IDisposable)source).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session, Is.Not.Null);
            Assert.That(videoSource.Start(context), Is.Null);
            Assert.Throws<ObjectDisposedException>((Action)(() => Update(source, seek: false)));
        }
    }

    private static VideoPlaybackContext CreateContext()
        => new(TestFrameClock.Instance, NullLogger.Instance);

    private static void Update(
        FFmpegVideoPlayer source,
        bool seek,
        FFmpegDecodeMode decodeMode = FFmpegDecodeMode.Auto)
    {
        source.Update(
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            seek: seek,
            decodeMode: decodeMode);
    }

    private static bool UpdateAndGetOnEnd(FFmpegVideoPlayer source)
    {
        source.Update(
            out _,
            out _,
            out _,
            out _,
            out _,
            out var onEnd,
            out _,
            out _,
            out _,
            out _);
        return onEnd;
    }

    private static void Update(FFmpegVideoPlayer source, string filename, bool play)
        => Update(source, filename, play, out _, out _, out _, out _);

    private static void Update(
        FFmpegVideoPlayer source,
        string filename,
        bool play,
        out double position,
        out double duration,
        out FFmpegPlaybackPhase phase,
        out FFmpegDecodePath decodePath)
    {
        source.Update(
            out _,
            out position,
            out duration,
            out _,
            out _,
            out _,
            out _,
            out phase,
            out decodePath,
            out _,
            filename: filename,
            play: play);
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
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "runtimes",
                    "win-x64",
                    "native");
                return File.Exists(Path.Combine(candidate, "avcodec-62.dll"))
                    ? candidate
                    : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class TestFrameClock : IFrameClock
    {
        public static readonly TestFrameClock Instance = new();

        public Time Time { get; init; }

        public double TimeDifference => 0d;

        public IObservable<FrameTimeMessage> GetTicks() => Observable.Never<FrameTimeMessage>();

        public IObservable<FrameFinishedMessage> GetFrameFinished()
            => Observable.Never<FrameFinishedMessage>();
    }

    private sealed class TestSessionFactory : IFFmpegPlayerSessionFactory
    {
        public IVideoPlayer Create(FFmpegVideoPlayer source, VideoPlaybackContext context)
            => new TestVideoPlayer();
    }

    private sealed class TestVideoPlayer : IVideoPlayer
    {
        public IResourceProvider<VideoFrame>? GrabVideoFrame() => null;

        public void Dispose()
        {
        }
    }
}
