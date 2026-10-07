using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VL.FFmpeg.Tests.Live;

/// <summary>Deterministic endless Y4M source; no FFmpeg executable or external server.</summary>
internal sealed class HttpLiveServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private int _connections;
    public string Url { get; }
    public int Connections => Volatile.Read(ref _connections);
    public bool Stall;
    public int FrameLimit = int.MaxValue;
    public int ResponseCode = 200;

    public HttpLiveServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live";
        _accept = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _clients.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
            Interlocked.Increment(ref _connections);
            if (ResponseCode != 200)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {ResponseCode} Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                return;
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\nYUV4MPEG2 W16 H16 F25:1 Ip A1:1 C420jpeg\n"u8.ToArray(), _stop.Token);
            var frame = new byte[6 + 16 * 16 * 3 / 2];
            "FRAME\n"u8.CopyTo(frame);
            Array.Fill(frame, (byte)128, 6, frame.Length - 6);
            for (var count = 0; count < FrameLimit && !_stop.IsCancellationRequested; count++)
            {
                while (Volatile.Read(ref Stall)) await Task.Delay(10, _stop.Token);
                await stream.WriteAsync(frame, _stop.Token);
                await Task.Delay(40, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _accept.GetAwaiter().GetResult();
        _listener.Stop();
        Task.WhenAll(_clients).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
