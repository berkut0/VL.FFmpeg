#nullable disable
using System.Collections.Generic;

namespace VL.FFmpeg.Interop.AutoGen;

public static unsafe partial class ffmpeg
{
    public static Dictionary<string, int> LibraryVersionMap = new Dictionary<string, int>
    {
        {"avcodec", 62},
        {"avdevice", 62},
        {"avfilter", 11},
        {"avformat", 62},
        {"avutil", 60},
        {"swresample", 6},
        {"swscale", 9},
    };
}
