using System.Collections.Concurrent;

namespace VL.FFmpeg.Internal;

/// <summary>Admission happens asynchronously before synchronous codec/conversion work starts.</summary>
internal static class PlaybackWork
{
    private static readonly int Capacity = Math.Max(1, Environment.ProcessorCount - 2);
    private static readonly object Gate = new();
    private static readonly Queue<Waiter> Waiting = new();
    private static int _available = Capacity;
    private static int _decoders;
    private static readonly ConcurrentDictionary<nint, SemaphoreSlim> Devices = new();

    public static int RegisterDecoder() => Math.Clamp(Capacity / Interlocked.Increment(ref _decoders), 1, 8);
    public static void UnregisterDecoder() => Interlocked.Decrement(ref _decoders);

    public static async Task<T> Run<T>(Func<T> operation, CancellationToken token, int weight = 1, nint device = 0)
    {
        weight = Math.Clamp(weight, 1, Capacity);
        var waiter = new Waiter(weight);
        lock (Gate) { Waiting.Enqueue(waiter); Admit(); }
        // A cancelled reservation is still admitted/released: it cannot strand the FIFO.
        using var registration = token.Register(() => { lock (Gate) { waiter.Cancelled = true; Admit(); } });
        await waiter.Ready.Task.ConfigureAwait(false);
        if (waiter.Cancelled) throw new OperationCanceledException(token);
        SemaphoreSlim? gpu = null;
        var acquiredGpu = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (device != 0)
            {
                gpu = Devices.GetOrAdd(device, static _ => new(1, 1));
                await gpu.WaitAsync(token).ConfigureAwait(false);
                acquiredGpu = true;
            }
            return await Task.Run(() => { token.ThrowIfCancellationRequested(); return operation(); }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (acquiredGpu) gpu!.Release();
            lock (Gate) { _available += weight; Admit(); }
        }
    }

    private static void Admit()
    {
        while (Waiting.TryPeek(out var waiter))
        {
            if (waiter.Cancelled)
            {
                Waiting.Dequeue();
                waiter.Ready.TrySetResult();
                continue;
            }
            if (waiter.Weight > _available) return;
            Waiting.Dequeue();
            _available -= waiter.Weight;
            waiter.Admitted = true;
            waiter.Ready.TrySetResult();
        }
    }
    private sealed class Waiter(int weight)
    {
        public int Weight { get; } = weight;
        public bool Admitted;
        private bool _cancelled;
        public bool Cancelled { get => _cancelled; set { if (!Admitted) _cancelled = value; } }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
