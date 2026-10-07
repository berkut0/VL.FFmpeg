using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Tests.Live;

/// <summary>Loopback RTSP/RTP fixture: VP8 keyframes and L16 audio, TCP or UDP.</summary>
internal sealed class RtspLiveServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly byte[] _vp8;
    public string Url { get; }
    public int Connections;
    public int TimestampOffset = 0;

    public RtspLiveServer()
    {
        _vp8 = ReadKeyframe();
        _listener.Start();
        Url = $"rtsp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live";
        _server = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref Connections);
                await Connection(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Connection(TcpClient client)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using var write = new SemaphoreSlim(1);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var stream = client.GetStream();
        var routes = new Dictionary<int, (bool Tcp, int Destination)>();
        Task? sender = null;
        try
        {
            while (await ReadRequest(stream, lifetime.Token) is { } request)
            {
                var lines = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var method = lines[0].Split(' ')[0];
                var sequence = lines.First(line => line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                var headers = $"RTSP/1.0 200 OK\r\nCSeq: {sequence}\r\nSession: test-session\r\n";
                var body = "";
                if (method == "OPTIONS") headers += "Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n";
                if (method == "DESCRIBE")
                {
                    body = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Live test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\na=control:*\r\n"
                        + "m=video 0 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=control:trackID=0\r\n"
                        + "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 L16/48000/1\r\na=control:trackID=1\r\n";
                    headers += $"Content-Type: application/sdp\r\nContent-Base: {Url}/\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\n";
                }
                if (method == "SETUP")
                {
                    var track = lines[0].Contains("trackID=1") ? 1 : 0;
                    var transport = lines.First(line => line.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase))[10..].Trim();
                    var tcp = transport.Contains("TCP");
                    var match = Regex.Match(transport, tcp ? @"interleaved=(\d+)" : @"client_port=(\d+)");
                    routes[track] = (tcp, int.Parse(match.Groups[1].Value));
                    headers += $"Transport: {transport}";
                    if (!tcp)
                    {
                        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                        headers += $";server_port={port}-{port + 1}";
                    }
                    headers += "\r\n";
                }
                await write.WaitAsync(lifetime.Token);
                try { await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n" + body), lifetime.Token); }
                finally { write.Release(); }
                if (method == "PLAY" && sender is null)
                    sender = SendMedia(stream, udp, routes, write, lifetime.Token);
                if (method == "TEARDOWN") break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        finally
        {
            lifetime.Cancel();
            if (sender is not null)
                try { await sender; } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        }
    }

    private async Task SendMedia(NetworkStream stream, UdpClient udp,
        Dictionary<int, (bool Tcp, int Destination)> routes, SemaphoreSlim write, CancellationToken token)
    {
        ushort videoSequence = 0, audioSequence = 0;
        var audio = new byte[960 * 2];
        for (var i = 0; i < 960; i++)
            BinaryPrimitives.WriteInt16BigEndian(audio.AsSpan(i * 2), (short)(Math.Sin(i * Math.PI / 24) * 10000));
        byte[] video = [0x10, .. _vp8]; // RFC 7741: start of first partition, complete keyframe.
        for (uint tick = 0; ; tick++)
        {
            token.ThrowIfCancellationRequested();
            if (tick % 2 == 0 && routes.ContainsKey(0))
                await Send(0, 96, videoSequence++, unchecked(tick * 1800 + (uint)TimestampOffset), video);
            if (routes.ContainsKey(1))
                await Send(1, 97, audioSequence++, unchecked(tick * 960 + (uint)(TimestampOffset * 48000L / 90000)), audio);
            await Task.Delay(20, token);
        }

        async Task Send(int track, byte type, ushort sequence, uint timestamp, byte[] payload)
        {
            var packet = new byte[12 + payload.Length];
            packet[0] = 0x80; packet[1] = (byte)(type | 0x80);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), (uint)(track + 1));
            payload.CopyTo(packet, 12);
            var route = routes[track];
            if (route.Tcp)
            {
                var header = new byte[] { 0x24, (byte)route.Destination, (byte)(packet.Length >> 8), (byte)packet.Length };
                await write.WaitAsync(token);
                try { await stream.WriteAsync(header, token); await stream.WriteAsync(packet, token); }
                finally { write.Release(); }
            }
            else await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, route.Destination), token);
        }
    }

    private static async Task<string?> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, token) != 0)
        {
            if (bytes.Count == 0 && one[0] == 0x24)
            {
                var header = new byte[3];
                await stream.ReadExactlyAsync(header, token);
                var data = new byte[header[1] * 256 + header[2]];
                await stream.ReadExactlyAsync(data, token); // Ignore client RTCP reports.
                continue;
            }
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
            if (bytes.Count > 16384) throw new IOException("Invalid test RTSP request.");
        }
        return null;
    }

    private static unsafe byte[] ReadKeyframe()
    {
        using var demux = new FFmpegDemuxContext(Path.Combine(MediaFixtures.Root, "tests", "VL.FFmpeg.Tests", "TestData", "vp8-alpha.webm"),
            CancellationToken.None, MediaFixtures.Runtime);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            FFmpegDemuxContext.Check(demux.Read(packet), "read VP8 test keyframe");
            return new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
        }
        finally { ffmpeg.av_packet_free(&packet); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.GetAwaiter().GetResult();
        _listener.Stop();
        _stop.Dispose();
    }
}
