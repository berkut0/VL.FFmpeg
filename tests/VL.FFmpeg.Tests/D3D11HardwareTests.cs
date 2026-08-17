using System.Runtime.InteropServices;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.Core;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Nodes;
using VL.Lib.Animation;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests;

public sealed unsafe class D3D11HardwareTests
{
    [Test]
    public void HardwareModeRequiresAD3D11Consumer()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        Assert.Throws<FFmpegHardwareException>((Action)(() =>
        {
            using var _ = new FFmpegVideoDecoder(
                filename,
                TimeSpan.Zero,
                CancellationToken.None,
                FindRepositoryRuntime(),
                FFmpegDecodeMode.Hardware,
                graphicsDevice: 0,
                graphicsDeviceType: GraphicsDeviceType.None);
        }));
    }

    [Test]
    [Platform("Win")]
    public void D3D11VaReturnsATextureBackedFrame()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        var createResult = D3D11CreateDevice(
            0,
            driverType: 1,
            software: 0,
            flags: 0x20,
            featureLevels: 0,
            featureLevelCount: 0,
            sdkVersion: 7,
            out var device,
            out _,
            out var immediateContext);
        if (createResult < 0 || device == 0)
            Assert.Ignore($"No hardware D3D11 device is available (HRESULT 0x{createResult:X8}).");

        IResourceProvider<VideoFrame>? firstProvider = null;
        IResourceProvider<VideoFrame>? secondProvider = null;
        try
        {
            using (var decoder = new FFmpegVideoDecoder(
                       filename,
                       TimeSpan.Zero,
                       CancellationToken.None,
                       FindRepositoryRuntime(),
                       FFmpegDecodeMode.Hardware,
                       graphicsDevice: device,
                       graphicsDeviceType: GraphicsDeviceType.Direct3D11,
                       usesLinearColorspace: false))
            {
                var decodedFrames = new List<DecodedVideoFrame>();
                decoder.Decode(frame =>
                {
                    decodedFrames.Add(frame);
                    return decodedFrames.Count < 2;
                });

                Assert.That(decodedFrames, Has.Count.EqualTo(2));
                firstProvider = decodedFrames[0].CreateProvider();
                secondProvider = decodedFrames[1].CreateProvider();
                foreach (var decodedFrame in decodedFrames)
                    decodedFrame.Dispose();
            }

            using var firstHandle = firstProvider!.GetHandle();
            using var secondHandle = secondProvider!.GetHandle();
            var hasTexture = firstHandle.Resource.TryGetTexture(out var texture);
            var hasSecondTexture = secondHandle.Resource.TryGetTexture(out var secondTexture);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hasTexture, Is.True);
                Assert.That(hasSecondTexture, Is.True);
                Assert.That(texture.NativePointer, Is.Not.EqualTo(nint.Zero));
                Assert.That(secondTexture.NativePointer, Is.Not.EqualTo(texture.NativePointer));
                Assert.That(texture.Width, Is.GreaterThan(0));
                Assert.That(texture.Height, Is.GreaterThan(0));
                Assert.That(firstHandle.Resource.TryGetMemory(out _), Is.False);
            }
        }
        finally
        {
            if (immediateContext != 0)
                Marshal.Release(immediateContext);
            if (device != 0)
                Marshal.Release(device);
        }
    }

    [Test]
    [Platform("Win")]
    public void PlayerSessionDeliversGpuFrameThroughResourceProvider()
    {
        var filename = FindGammaReferenceClip();
        if (filename is null)
            Assert.Ignore("The Gamma VL.Video reference clip is not installed on this machine.");

        var createResult = D3D11CreateDevice(
            0,
            driverType: 1,
            software: 0,
            flags: 0x20,
            featureLevels: 0,
            featureLevelCount: 0,
            sdkVersion: 7,
            out var device,
            out _,
            out var immediateContext);
        if (createResult < 0 || device == 0)
            Assert.Ignore($"No hardware D3D11 device is available (HRESULT 0x{createResult:X8}).");

        var previousRuntimePath = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        var runtimePath = FindRepositoryRuntime();
        if (runtimePath is not null)
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", runtimePath);

        IResourceHandle<VideoFrame>? handle = null;
        try
        {
            using var source = new FFmpegVideoPlayer();
            source.Update(
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                filename: filename,
                decodeMode: FFmpegDecodeMode.Auto);
            var clock = new TestFrameClock { Time = 0.25d };
            var playbackContext = new VideoPlaybackContext(
                clock,
                NullLogger.Instance,
                () => device,
                GraphicsDeviceType.Direct3D11,
                usesLinearColorspace: false);

            FFmpegDecodePath path;
            string status;
            using (var session = ((IVideoSource2)source).Start(playbackContext))
            {
                IResourceProvider<VideoFrame>? provider = null;
                for (var attempt = 0; attempt < 200 && provider is null; attempt++)
                {
                    provider = session!.GrabVideoFrame();
                    if (provider is null)
                        Thread.Sleep(5);
                }

                Assert.That(provider, Is.Not.Null);
                handle = provider!.GetHandle();
                source.Update(
                    out _, out _, out _, out _, out _, out _, out _, out _, out path, out status,
                    filename: filename,
                    decodeMode: FFmpegDecodeMode.Auto);
            }

            var hasTexture = handle!.Resource.TryGetTexture(out var texture);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hasTexture, Is.True);
                Assert.That(texture.NativePointer, Is.Not.EqualTo(nint.Zero));
                Assert.That(path, Is.EqualTo(FFmpegDecodePath.D3D11VaGpuTexture));
                Assert.That(status, Does.Contain("D3D11VA"));
            }
        }
        finally
        {
            handle?.Dispose();
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", previousRuntimePath);
            if (immediateContext != 0)
                Marshal.Release(immediateContext);
            if (device != 0)
                Marshal.Release(device);
        }
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
                var candidate = Path.Combine(directory.FullName, "runtimes", "win-x64", "native");
                return File.Exists(Path.Combine(candidate, "avcodec-62.dll")) ? candidate : null;
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
}
