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

public sealed class VideoPlayerTests
{
    [Test]
    public void PublicNodeSurfaceContainsOnlyImplementedTransportControls()
    {
        var update = typeof(VideoPlayer).GetMethod(nameof(VideoPlayer.Update));
        var parameters = update!.GetParameters().Select(parameter => parameter.Name).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(update.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(parameters, Does.Contain("videoSource"));
            Assert.That(parameters, Does.Contain("audioSource"));
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
        using var source = new VideoPlayer(factory);
        var session = ((IVideoSource2)source).Start(CreateContext());

        source.PublishStatus(session!, PlaybackStatus.Idle with
        {
            Phase = PlaybackPhase.Ended,
            IsEnded = true
        });

        var first = UpdateAndGetOnEnd(source);
        var held = UpdateAndGetOnEnd(source);
        source.PublishStatus(session!, PlaybackStatus.Idle);
        var cleared = UpdateAndGetOnEnd(source);
        source.PublishStatus(session!, PlaybackStatus.Idle with
        {
            Phase = PlaybackPhase.Ended,
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
            using var source = new VideoPlayer();
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
                Assert.That(phase, Is.EqualTo(PlaybackPhase.Playing));
                Assert.That(decodePath, Is.EqualTo(DecodePath.Software));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", previousRuntimePath);
        }
    }

    [Test]
    public void AudioSourceReturnsRequestedPlanarFramesWithoutVideoConsumer()
    {
        var filename = FindGammaAudioReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Audio reference clip is not installed on this machine.");

        var runtimePath = FindRepositoryRuntime();
        var previousRuntimePath = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        if (runtimePath is not null)
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", runtimePath);
        try
        {
            using var source = new VideoPlayer();
            source.Update(
                out _,
                out var audioSource,
                out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                play: true);

            IResourceProvider<VL.Lib.Basics.Audio.AudioFrame>? provider = null;
            for (var attempt = 0; attempt < 200 && provider is null; attempt++)
            {
                provider = audioSource.GrabAudioFrame(512, 48_000, 2, false);
                if (provider is null)
                    Thread.Sleep(5);
            }

            Assert.That(provider, Is.Not.Null, ((VideoPlayerSource)audioSource).AudioFault?.ToString());
            using var handle = provider!.GetHandle();
            var frame = handle.Resource;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(frame.SampleRate, Is.EqualTo(48_000));
                Assert.That(frame.ChannelCount, Is.EqualTo(2));
                Assert.That(frame.SampleCount, Is.EqualTo(512));
                Assert.That(frame.IsPlanar, Is.True);
                Assert.That(frame.GetChannel(0).ToArray().Any(sample => sample != 0f), Is.True);
            }

            source.Update(
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                play: false);
            Assert.That(audioSource.GrabAudioFrame(512, 48_000, 2, false), Is.Null);

            source.Update(
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                play: true,
                seekTime: 0.5d,
                seek: true);
            IResourceProvider<VL.Lib.Basics.Audio.AudioFrame>? seekProvider = null;
            for (var attempt = 0; attempt < 200 && seekProvider is null; attempt++)
            {
                seekProvider = audioSource.GrabAudioFrame(512, 48_000, 2, false);
                if (seekProvider is null)
                    Thread.Sleep(5);
            }
            Assert.That(seekProvider, Is.Not.Null, ((VideoPlayerSource)audioSource).AudioFault?.ToString());
            using var seekHandle = seekProvider!.GetHandle();
            Assert.That(seekHandle.Resource.Timecode,
                Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(0.5d)));
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
            using var source = new VideoPlayer();
            source.Update(
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                decodeMode: DecodeMode.Hardware);
            using var session = ((IVideoSource2)source).Start(CreateContext());

            var phase = PlaybackPhase.Idle;
            var status = string.Empty;
            for (var attempt = 0; attempt < 100 && phase != PlaybackPhase.Faulted; attempt++)
            {
                source.Update(
                    out _, out _, out _, out _, out _, out _, out _, out _, out phase, out _, out status,
                    filename: filename,
                    decodeMode: DecodeMode.Hardware);
                if (phase != PlaybackPhase.Faulted)
                    Thread.Sleep(5);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(phase, Is.EqualTo(PlaybackPhase.Faulted));
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
        using var source = new VideoPlayer();

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
        using var source = new VideoPlayer();

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
        using var source = new VideoPlayer();

        Update(source, seek: false, decodeMode: DecodeMode.Software);
        var software = source.Options;
        Update(source, seek: false, decodeMode: DecodeMode.Software);
        var unchanged = source.Options;
        Update(source, seek: false, decodeMode: DecodeMode.Hardware);
        var hardware = source.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(software.DecodeMode, Is.EqualTo(DecodeMode.Software));
            Assert.That(unchanged, Is.SameAs(software));
            Assert.That(hardware.DecodeMode, Is.EqualTo(DecodeMode.Hardware));
            Assert.That(hardware.Revision, Is.EqualTo(software.Revision + 1));
        }
    }

    [Test]
    public void SourceAllowsOneSessionAndSignalsRetryAfterDispose()
    {
        using var source = new VideoPlayer();
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
        var source = new VideoPlayer();
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
        VideoPlayer source,
        bool seek,
        DecodeMode decodeMode = DecodeMode.Auto)
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
            out _,
            seek: seek,
            decodeMode: decodeMode);
    }

    private static bool UpdateAndGetOnEnd(VideoPlayer source)
    {
        source.Update(
            out _,
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

    private static void Update(VideoPlayer source, string filename, bool play)
        => Update(source, filename, play, out _, out _, out _, out _);

    private static void Update(
        VideoPlayer source,
        string filename,
        bool play,
        out double position,
        out double duration,
        out PlaybackPhase phase,
        out DecodePath decodePath)
    {
        source.Update(
            out _,
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
        public IVideoPlayer Create(VideoPlayerSource source, VideoPlaybackContext context)
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
