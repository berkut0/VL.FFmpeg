using System.Diagnostics;

namespace VL.FFmpeg.Internal;

/// <summary>Frame-clock authority with a lock-free extrapolated snapshot for workers/audio.</summary>
internal sealed class MasterClock
{
    private readonly object _gate = new();
    private Snapshot _snapshot = new(0, Stopwatch.GetTimestamp(), false);
    private double? _frameAnchor;
    private double _mediaAnchor;
    private bool _play;
    private bool _waiting;
    private bool _hasVideoClock;

    public double Position
    {
        get
        {
            var s = Volatile.Read(ref _snapshot);
            return s.Position + (s.Running ? Stopwatch.GetElapsedTime(s.Stamp).TotalSeconds : 0);
        }
    }
    public bool Waiting { get { lock (_gate) return _waiting; } }
    public void Reset(double position, bool play, bool waitForReady)
    {
        lock (_gate)
        {
            _mediaAnchor = position;
            _frameAnchor = null;
            _play = play;
            _waiting = waitForReady;
            Publish(position, play && !waitForReady);
        }
    }
    public void SetPlay(bool play)
    {
        lock (_gate)
        {
            if (_play == play) return;
            _mediaAnchor = _hasVideoClock ? _snapshot.Position : Position;
            _play = play;
            _frameAnchor = null;
            Publish(_mediaAnchor, play && !_waiting);
        }
    }
    public void Ready()
    {
        lock (_gate)
        {
            if (!_waiting) return;
            _waiting = false;
            _frameAnchor = null;
            Publish(_mediaAnchor, _play && !_hasVideoClock);
        }
    }
    public double Observe(double seconds)
    {
        lock (_gate)
        {
            if (!_hasVideoClock && !_waiting)
            {
                _mediaAnchor = Position;
                _frameAnchor = seconds;
            }
            _hasVideoClock = true;
            if (_waiting) return _mediaAnchor;
            _frameAnchor ??= seconds;
            var position = _mediaAnchor + (_play ? Math.Max(0, seconds - _frameAnchor.Value) : 0);
            Publish(position, _play);
            return position;
        }
    }
    public void DetachVideo()
    {
        lock (_gate) { _hasVideoClock = false; _frameAnchor = null; _mediaAnchor = Position; }
    }
    private void Publish(double position, bool running)
        => Volatile.Write(ref _snapshot, new(position, Stopwatch.GetTimestamp(), running));
    private sealed record Snapshot(double Position, long Stamp, bool Running);
}
