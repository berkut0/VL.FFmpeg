using VL.Core.Import;
using VL.FFmpeg.Internal;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Video;
using VL.Model;

namespace VL.FFmpeg.Nodes;

/// <summary>
/// FFmpeg video player for vvvv gamma.
/// </summary>
/// <remarks>
/// Connect the output to VideoSourceToSKImage or VideoSourceToTexture.
/// </remarks>
[ProcessNode]
public sealed class VideoPlayer : IVideoSource2, IDisposable
{
    private readonly VideoPlayerSource _source;

    /// <summary>
    /// Creates a Gamma video source. Native resources are created lazily by a
    /// subscribed video consumer.
    /// </summary>
    public VideoPlayer()
        : this(FFmpegPlayerSessionFactory.Instance)
    {
    }

    internal VideoPlayer(IFFmpegPlayerSessionFactory sessionFactory)
    {
        _source = new VideoPlayerSource(sessionFactory);
    }

    /// <summary>
    /// Updates playback parameters and returns a renderer-neutral video source.
    /// </summary>
    public void Update(
        out IVideoSource videoSource,
        out IAudioSource audioSource,
        out double position,
        out double duration,
        out bool isPlaying,
        out bool isEnded,
        out bool onEnd,
        out bool playbackOverload,
        out PlaybackPhase phase,
        out DecodePath decodePath,
        [Pin(Visibility = PinVisibility.Optional)] out string status,
        string filename = "",
        bool play = true,
        bool loop = false,
        double seekTime = 0d,
        bool seek = false,
        [Pin(Visibility = PinVisibility.Optional)] DecodeMode decodeMode = DecodeMode.Auto)
    {
        _source.UpdateFromPins(filename, play, loop, seekTime, seek, decodeMode);
        _source.ReadOutputs(
            out position,
            out duration,
            out isPlaying,
            out isEnded,
            out onEnd,
            out playbackOverload,
            out phase,
            out decodePath,
            out status);
        videoSource = this;
        audioSource = _source;
    }

    IVideoPlayer? IVideoSource2.Start(VideoPlaybackContext context)
        => ((IVideoSource2)_source).Start(context);

    int IVideoSource2.ChangedTicket
        => ((IVideoSource2)_source).ChangedTicket;

    internal PlaybackOptions Options => _source.Options;

    internal void PublishStatus(IVideoPlayer session, PlaybackStatus status)
        => _source.PublishStatus(session, status);

    void IDisposable.Dispose()
        => ((IDisposable)_source).Dispose();
}
