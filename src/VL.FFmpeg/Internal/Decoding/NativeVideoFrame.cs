using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

internal unsafe sealed class NativeVideoFrame : IDisposable
{
    private AVFrame* _frame;
    public AVFrame* Frame => _frame;
    public double Time { get; private set; }
    public double? SourceTime { get; }
    public long Epoch { get; private set; }
    public double Duration { get; }
    public NativeVideoFrame(AVFrame* frame, double time, double duration, double? sourceTime = null)
    {
        _frame = ffmpeg.av_frame_clone(frame);
        if (_frame is null) throw new OutOfMemoryException();
        Time = time;
        Duration = duration;
        SourceTime = sourceTime;
    }
    // Only the producer maps time, before transferring the frame to the raw queue.
    public void MapTime(double time, long epoch) { Time = time; Epoch = epoch; }
    public void Dispose()
    {
        var frame = _frame;
        _frame = null;
        if (frame is not null) ffmpeg.av_frame_free(&frame);
    }
}
