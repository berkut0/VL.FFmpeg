namespace VL.FFmpeg.Internal.Decoding;

internal sealed record DecodedAudioFrame(
    float[] Samples,
    int ChannelCount,
    int SampleCount,
    int SampleOffset,
    int SampleRate,
    TimeSpan Timecode)
{
    public double? SourceTime { get; init; }
}

internal sealed record FFmpegAudioMediaInfo(
    TimeSpan Duration,
    int ChannelCount,
    int SampleRate,
    string AudioCodec);
