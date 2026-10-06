using System.Collections.Concurrent;

namespace VL.FFmpeg.Internal;

internal interface IResourceCache
{
    void TrimIdle();
}

/// <summary>Accounts owned buffers, including leases retained by consumers.</summary>
internal sealed class ResourceBudget(long capacity)
{
    public static ResourceBudget Cpu { get; } = new(1L << 30);
    private static readonly ConcurrentDictionary<nint, ResourceBudget> Devices = new();
    public static ResourceBudget ForDevice(nint device) => Devices.GetOrAdd(device, static _ => new(1L << 30));
    private long _used;
    private readonly ConcurrentDictionary<IResourceCache, byte> _caches = new();
    public void Register(IResourceCache cache) => _caches.TryAdd(cache, 0);
    public void Unregister(IResourceCache cache) => _caches.TryRemove(cache, out _);
    public AsyncPulse Changed { get; } = new();
    public long Used => Interlocked.Read(ref _used);
    public long Capacity { get; } = capacity;

    public bool TryReserve(long bytes, bool reclaim = true)
    {
        if (bytes < 0 || bytes > Capacity) return false;
        var reclaimed = false;
        while (true)
        {
            var used = Used;
            if (bytes > Capacity - used)
            {
                if (reclaimed || !reclaim) return false;
                reclaimed = true;
                foreach (var cache in _caches.Keys) cache.TrimIdle();
                continue;
            }
            if (Interlocked.CompareExchange(ref _used, used + bytes, used) == used) return true;
        }
    }
    public void Release(long bytes) { Interlocked.Add(ref _used, -bytes); Changed.Pulse(); }
}

internal sealed class ResourceUnavailableException : Exception
{
    public ResourceUnavailableException() : base("Playback buffer budget or texture slots are in use.") { }
}
