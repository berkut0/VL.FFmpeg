using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using VL.FFmpeg.Internal.Decoding;

namespace VL.FFmpeg.Tests.Live;

public sealed class NetworkInputTests
{
    [Test]
    public async Task StalledHttpOpenHonorsDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live";
        var options = new MediaInputOptions(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        var elapsed = Stopwatch.StartNew();
        var open = Task.Run(() =>
        {
            using var input = new FFmpegDemuxContext(url, CancellationToken.None, MediaFixtures.Runtime, options);
        });
        using var client = await listener.AcceptTcpClientAsync();
        var error = await Capture(open);
        Assert.That(error, Is.TypeOf<TimeoutException>());
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public async Task CancelledHttpOpenStopsBeforeItsDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live";
        var open = Task.Run(() =>
        {
            using var input = new FFmpegDemuxContext(url, cancel.Token, MediaFixtures.Runtime,
                new MediaInputOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        });
        using var client = await listener.AcceptTcpClientAsync();
        cancel.Cancel();
        Assert.That(await Capture(open), Is.InstanceOf<OperationCanceledException>());
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(4)); return null; }
        catch (Exception e) { return e; }
    }
}
