using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.FFmpeg.Nodes;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Tests.Live;

public sealed class LivePlayerTests
{
    private string? _runtime;
    [SetUp] public void Setup()
    {
        _runtime = Environment.GetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH");
        Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", MediaFixtures.Runtime);
    }
    [TearDown] public void Cleanup() => Environment.SetEnvironmentVariable("VL_FFMPEG_NATIVE_PATH", _runtime);

    [Test]
    public void LiveNodeHasItsOwnCompactTransportContract()
    {
        var attribute = typeof(LiveVideoPlayer).GetCustomAttributesData().Single(a => a.AttributeType.Name == "ProcessNodeAttribute");
        Assert.That(attribute.NamedArguments.Single(a => a.MemberName == "Name").TypedValue.Value, Is.EqualTo("VideoPlayer (Live)"));
        var inputs = typeof(LiveVideoPlayer).GetMethod("Update")!.GetParameters().Where(p => !p.IsOut).Select(p => p.Name).ToArray();
        Assert.That(inputs, Is.EqualTo(new[] { "url", "enabled", "reconnect", "decodeMode", "transport" }));
    }

    [Test]
    public void HttpFramesSurviveReconnectAndSourceDisposal()
    {
        using var server = new HttpLiveServer();
        var source = new LiveVideoPlayer();
        using var disposeSource = source;
        Update(source, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance));
        using var first = WaitFrame(player!, clock);
        Assert.That(first.Resource.TryGetMemory(out var before), Is.True);
        var pixels = before.ToArray();
        Update(source, server.Url, reconnect: true);
        using var next = WaitFrame(player!, clock);
        Assert.That(server.Connections, Is.EqualTo(2));
        Update(source, server.Url, reconnect: true);
        Assert.That(source.Session.Metrics.Seeks, Is.Zero);
        ((IDisposable)source).Dispose();
        Assert.That(first.Resource.TryGetMemory(out var after), Is.True);
        Assert.That(after.ToArray(), Is.EqualTo(pixels));
    }

    [Test]
    public void DisabledSourceDoesNotOpenAndUnchangedPinsDoNotReconnect()
    {
        using var server = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        Update(source, server.Url, enabled: false);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance));
        Assert.That(source.Session.Metrics.ContainerOpens, Is.Zero);
        Update(source, server.Url);
        using var frame = WaitFrame(player!, clock);
        for (var i = 0; i < 10; i++) Update(source, server.Url);
        using var another = WaitFrame(player!, clock);
        Assert.That(server.Connections, Is.EqualTo(1));
        Update(source, server.Url, enabled: false);
        Assert.That(player!.GrabVideoFrame(), Is.Null);
    }

    internal static void Update(LiveVideoPlayer source, string url, bool enabled = true, bool reconnect = false)
        => source.Update(out _, out _, out _, out _, out _, out _, url, enabled, reconnect);

    [Test]
    public void AuthenticationFailureIsTerminalAndStatusDoesNotExposeUrlSecrets()
    {
        using var server = new HttpLiveServer { ResponseCode = 401 };
        using var source = new LiveVideoPlayer();
        var url = server.Url.Replace("http://", "http://alice:private-password@") + "?key=private-token";
        Update(source, url);
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        Assert.That(SpinWait.SpinUntil(() => source.Session.Status.Phase == LivePlaybackPhase.Faulted, 3000), Is.True);
        Assert.That(server.Connections, Is.EqualTo(1));
        Assert.That(source.Session.Status.Message, Does.Not.Contain("private-").And.Not.Contain("alice"));
    }

    [Test]
    public void AudioOnlyRtspProvidesSamplesWithoutVideoConsumer()
    {
        using var server = new RtspLiveServer();
        using var source = new LiveVideoPlayer();
        source.Update(out _, out var audio, out _, out _, out _, out _, server.Url);
        var watch = Stopwatch.StartNew();
        var audible = false;
        while (!audible && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (audio.GrabAudioFrame(512, 48000, 1, false) is { } provider)
            {
                using var sound = provider.GetHandle();
                audible = sound.Resource.GetChannel(0).ToArray().Any(value => Math.Abs(value) > .01);
            }
            Thread.Sleep(10);
        }
        Assert.That(audible, Is.True, source.Session.Status.Message);
        Assert.That(source.Session.Metrics.Seeks, Is.Zero);
    }

    [Test]
    public void RtspTimestampJumpDoesNotStallPresentationOrReconnect()
    {
        using var server = new RtspLiveServer();
        using var source = new LiveVideoPlayer();
        Update(source, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, clock);
        server.TimestampOffset = 90000 * 50;
        var watch = Stopwatch.StartNew();
        var count = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(1))
        {
            using var frame = WaitFrame(player, clock);
            Assert.That(frame.Resource.Timecode.TotalSeconds - first.Resource.Timecode.TotalSeconds, Is.LessThan(3));
            count++;
        }
        Assert.That(count, Is.GreaterThan(5));
        Assert.That(server.Connections, Is.EqualTo(1));
    }

    [Test]
    public void StrictHardwareFailsWithoutReconnectLoop()
    {
        using var server = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        source.Update(out _, out _, out _, out _, out _, out _, server.Url, decodeMode: DecodeMode.Hardware);
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(new ManualClock(), NullLogger.Instance))!;
        Assert.That(SpinWait.SpinUntil(() => source.Session.Status.Phase == LivePlaybackPhase.Faulted, 3000), Is.True);
        Assert.That(server.Connections, Is.EqualTo(1));
    }

    [Test]
    public void LiveAndFilePlayersReleaseResourcesAfterSlowConsumption()
    {
        var used = VL.FFmpeg.Internal.ResourceBudget.Cpu.Used;
        using var server = new HttpLiveServer();
        using var file = new VL.FFmpeg.Internal.VideoPlayerSource(VL.FFmpeg.Internal.FFmpegPlayerSessionFactory.Instance);
        file.Open(Path.Combine(MediaFixtures.Root, "tests", "VL.FFmpeg.Tests", "TestData", "vp9-alpha.webm"), true);
        file.SetLoop(true);
        var fileClock = new ManualClock();
        using var filePlayer = ((IVideoSource2)file).Start(new VideoPlaybackContext(fileClock, NullLogger.Instance))!;
        var sources = Enumerable.Range(0, 6).Select(_ => new LiveVideoPlayer()).ToArray();
        var players = new List<IVideoPlayer>();
        var handles = new List<IResourceHandle<VideoFrame>>();
        var clocks = sources.Select(_ => new ManualClock()).ToArray();
        try
        {
            for (var i = 0; i < sources.Length; i++)
            {
                Update(sources[i], server.Url);
                players.Add(((IVideoSource2)sources[i]).Start(new VideoPlaybackContext(clocks[i], NullLogger.Instance))!);
            }
            for (var i = 0; i < sources.Length; i++) handles.Add(WaitFrame(players[i], clocks[i]));
            for (var i = 0; i < sources.Length; i++)
            {
                using var recorded = WaitFrame(filePlayer, fileClock);
                clocks[i].Time += 2;
                using var frame = WaitFrame(players[i], clocks[i]);
                Assert.That(sources[i].Session.Metrics.ContainerOpens, Is.EqualTo(1));
                Assert.That(sources[i].Session.Metrics.Seeks, Is.Zero);
            }
        }
        finally
        {
            foreach (var player in players) player.Dispose();
            foreach (var source in sources) ((IDisposable)source).Dispose();
            foreach (var handle in handles) handle.Dispose();
            ((IDisposable)file).Dispose();
        }
        Assert.That(VL.FFmpeg.Internal.ResourceBudget.Cpu.Used, Is.EqualTo(used));
    }

    [Test]
    [Explicit("External HTTPS smoke test; requires network access and trusted system certificates.")]
    public void HttpsSourcePresentsFrameWithCertificateVerification()
    {
        using var source = new LiveVideoPlayer();
        Update(source, "https://media.w3.org/2010/05/sintel/trailer.mp4");
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var frame = WaitFrame(player, clock);
        Assert.That(frame.Resource.Width, Is.GreaterThan(0));
    }

    [TestCase(LiveTransport.Tcp)]
    [TestCase(LiveTransport.Udp)]
    public void RtspVideoAndLateAudioUseOneConnection(LiveTransport transport)
    {
        using var server = new RtspLiveServer();
        using var source = new LiveVideoPlayer();
        source.Update(out _, out var audio, out _, out _, out _, out _, server.Url, transport: transport);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var frame = WaitFrame(player, clock);
        Assert.That(frame.Resource.Width, Is.EqualTo(32));
        foreach (var rate in new[] { 48000, 44100 })
        {
            var watch = Stopwatch.StartNew();
            var start = clock.Time.Seconds;
            var audible = false;
            while (!audible && watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                clock.Time = start + watch.Elapsed.TotalSeconds;
                if (player.GrabVideoFrame() is { } p) { using var h = p.GetHandle(); }
                if (audio.GrabAudioFrame(256, rate, 1, false) is { } provider)
                {
                    using var sound = provider.GetHandle();
                    Assert.That(sound.Resource.SampleRate, Is.EqualTo(rate));
                    audible = sound.Resource.GetChannel(0).ToArray().Any(value => Math.Abs(value) > .01);
                }
                Thread.Sleep(5);
            }
            Assert.That(audible, Is.True, source.Session.Status.Message);
        }
        Assert.That(server.Connections, Is.EqualTo(1));
        Assert.That(source.Session.Metrics.Seeks, Is.Zero);
    }

    [Test]
    public void ConsumerReplacementKeepsConnectionAndDoesNotSeek()
    {
        using var server = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        Update(source, server.Url);
        var clock = new ManualClock();
        var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var held = WaitFrame(player, clock);
        player.Dispose();
        using var next = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var frame = WaitFrame(next, clock);
        Assert.That(server.Connections, Is.EqualTo(1));
        Assert.That(source.Session.Metrics.Seeks, Is.Zero);
    }

    [Test]
    public void FiniteSourceReconnectsAndDisableCancelsRetry()
    {
        using var server = new HttpLiveServer { FrameLimit = 6 };
        using var source = new LiveVideoPlayer();
        Update(source, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        var watch = Stopwatch.StartNew();
        while (server.Connections < 2 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            clock.Time = watch.Elapsed.TotalSeconds;
            if (player.GrabVideoFrame() is { } p) { using var frame = p.GetHandle(); }
            Thread.Sleep(5);
        }
        Assert.That(server.Connections, Is.EqualTo(2), source.Session.Status.Message);
        Update(source, server.Url, enabled: false);
        Assert.That(source.Session.Status.Phase, Is.EqualTo(LivePlaybackPhase.Idle));
    }

    [Test]
    public void DisposeInterruptsStalledReadAndFilledQueues()
    {
        using var server = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        Update(source, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var held = WaitFrame(player, clock);
        server.Stall = true;
        var watch = Stopwatch.StartNew();
        ((IDisposable)source).Dispose();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        Assert.That(held.Resource.TryGetMemory(out _), Is.True);
    }

    [Test]
    public void StalledNativeReadTransitionsToReconnect()
    {
        using var server = new HttpLiveServer();
        using var source = new LiveVideoPlayer();
        Update(source, server.Url);
        var clock = new ManualClock();
        using var player = ((IVideoSource2)source).Start(new VideoPlaybackContext(clock, NullLogger.Instance))!;
        using var first = WaitFrame(player, clock);
        server.Stall = true;
        Assert.That(SpinWait.SpinUntil(() => source.Session.Status.Phase == LivePlaybackPhase.Reconnecting, 7000), Is.True,
            source.Session.Status.Message);
        Update(source, server.Url, enabled: false);
    }

    internal static IResourceHandle<VideoFrame> WaitFrame(IVideoPlayer player, ManualClock clock)
    {
        var watch = Stopwatch.StartNew();
        var start = clock.Time.Seconds;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            clock.Time = start + watch.Elapsed.TotalSeconds;
            if (player.GrabVideoFrame() is { } provider) return provider.GetHandle();
            Thread.Sleep(5);
        }
        throw new TimeoutException("Live source did not present a frame.");
    }
}
