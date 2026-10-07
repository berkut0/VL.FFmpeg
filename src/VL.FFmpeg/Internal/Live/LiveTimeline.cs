namespace VL.FFmpeg.Internal.Live;

internal readonly record struct LiveTime(double Time, long Epoch, bool Accepted = true);

/// <summary>Maps source timestamps into one A/V epoch. Called after decoder reordering.</summary>
internal sealed class LiveTimeline
{
    private readonly object _gate = new();
    private readonly Track _video = new(), _audio = new();
    private double? _origin;
    private double _anchor;
    private double _sourceShift;
    private long _epoch;

    public LiveTime Map(double? timestamp, double duration, double arrival, bool video)
    {
        lock (_gate)
        {
            var track = video ? _video : _audio;
            var source = timestamp ?? (duration > 0 && track.Source is { } previous
                ? previous + track.Duration : (_origin ?? 0) + arrival - _anchor);
            if (_origin is null) { _origin = source; _anchor = arrival; }
            if (track.Epoch != _epoch && track.Source is { } oldSource)
            {
                var oldExpected = oldSource + Math.Max(0, arrival - track.Arrival);
                var newExpected = oldExpected + _sourceShift - track.SourceShift;
                if (timestamp is null) source = newExpected;
                else if (Math.Abs(source - oldExpected) < Math.Abs(source - newExpected))
                    return new(0, _epoch, false); // Still belongs to the previous epoch.
            }
            if (track.Epoch == _epoch && track.Source is { } last)
            {
                var delta = source - last;
                if (delta < -.5 || delta - (arrival - track.Arrival) > 5)
                {
                    _sourceShift += delta - (arrival - track.Arrival);
                    _origin = source;
                    _anchor = arrival;
                    _epoch++;
                }
            }
            track.Source = source;
            track.Duration = duration;
            track.Arrival = arrival;
            track.Epoch = _epoch;
            track.SourceShift = _sourceShift;
            return new(source - _origin.Value + _anchor, _epoch);
        }
    }

    private sealed class Track
    {
        public double? Source;
        public double Duration, Arrival, SourceShift;
        public long Epoch;
    }
}
