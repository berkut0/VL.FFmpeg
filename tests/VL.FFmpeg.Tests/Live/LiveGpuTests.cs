using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Internal.Live;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests.Live;

public sealed unsafe class LiveGpuTests
{
    [Test]
    [Platform("Win")]
    public void RunRetainsItsDeviceAfterConsumerBindingIsRetired()
    {
        nint device = 0, context = 0;
        try
        {
            var result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (result < 0 || device == 0) Assert.Ignore("No hardware D3D11 device available.");
            var playback = new VideoPlaybackContext(new ManualClock(), NullLogger.Instance,
                () => device, GraphicsDeviceType.Direct3D11);
            using var attachment = new LiveSession.Binding(playback);
            using var run = attachment.Retain();
            // Retire both the consumer's binding and the test's own native references.
            attachment.Dispose();
            Marshal.Release(context); context = 0;
            Marshal.Release(device); device = 0;
            var unknown = new Guid("00000000-0000-0000-C000-000000000046");
            Assert.That(Marshal.QueryInterface(run.Device, ref unknown, out var retained), Is.Zero);
            Marshal.Release(retained);
        }
        finally
        {
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    public void RetainedTextureSurvivesReconnectAndShutdown(bool linear)
    {
        var previous = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
        nint device = 0, context = 0;
        try
        {
            var result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (result < 0 || device == 0) Assert.Ignore("No hardware D3D11 device available.");
            using var server = new HttpLiveServer();
            using var source = new LiveVideoPlayer();
            LivePlayerTests.Update(source, server.Url);
            var clock = new ManualClock();
            using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance,
                () => device, GraphicsDeviceType.Direct3D11, linear))!;
            using var first = LivePlayerTests.WaitFrame(player, clock);
            Assert.That(first.Resource.TryGetTexture(out var texture), Is.True);
            LivePlayerTests.Update(source, server.Url, reconnect: true);
            using var second = LivePlayerTests.WaitFrame(player, clock);
            Assert.That(second.Resource.TryGetTexture(out var next), Is.True);
            Assert.That(next.NativePointer, Is.Not.EqualTo(texture.NativePointer));
            ((IDisposable)source).Dispose();
            var description = D3D11Interop.GetDescription((ID3D11Texture2D*)texture.NativePointer);
            Assert.That(description.Width, Is.EqualTo(16));
            Assert.That(description.Height, Is.EqualTo(16));
        }
        finally
        {
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
            Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", previous);
        }
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device,
        out uint selectedFeatureLevel, out nint immediateContext);
}
