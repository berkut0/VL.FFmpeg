namespace VL.FFmpeg.Internal;

internal interface IPlaybackOptionsSink
{
    void OptionsChanged(PlaybackOptions options);
}

