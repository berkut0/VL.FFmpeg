namespace VL.FFmpeg.Internal;

internal readonly record struct PlaybackHealthSnapshot(
    int QueueDepth,
    long QueueEmptyEvents,
    long PresentationUnderruns,
    long DroppedFrames,
    TimeSpan MaxQueueEmptyDuration,
    TimeSpan MaxPresentationLateness,
    TimeSpan MaxProducerDuration,
    TimeSpan MaxQueueWaitDuration,
    bool IsQueueEmpty,
    bool IsPresentationLate);

internal sealed class PlaybackHealthTracker
{
    private const double LatenessEpsilonSeconds = 0.001d;

    private readonly object _syncRoot = new();
    private bool _hasPresentedFrame;
    private bool _queueEmpty;
    private bool _presentationLate;
    private double _lastPresentedTimelineSeconds;
    private double _queueEmptyStartedClockSeconds;
    private double _maxQueueEmptySeconds;
    private double _maxPresentationLatenessSeconds;
    private TimeSpan _maxProducerDuration;
    private TimeSpan _maxQueueWaitDuration;
    private long _queueEmptyEvents;
    private long _presentationUnderruns;
    private long _droppedFrames;

    public PlaybackHealthSnapshot Observe(
        double clockSeconds,
        double targetTimelineSeconds,
        double? presentedTimelineSeconds,
        int queueDepth,
        double frameDurationSeconds,
        int drainedFrames,
        bool active)
    {
        lock (_syncRoot)
        {
            if (presentedTimelineSeconds is { } presentedTimeline)
            {
                _lastPresentedTimelineSeconds = presentedTimeline;
                _hasPresentedFrame = true;
            }

            _droppedFrames += Math.Max(0, drainedFrames - 1);

            var queueEmpty = active
                && _hasPresentedFrame
                && presentedTimelineSeconds is null
                && queueDepth == 0;
            ObserveQueueEmpty(clockSeconds, queueEmpty);

            var latenessSeconds = queueEmpty && frameDurationSeconds > 0d
                ? targetTimelineSeconds
                    - (_lastPresentedTimelineSeconds + frameDurationSeconds)
                : 0d;
            var presentationLate = latenessSeconds > LatenessEpsilonSeconds;
            if (presentationLate && !_presentationLate)
                _presentationUnderruns++;
            _presentationLate = presentationLate;
            _maxPresentationLatenessSeconds = Math.Max(
                _maxPresentationLatenessSeconds,
                latenessSeconds);

            return Snapshot(queueDepth);
        }
    }

    public void ObserveProducerDuration(TimeSpan duration)
    {
        lock (_syncRoot)
            _maxProducerDuration = Max(_maxProducerDuration, duration);
    }

    public void ObserveQueueWaitDuration(TimeSpan duration)
    {
        lock (_syncRoot)
            _maxQueueWaitDuration = Max(_maxQueueWaitDuration, duration);
    }

    private void ObserveQueueEmpty(double clockSeconds, bool queueEmpty)
    {
        if (queueEmpty)
        {
            if (!_queueEmpty)
            {
                _queueEmpty = true;
                _queueEmptyStartedClockSeconds = clockSeconds;
                _queueEmptyEvents++;
            }

            _maxQueueEmptySeconds = Math.Max(
                _maxQueueEmptySeconds,
                Math.Max(0d, clockSeconds - _queueEmptyStartedClockSeconds));
        }
        else if (_queueEmpty)
        {
            _maxQueueEmptySeconds = Math.Max(
                _maxQueueEmptySeconds,
                Math.Max(0d, clockSeconds - _queueEmptyStartedClockSeconds));
            _queueEmpty = false;
        }
    }

    private PlaybackHealthSnapshot Snapshot(int queueDepth)
        => new(
            QueueDepth: queueDepth,
            QueueEmptyEvents: _queueEmptyEvents,
            PresentationUnderruns: _presentationUnderruns,
            DroppedFrames: _droppedFrames,
            MaxQueueEmptyDuration: TimeSpan.FromSeconds(_maxQueueEmptySeconds),
            MaxPresentationLateness: TimeSpan.FromSeconds(
                _maxPresentationLatenessSeconds),
            MaxProducerDuration: _maxProducerDuration,
            MaxQueueWaitDuration: _maxQueueWaitDuration,
            IsQueueEmpty: _queueEmpty,
            IsPresentationLate: _presentationLate);

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
        => left >= right ? left : right;
}
