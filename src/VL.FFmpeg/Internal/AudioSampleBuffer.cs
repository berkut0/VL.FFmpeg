using System.Buffers;
using CommunityToolkit.HighPerformance;
using VL.FFmpeg.Internal.Decoding;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;

namespace VL.FFmpeg.Internal;

internal sealed class AudioSampleBuffer : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly float[][] _channels;
    private readonly int _capacity;
    private int _readIndex;
    private int _count;
    private TimeSpan _headTimecode;
    private bool _disposed;

    public AudioSampleBuffer(int sampleRate, int channelCount, int capacity)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        SampleRate = sampleRate;
        ChannelCount = channelCount;
        _capacity = capacity;
        _channels = new float[channelCount][];
        for (var channel = 0; channel < channelCount; channel++)
            _channels[channel] = new float[capacity];
    }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public void Write(DecodedAudioFrame frame, CancellationToken cancellationToken)
    {
        if (frame.SampleRate != SampleRate || frame.ChannelCount != ChannelCount)
            throw new InvalidOperationException("Decoded audio format changed without resetting the buffer.");
        if (frame.SampleCount > _capacity)
            throw new InvalidOperationException("Decoded audio frame exceeds the bounded buffer capacity.");

        lock (_syncRoot)
        {
            while (!_disposed && _capacity - _count < frame.SampleCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_syncRoot, millisecondsTimeout: 5);
            }
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_count == 0)
                _headTimecode = frame.Timecode;
            var writeIndex = (_readIndex + _count) % _capacity;
            var sourceStride = frame.Samples.Length / frame.ChannelCount;
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                var sourceOffset = channel * sourceStride + frame.SampleOffset;
                CopyToRing(
                    frame.Samples.AsSpan(sourceOffset, frame.SampleCount),
                    _channels[channel],
                    writeIndex);
            }
            _count += frame.SampleCount;
            Monitor.PulseAll(_syncRoot);
        }
    }

    public IResourceProvider<AudioFrame>? TryRead(int sampleCount, bool interleaved)
    {
        if (sampleCount <= 0)
            return null;

        float[] rented;
        TimeSpan timecode;
        lock (_syncRoot)
        {
            if (_disposed || _count < sampleCount)
                return null;

            rented = ArrayPool<float>.Shared.Rent(checked(sampleCount * ChannelCount));
            timecode = _headTimecode;
            if (interleaved)
                ReadInterleaved(rented, sampleCount);
            else
                ReadPlanar(rented, sampleCount);

            _readIndex = (_readIndex + sampleCount) % _capacity;
            _count -= sampleCount;
            _headTimecode += TimeSpan.FromSeconds(sampleCount / (double)SampleRate);
            Monitor.PulseAll(_syncRoot);
        }

        var length = checked(sampleCount * ChannelCount);
        var memory = rented.AsMemory(0, length);
        var data = interleaved
            ? memory.AsMemory2D(sampleCount, ChannelCount)
            : memory.AsMemory2D(ChannelCount, sampleCount);
        var frame = new AudioFrame(
            data,
            SampleRate,
            IsInterleaved: interleaved,
            Metadata: "FFmpeg decoded audio",
            Timecode: timecode);
        return ResourceProvider.Return(
            frame,
            rented,
            static buffer => ArrayPool<float>.Shared.Return(buffer));
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            _count = 0;
            Monitor.PulseAll(_syncRoot);
        }
    }

    private static void CopyToRing(ReadOnlySpan<float> source, float[] destination, int index)
    {
        var firstCount = Math.Min(source.Length, destination.Length - index);
        source[..firstCount].CopyTo(destination.AsSpan(index));
        source[firstCount..].CopyTo(destination);
    }

    private void ReadPlanar(float[] destination, int sampleCount)
    {
        for (var channel = 0; channel < ChannelCount; channel++)
            CopyFromRing(_channels[channel], _readIndex, destination.AsSpan(channel * sampleCount, sampleCount));
    }

    private void ReadInterleaved(float[] destination, int sampleCount)
    {
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var sourceIndex = (_readIndex + sample) % _capacity;
            for (var channel = 0; channel < ChannelCount; channel++)
                destination[sample * ChannelCount + channel] = _channels[channel][sourceIndex];
        }
    }

    private static void CopyFromRing(float[] source, int index, Span<float> destination)
    {
        var firstCount = Math.Min(destination.Length, source.Length - index);
        source.AsSpan(index, firstCount).CopyTo(destination);
        source.AsSpan(0, destination.Length - firstCount).CopyTo(destination[firstCount..]);
    }
}
