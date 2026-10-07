using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace VL.FFmpeg.Tests.Live;

/// <summary>Finite media with observable ranges, interrupted responses and optional untrusted TLS.</summary>
internal sealed class HttpMediaServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private readonly X509Certificate2? _certificate;
    private readonly byte[] _media;
    private int _connections;
    public string Url { get; }
    public int Connections => Volatile.Read(ref _connections);
    public ConcurrentQueue<long> Offsets { get; } = new();
    public bool SupportsRanges = true;
    public bool StallRequests;
    public int DropResponseAfterBytes;
    public string? RedirectTo;
    public int FrameCount { get; }

    public HttpMediaServer(bool tls = false, int frames = 200)
    {
        FrameCount = frames;
        using var data = new MemoryStream();
        data.Write("YUV4MPEG2 W64 H32 F25:1 Ip A1:1 C420jpeg\n"u8);
        var pixels = new byte[64 * 32 * 3 / 2];
        for (var i = 0; i < frames; i++)
        {
            Array.Fill(pixels, (byte)(16 + i % 200), 0, 64 * 32);
            Array.Fill(pixels, (byte)128, 64 * 32, pixels.Length - 64 * 32);
            data.Write("FRAME\n"u8); data.Write(pixels);
        }
        _media = data.ToArray();
        if (tls)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Schannel's server side requires an imported key; Dispose removes the temporary key.
            // This certificate is never added to a trust store.
            _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        }
        _listener.Start();
        Url = $"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/recording.y4m";
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
            using Stream stream = _certificate is null ? client.GetStream() : new SslStream(client.GetStream());
            if (stream is SslStream secure)
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var headers = new List<string>();
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header) headers.Add(header);
            Interlocked.Increment(ref _connections);
            while (Volatile.Read(ref StallRequests)) await Task.Delay(10, _stop.Token);
            if (RedirectTo is { } location)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                return;
            }
            var range = headers.FirstOrDefault(h => h.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            var offset = SupportsRanges && range is not null ? long.Parse(Regex.Match(range, @"bytes=(\d+)-").Groups[1].Value) : 0;
            Offsets.Enqueue(offset);
            if (offset >= _media.Length)
            {
                await stream.WriteAsync("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                return;
            }
            var response = $"HTTP/1.1 {(range is not null && SupportsRanges ? "206 Partial Content" : "200 OK")}\r\n"
                + $"Content-Length: {_media.Length - offset}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n";
            if (SupportsRanges)
            {
                response += "Accept-Ranges: bytes\r\n";
                if (range is not null) response += $"Content-Range: bytes {offset}-{_media.Length - 1}/{_media.Length}\r\n";
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), _stop.Token);
            var drop = Interlocked.Exchange(ref DropResponseAfterBytes, 0);
            var end = drop > 0 ? Math.Min(_media.Length, offset + drop) : _media.Length;
            for (var position = offset; position < end;)
            {
                var count = (int)Math.Min(4096, end - position);
                await stream.WriteAsync(_media.AsMemory((int)position, count), _stop.Token);
                position += count;
                await Task.Delay(1, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or AuthenticationException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _accept.GetAwaiter().GetResult();
        _listener.Stop();
        Task.WhenAll(_clients).GetAwaiter().GetResult();
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
