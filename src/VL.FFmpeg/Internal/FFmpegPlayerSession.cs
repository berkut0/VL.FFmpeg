using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal;

/// <summary>Gamma video attachment; the source owns the shared media lifetime.</summary>
internal sealed class FFmpegPlayerSession : IVideoPlayer, IPlaybackOptionsSink
{
    private readonly VideoPlayerSource _source;
    private readonly PlaybackSession _playback;
    private int _disposed;
    public FFmpegPlayerSession(VideoPlayerSource source, VideoPlaybackContext context)
    {
        _source = source;
        _playback = source.GetPlayback();
        _playback.Attach(context);
    }
    public IResourceProvider<VideoFrame>? GrabVideoFrame()
        => Volatile.Read(ref _disposed) == 0 ? _playback.GrabVideoFrame() : null;
    public void OptionsChanged(PlaybackOptions options) => _playback.OptionsChanged(options);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _playback.Detach();
        _source.SessionDisposed(this);
    }
}

internal sealed class FFmpegPlayerSessionFactory : IFFmpegPlayerSessionFactory
{
    public static readonly FFmpegPlayerSessionFactory Instance = new();
    private FFmpegPlayerSessionFactory() { }
    public IVideoPlayer Create(VideoPlayerSource source, VideoPlaybackContext context)
        => new FFmpegPlayerSession(source, context);
}
