using System.Diagnostics;
using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackDiagnosticsTests
{
    [Test]
    public void SnapshotKeepsStageMeasurementsSeparateAndDoesNotChangeLater()
    {
        var diagnostics = new PlaybackDiagnostics();
        diagnostics.DecodeCompleted(Stopwatch.Frequency / 10);
        diagnostics.ConversionCompleted(Stopwatch.Frequency / 5, forcedProgress: true);
        var snapshot = diagnostics.Capture();
        diagnostics.DecodeCompleted(Stopwatch.Frequency);
        diagnostics.ConversionCompleted(Stopwatch.Frequency / 20, forcedProgress: false);
        Assert.That(snapshot.MaxDecodeMilliseconds, Is.EqualTo(100));
        Assert.That(snapshot.MaxConvertMilliseconds, Is.EqualTo(200));
        Assert.That(snapshot.Metrics.Conversions, Is.EqualTo(1));
        Assert.That(snapshot.ProgressFrames, Is.EqualTo(1));
        Assert.That(diagnostics.Capture().MaxDecodeMilliseconds, Is.EqualTo(1000));
    }

    [Test]
    public void RecentReadsAdaptWithoutErasingLifetimeMaximum()
    {
        var diagnostics = new PlaybackDiagnostics();
        diagnostics.ReadCompleted(Stopwatch.Frequency);
        diagnostics.VideoPacketRead(Stopwatch.Frequency, 4 * 1048576);
        for (var i = 0; i < 100; i++)
            diagnostics.VideoPacketRead(Stopwatch.Frequency / 1000, 2 * 1048576);
        var snapshot = diagnostics.Capture();
        Assert.That(snapshot.MaxReadMilliseconds, Is.EqualTo(1000));
        Assert.That(snapshot.RecentReadMilliseconds, Is.LessThan(3));
        Assert.That(snapshot.RecentPacketMiB, Is.EqualTo(2).Within(.01));
        diagnostics.ResetReadAverage();
        diagnostics.VideoPacketRead(Stopwatch.Frequency / 10, 1048576);
        Assert.That(diagnostics.Capture().RecentReadMilliseconds, Is.EqualTo(100));
    }
}
