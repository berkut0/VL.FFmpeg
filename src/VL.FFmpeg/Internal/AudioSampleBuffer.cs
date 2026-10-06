using System.Buffers;
using System.Collections.Concurrent;
using CommunityToolkit.HighPerformance;
using VL.FFmpeg.Internal.Decoding;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;

namespace VL.FFmpeg.Internal;

/// <summary>Bounded SPSC sample blocks. The consumer never waits for producer work.</summary>
internal sealed class AudioSampleBuffer : IDisposable
{
    private readonly ConcurrentQueue<AudioBlock> _blocks = new();
    private readonly int _capacity;
    private AudioBlock? _current;
    private readonly ResourceBudget _budget;
    private int _offset;
    private int _count;
    private int _reading;
    private int _disposed;
    public int SampleRate { get; }
    public int ChannelCount { get; }
    public int Count => Math.Max(0, Volatile.Read(ref _count));
    public AsyncPulse Changed { get; } = new();
    public long Underruns { get; private set; }

    public AudioSampleBuffer(int sampleRate, int channelCount, int capacity, ResourceBudget? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _budget = budget ?? ResourceBudget.Cpu;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        _capacity = capacity;
    }

    public bool TryWrite(DecodedAudioFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (frame.SampleRate != SampleRate || frame.ChannelCount != ChannelCount)
            throw new InvalidOperationException("Decoded audio format changed without resetting the buffer.");
        // One producer; a single large decoded block may exceed the target duration.
        if (Count > 0 && frame.SampleCount > _capacity - Count) return false;
        var bytes = checked(frame.Samples.LongLength * sizeof(float));
        if (!_budget.TryReserve(bytes)) return false;
        Interlocked.Add(ref _count, frame.SampleCount);
        _blocks.Enqueue(new AudioBlock(frame, _budget, bytes));
        if (Volatile.Read(ref _disposed) != 0) Drain();
        Changed.Pulse();
        return true;
    }

    public void Write(DecodedAudioFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryWrite(frame)) throw new ResourceUnavailableException();
    }

    public async ValueTask<bool> WriteAsync(DecodedAudioFrame frame, Func<double> mediaTime, CancellationToken token)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            token.ThrowIfCancellationRequested();
            var changed = Changed.Next;
            var budgetChanged = _budget.Changed.Next;
            var remaining = frame.Timecode.TotalSeconds + frame.SampleCount / (double)SampleRate - mediaTime();
            if (remaining <= 0) return false;
            if (TryWrite(frame)) return true;
            await Task.WhenAny(changed, budgetChanged, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(remaining, .001, 1)), token))
                .WaitAsync(token).ConfigureAwait(false);
        }
        return false;
    }

    public IResourceProvider<AudioFrame>? TryRead(int sampleCount, bool interleaved)
    {
        if (sampleCount <= 0 || Count < sampleCount || Volatile.Read(ref _disposed) != 0) return null;
        var block = _current;
        if (block is null && !_blocks.TryPeek(out block)) return null;
        return ReadAt(sampleCount, interleaved,
            block.Frame.Timecode + TimeSpan.FromSeconds(_offset / (double)SampleRate));
    }

    public IResourceProvider<AudioFrame> ReadAt(int sampleCount, bool interleaved, TimeSpan timecode)
    {
        var length = checked(sampleCount * ChannelCount);
        var lease = AudioOutputLease.Rent(length, _budget);
        var samples = lease.Buffer;
        try
        {
        samples.AsSpan(0, length).Clear();
        var written = 0;
        if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
        {
            try
            {
                while (written < sampleCount && Volatile.Read(ref _disposed) == 0)
                {
                    if (_current is null)
                    {
                        if (!_blocks.TryDequeue(out _current)) break;
                        _offset = 0;
                    }
                    var frame = _current.Frame;
                    var target = timecode.TotalSeconds + written / (double)SampleRate;
                    var neededOffset = (int)Math.Clamp(Math.Round((target - frame.Timecode.TotalSeconds) * SampleRate), int.MinValue, int.MaxValue);
                    if (neededOffset > _offset)
                    {
                        var skip = Math.Min(frame.SampleCount, neededOffset) - _offset;
                        _offset += skip;
                        Interlocked.Add(ref _count, -skip);
                    }
                    if (_offset >= frame.SampleCount) { _current.Dispose(); _current = null; continue; }
                    if (neededOffset < _offset)
                    {
                        written += Math.Min(sampleCount - written, _offset - neededOffset);
                        continue;
                    }
                    var count = Math.Min(sampleCount - written, frame.SampleCount - _offset);
                    var stride = frame.Samples.Length / frame.ChannelCount;
                    for (var channel = 0; channel < ChannelCount; channel++)
                    {
                        var source = frame.Samples.AsSpan(channel * stride + frame.SampleOffset + _offset, count);
                        if (interleaved)
                            for (var i = 0; i < count; i++) samples[(written + i) * ChannelCount + channel] = source[i];
                        else source.CopyTo(samples.AsSpan(channel * sampleCount + written, count));
                    }
                    written += count;
                    _offset += count;
                    Interlocked.Add(ref _count, -count);
                    if (_offset == frame.SampleCount) { _current.Dispose(); _current = null; }
                }
                if (written < sampleCount) Underruns++;
            }
            finally
            {
                if (Volatile.Read(ref _disposed) != 0) { _current?.Dispose(); _current = null; }
                Volatile.Write(ref _reading, 0);
                Changed.Pulse();
            }
        }
        var memory = samples.AsMemory(0, length);
        var data = interleaved ? memory.AsMemory2D(sampleCount, ChannelCount) : memory.AsMemory2D(ChannelCount, sampleCount);
        return ResourceProvider.Return(new AudioFrame(data, SampleRate, interleaved, "FFmpeg decoded audio", timecode),
            lease, static value => value.Dispose());
        }
        catch { lease.Dispose(); throw; }
    }

    private void Drain()
    {
        while (_blocks.TryDequeue(out var block))
        {
            Interlocked.Add(ref _count, -block.Frame.SampleCount);
            block.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Drain();
        _current?.Dispose();
        Changed.Pulse();
    }

    private sealed class AudioBlock(DecodedAudioFrame frame, ResourceBudget budget, long bytes) : IDisposable
    {
        private int _released;
        public DecodedAudioFrame Frame { get; } = frame;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) budget.Release(bytes); }
    }

    private sealed class AudioOutputLease(float[] buffer, ResourceBudget budget) : IDisposable
    {
        private float[]? _buffer = buffer;
        public float[] Buffer => _buffer!;
        public static AudioOutputLease Rent(int length, ResourceBudget budget)
        {
            var reserved = checked((long)length * sizeof(float));
            if (!budget.TryReserve(reserved, reclaim: false)) throw new ResourceUnavailableException();
            float[]? data = null;
            try
            {
                data = ArrayPool<float>.Shared.Rent(length);
                var extra = checked(data.LongLength * sizeof(float) - reserved);
                if (!budget.TryReserve(extra, reclaim: false)) throw new ResourceUnavailableException();
                reserved += extra;
                return new AudioOutputLease(data, budget);
            }
            catch
            {
                if (data is not null) ArrayPool<float>.Shared.Return(data);
                budget.Release(reserved);
                throw;
            }
        }
        public void Dispose()
        {
            var data = Interlocked.Exchange(ref _buffer, null);
            if (data is null) return;
            ArrayPool<float>.Shared.Return(data);
            budget.Release(data.LongLength * sizeof(float));
        }
    }
}
