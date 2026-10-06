using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class NativeVideoFrame : IDisposable
{
    private AVFrame* _frame;
    public AVFrame* Frame => _frame;
    public double Time { get; }
    public double Duration { get; }
    public NativeVideoFrame(AVFrame* frame, double time, double duration)
    {
        _frame = ffmpeg.av_frame_clone(frame);
        if (_frame is null) throw new OutOfMemoryException();
        Time = time;
        Duration = duration;
    }
    public void Dispose()
    {
        var frame = _frame;
        _frame = null;
        if (frame is not null) ffmpeg.av_frame_free(&frame);
    }
}
