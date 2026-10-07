using System.Diagnostics;

namespace VL.FFmpeg.Internal;

/// <summary>Worker counters are atomic; text caching is owned by the session's presentation lock.</summary>
internal sealed class PlaybackDiagnostics
{
    private long _containerOpens, _seeks, _conversions, _droppedFrames, _resourceWaits;
    private long _progressFrames, _audioGaps, _readTicks, _decodeTicks, _convertTicks;
    private int _resourceBlocked;
    // Only the demux owner writes these moving averages.
    private double _recentReadTicks, _recentPacketBytes;
    private long _lastTextStamp;
    private string _countersText = "", _message = "";
    private string? _description, _recovery;

    public PlaybackMetrics Metrics => new(
        Interlocked.Read(ref _containerOpens), Interlocked.Read(ref _seeks),
        Interlocked.Read(ref _conversions), Interlocked.Read(ref _droppedFrames),
        Interlocked.Read(ref _resourceWaits));

    public bool ResourceBlocked => Volatile.Read(ref _resourceBlocked) != 0;

    public PlaybackDiagnosticSnapshot Capture() => new(
        Metrics, Interlocked.Read(ref _progressFrames), Interlocked.Read(ref _audioGaps),
        Milliseconds(Interlocked.Read(ref _readTicks)),
        Milliseconds(Interlocked.Read(ref _decodeTicks)),
        Milliseconds(Interlocked.Read(ref _convertTicks)),
        Milliseconds(Volatile.Read(ref _recentReadTicks)),
        Volatile.Read(ref _recentPacketBytes) / 1048576);

    public void ContainerOpened() => Interlocked.Increment(ref _containerOpens);
    public void SeekCompleted() => Interlocked.Increment(ref _seeks);
    public void FrameDropped() => Interlocked.Increment(ref _droppedFrames);
    public void AudioDiscontinuity() => Interlocked.Increment(ref _audioGaps);
    public void DecodeCompleted(long elapsedTicks) => Maximum(ref _decodeTicks, elapsedTicks);
    public void ReadCompleted(long elapsedTicks) => Maximum(ref _readTicks, elapsedTicks);

    public void ConversionCompleted(long elapsedTicks, bool forcedProgress)
    {
        Interlocked.Increment(ref _conversions);
        if (forcedProgress) Interlocked.Increment(ref _progressFrames);
        Maximum(ref _convertTicks, elapsedTicks);
    }

    public void ResourceUnavailable()
    {
        Interlocked.Increment(ref _resourceWaits);
        Volatile.Write(ref _resourceBlocked, 1);
    }

    public void ResourceAvailable() => Volatile.Write(ref _resourceBlocked, 0);

    public void ResetReadAverage()
    {
        Volatile.Write(ref _recentReadTicks, 0);
        Volatile.Write(ref _recentPacketBytes, 0);
    }

    public void VideoPacketRead(long elapsedTicks, int bytes)
    {
        var weight = _recentPacketBytes == 0 ? 1d : 1d / 16;
        Volatile.Write(ref _recentReadTicks, _recentReadTicks + (elapsedTicks - _recentReadTicks) * weight);
        Volatile.Write(ref _recentPacketBytes, _recentPacketBytes + (bytes - _recentPacketBytes) * weight);
    }

    public string Describe(string description, string? recovery, PlaybackHealthSnapshot health,
        int queueDepth, long audioUnderruns)
    {
        var refresh = _lastTextStamp == 0 || Stopwatch.GetElapsedTime(_lastTextStamp).TotalMilliseconds >= 250;
        if (refresh)
        {
            _lastTextStamp = Stopwatch.GetTimestamp();
            var s = Capture();
            _countersText = $"Buffer {queueDepth}/{VideoSchedulingPolicy.ReadyFrameLimit}; empty {health.QueueEmptyEvents}; late {health.PresentationUnderruns}; "
                + $"dropped {health.DroppedFrames + s.Metrics.DroppedBeforeConversion}; "
                + $"max empty/late {health.MaxQueueEmptyDuration.TotalMilliseconds:F1}/{health.MaxPresentationLateness.TotalMilliseconds:F1} ms; "
                + $"max queue wait {health.MaxQueueWaitDuration.TotalMilliseconds:F1} ms; "
                + $"max I/O/decode/convert {s.MaxReadMilliseconds:F1}/{s.MaxDecodeMilliseconds:F1}/{s.MaxConvertMilliseconds:F1} ms; "
                + $"recent video read {s.RecentReadMilliseconds:F1} ms/{s.RecentPacketMiB:F2} MiB; "
                + $"resource waits {s.Metrics.ResourceWaits}; audio underruns {audioUnderruns}; "
                + $"open/seek {s.Metrics.ContainerOpens}/{s.Metrics.Seeks}; converted {s.Metrics.Conversions}; "
                + $"progress frames {s.ProgressFrames}; audio gaps {s.AudioGaps}.";
        }
        if (refresh || _description != description || _recovery != recovery)
        {
            _description = description;
            _recovery = recovery;
            _message = $"{description}. {_countersText} {recovery}";
        }
        return _message;
    }

    private static double Milliseconds(double ticks) => ticks * 1000 / Stopwatch.Frequency;

    private static void Maximum(ref long location, long value)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref location);
            if (value <= previous) return;
        }
        while (Interlocked.CompareExchange(ref location, value, previous) != previous);
    }
}

internal readonly record struct PlaybackMetrics(long ContainerOpens, long Seeks, long Conversions,
    long DroppedBeforeConversion, long ResourceWaits);

internal readonly record struct PlaybackDiagnosticSnapshot(PlaybackMetrics Metrics, long ProgressFrames,
    long AudioGaps, double MaxReadMilliseconds, double MaxDecodeMilliseconds, double MaxConvertMilliseconds,
    double RecentReadMilliseconds, double RecentPacketMiB);
