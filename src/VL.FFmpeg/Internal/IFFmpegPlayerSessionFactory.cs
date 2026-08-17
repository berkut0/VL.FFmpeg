using VL.Lib.Basics.Video;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal;

internal interface IFFmpegPlayerSessionFactory
{
    IVideoPlayer Create(FFmpegVideoPlayer source, VideoPlaybackContext context);
}
