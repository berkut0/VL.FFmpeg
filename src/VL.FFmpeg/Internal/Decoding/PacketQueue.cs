using System.Threading.Channels;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class NativePacket : IDisposable
{
    private AVPacket* _packet;
    private bool _reserved;
    public AVPacket* Pointer => _packet;
    public int StreamIndex => _packet->stream_index;
    public int Size { get; }
    public bool Discontinuity { get; set; }
    public NativePacket(AVPacket* packet) { _packet = packet; Size = Math.Max(0, packet->size); }
    public bool TryReserve() => _reserved || (_reserved = ResourceBudget.Cpu.TryReserve(Size));
    public void Dispose()
    {
        var packet = _packet;
        if (packet is null) return;
        _packet = null;
        ffmpeg.av_packet_free(&packet);
        if (_reserved) ResourceBudget.Cpu.Release(Size);
    }
}

internal sealed class PacketQueue : IDisposable
{
    private const long ByteCapacity = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<NativePacket> _packets = new();
    private long _bytes;
    private bool _complete;
    public AsyncPulse Changed { get; } = new();

    public bool TryWrite(NativePacket packet)
    {
        lock (_gate)
        {
            if (_complete || _packets.Count >= 8 || (_packets.Count > 0 && packet.Size > ByteCapacity - _bytes)) return false;
            if (!packet.TryReserve()) return false;
            _packets.Enqueue(packet);
            _bytes += packet.Size;
        }
        Changed.Pulse();
        return true;
    }
    public async Task<NativePacket?> Read(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var changed = Changed.Next;
            lock (_gate)
            {
                if (_packets.TryDequeue(out var packet))
                {
                    _bytes -= packet.Size;
                    Changed.Pulse();
                    return packet;
                }
                if (_complete) return null;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            while (_packets.TryDequeue(out var packet)) packet.Dispose();
            _bytes = 0;
        }
        Changed.Pulse();
    }
    public void Complete() { lock (_gate) _complete = true; Changed.Pulse(); }
    public void Dispose() { Complete(); Clear(); }
}
