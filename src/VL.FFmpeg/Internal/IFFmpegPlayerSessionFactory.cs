using VL.Lib.Basics.Video;
namespace VL.FFmpeg.Internal;

internal interface IFFmpegPlayerSessionFactory
{
    IVideoPlayer Create(VideoPlayerSource source, VideoPlaybackContext context);
}
