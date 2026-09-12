namespace VL.FFmpeg.Internal;

internal sealed class PlaybackTimeline
{
    private double _positionSeconds;
    private double _anchorPositionSeconds;
    private double _anchorClockSeconds;
    private bool _needsAnchor = true;
    private bool _lastPlay;

    public double Update(double clockSeconds, bool play)
    {
        if (_needsAnchor)
        {
            _anchorClockSeconds = clockSeconds;
            _anchorPositionSeconds = _positionSeconds;
            _lastPlay = play;
            _needsAnchor = false;
            return _positionSeconds;
        }

        if (_lastPlay != play)
        {
            if (_lastPlay)
                _positionSeconds = CurrentPosition(clockSeconds);

            _anchorClockSeconds = clockSeconds;
            _anchorPositionSeconds = _positionSeconds;
            _lastPlay = play;
        }

        _positionSeconds = CurrentPosition(clockSeconds);
        return _positionSeconds;
    }

    public void Reset(double positionSeconds)
    {
        _positionSeconds = positionSeconds;
        _anchorPositionSeconds = positionSeconds;
        _needsAnchor = true;
    }

    private double CurrentPosition(double clockSeconds)
    {
        if (!_lastPlay)
            return _positionSeconds;

        var elapsed = Math.Max(0d, clockSeconds - _anchorClockSeconds);
        return _anchorPositionSeconds + elapsed;
    }
}
