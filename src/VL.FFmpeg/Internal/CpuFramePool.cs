namespace VL.FFmpeg.Internal;

internal sealed class CpuFramePool : IDisposable, IResourceCache
{
    private readonly ResourceBudget budget;
    private readonly object _gate = new();
    private readonly List<byte[]> _free = [];
    private bool _disposed;
    public CpuFramePool(ResourceBudget budget) { this.budget = budget; budget.Register(this); }

    public void TrimIdle()
    {
        // Never wait while the caller might hold another cache's lock.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            foreach (var buffer in _free) budget.Release(buffer.Length);
            _free.Clear();
        }
        finally { Monitor.Exit(_gate); }
    }

    public CpuFrameLease? TryRent(int length)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var i = 0; i < _free.Count; i++)
            {
                if (_free[i].Length != length) continue;
                var buffer = _free[i];
                _free.RemoveAt(i);
                return new(this, buffer);
            }
            // Retire unused buffers of an old format before requesting more memory.
            foreach (var buffer in _free) budget.Release(buffer.Length);
            _free.Clear();
            if (!budget.TryReserve(length)) return null;
        }
        // Consumer release only takes the metadata lock, never an allocation-sized critical section.
        try { return new(this, GC.AllocateUninitializedArray<byte>(length)); }
        catch { budget.Release(length); throw; }
    }

    internal void Return(byte[] buffer)
    {
        lock (_gate)
        {
            if (_disposed || _free.Count >= 8) budget.Release(buffer.Length);
            else _free.Add(buffer);
            budget.Changed.Pulse();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            budget.Unregister(this);
            foreach (var buffer in _free) budget.Release(buffer.Length);
            _free.Clear();
        }
    }
}

internal sealed class CpuFrameLease(CpuFramePool owner, byte[] buffer) : IDisposable
{
    private CpuFramePool? _owner = owner;
    public byte[] Buffer { get; } = buffer;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Buffer);
}
