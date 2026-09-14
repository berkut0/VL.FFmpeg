using VL.Core.Import;
using VL.FFmpeg.Internal;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Video;
using VL.Model;

namespace VL.FFmpeg.Nodes;

/// <summary>
/// FFmpeg video player controlled through a reusable transport object.
/// </summary>
/// <remarks>
/// Connect Video Source to a standard video consumer and call operations on
/// Control from any part of the patch.
/// </remarks>
[ProcessNode(Name = "VideoPlayer (Advanced)")]
public sealed class AdvancedVideoPlayer : IVideoSource2, IDisposable
{
    private readonly VideoPlayerSource _source;
    private readonly VideoPlayerControl _control;

    /// <summary>
    /// Creates a remotely controlled Gamma video source. Native resources are
    /// created lazily by a subscribed video consumer.
    /// </summary>
    public AdvancedVideoPlayer()
        : this(FFmpegPlayerSessionFactory.Instance)
    {
    }

    internal AdvancedVideoPlayer(IFFmpegPlayerSessionFactory sessionFactory)
    {
        _source = new VideoPlayerSource(sessionFactory);
        _control = new VideoPlayerControl(_source);
    }

    /// <summary>
    /// Returns the renderer-neutral source, transport control and current status.
    /// </summary>
    public void Update(
        out IVideoSource videoSource,
        out IAudioSource audioSource,
        out VideoPlayerControl control,
        out double position,
        out double duration,
        out bool isPlaying,
        out bool isEnded,
        out bool onEnd,
        out bool playbackOverload,
        out PlaybackPhase phase,
        out DecodePath decodePath,
        [Pin(Visibility = PinVisibility.Optional)] out string status)
    {
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
        control = _control;
    }

    IVideoPlayer? IVideoSource2.Start(VideoPlaybackContext context)
        => ((IVideoSource2)_source).Start(context);

    int IVideoSource2.ChangedTicket
        => ((IVideoSource2)_source).ChangedTicket;

    internal VideoPlayerSource Source => _source;

    void IDisposable.Dispose()
        => ((IDisposable)_source).Dispose();
}
