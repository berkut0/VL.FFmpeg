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
    private readonly H264RtpFixture? _h264;
    public string Url { get; }
    public int Connections;
    public int TimestampOffset = 0;
    public bool RejectSeek = false;
    public bool RejectSetup = false;
    public int PlayRequests;
    public int DisconnectAfterTicks = 0;
    public int DropEveryVideoPacket = 0;
    public bool ReorderVideoFragments = false;
    public string? StallMethod = null;
    public bool RequireAuthorization = false;

    public RtspLiveServer(bool h264 = false)
    {
        _h264 = h264 ? new H264RtpFixture() : null;
        _vp8 = h264 ? [] : ReadKeyframe();
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
                if (method == StallMethod) await Task.Delay(Timeout.Infinite, lifetime.Token);
                var sequence = lines.First(line => line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                if (RequireAuthorization && !lines.Contains("Authorization: Basic " + Convert.ToBase64String("demo:password"u8)))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 401 Unauthorized\r\nCSeq: {sequence}\r\nWWW-Authenticate: Basic realm=\"Live test\"\r\n\r\n"), lifetime.Token);
                    continue;
                }
                var headers = $"RTSP/1.0 200 OK\r\nCSeq: {sequence}\r\nSession: test-session\r\n";
                if (method == "PLAY") Interlocked.Increment(ref PlayRequests);
                if ((method == "PLAY" && sender is not null && RejectSeek) || (method == "SETUP" && RejectSetup))
                {
                    await write.WaitAsync(lifetime.Token);
                    try { await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {(method == "SETUP" ? "403 Forbidden" : "455 Method Not Valid in This State")}\r\nCSeq: {sequence}\r\n\r\n"), lifetime.Token); }
                    finally { write.Release(); }
                    continue;
                }
                var body = "";
                if (method == "OPTIONS") headers += "Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n";
                if (method == "DESCRIBE")
                {
                    var videoDescription = _h264 is null ? "a=rtpmap:96 VP8/90000\r\n"
                        : $"a=rtpmap:96 H264/90000\r\na=fmtp:96 packetization-mode=1;sprop-parameter-sets={_h264.ParameterSets}\r\n";
                    body = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Live test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\na=control:*\r\n"
                        + "m=video 0 RTP/AVP 96\r\n" + videoDescription + "a=control:trackID=0\r\n"
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
                    sender = SendMedia(stream, udp, routes, write, client, lifetime.Token);
                if (method == "TEARDOWN") break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            lifetime.Cancel();
            if (sender is not null)
                try { await sender; } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private async Task SendMedia(NetworkStream stream, UdpClient udp,
        Dictionary<int, (bool Tcp, int Destination)> routes, SemaphoreSlim write, TcpClient client, CancellationToken token)
    {
        ushort videoSequence = 0, audioSequence = 0;
        var audio = new byte[960 * 2];
        for (var i = 0; i < 960; i++)
            BinaryPrimitives.WriteInt16BigEndian(audio.AsSpan(i * 2), (short)(Math.Sin(i * Math.PI / 24) * 10000));
        byte[] video = [0x10, .. _vp8]; // RFC 7741: start of first partition, complete keyframe.
        for (uint tick = 0; ; tick++)
        {
            token.ThrowIfCancellationRequested();
            if (Connections == 1 && DisconnectAfterTicks > 0 && tick >= DisconnectAfterTicks)
            { client.Close(); return; }
            if (tick % 2 == 0 && routes.ContainsKey(0))
            {
                var timestamp = unchecked(tick * 1800 + (uint)TimestampOffset);
                if (_h264 is not null)
                {
                    var frameIndex = tick / 2;
                    var frame = _h264.Frames[(int)(frameIndex % _h264.Frames.Count)];
                    timestamp = unchecked(frame.Timestamp + frameIndex / (uint)_h264.Frames.Count * _h264.CycleTicks + (uint)TimestampOffset);
                    for (var i = 0; i < frame.Payloads.Length; i++)
                        await Send(0, 96, videoSequence++, timestamp, frame.Payloads[i], marker: i == frame.Payloads.Length - 1);
                }
                else if (ReorderVideoFragments && tick > 0)
                {
                    var middle = video.Length / 2;
                    var sequence = videoSequence;
                    videoSequence += 2;
                    await Send(0, 96, (ushort)(sequence + 1), timestamp, [0, .. video[middle..]]);
                    await Send(0, 96, sequence, timestamp, video[..middle], marker: false);
                }
                else await Send(0, 96, videoSequence++, timestamp, video);
            }
            if (routes.ContainsKey(1))
                await Send(1, 97, audioSequence++, unchecked(tick * 960 + (uint)(TimestampOffset * 48000L / 90000)), audio);
            await Task.Delay(20, token);
        }

        async Task Send(int track, byte type, ushort sequence, uint timestamp, byte[] payload, bool marker = true)
        {
            if (track == 0 && DropEveryVideoPacket > 0 && sequence % DropEveryVideoPacket == DropEveryVideoPacket - 1) return;
            var packet = new byte[12 + payload.Length];
            packet[0] = 0x80; packet[1] = (byte)(type | (marker ? 0x80 : 0));
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
