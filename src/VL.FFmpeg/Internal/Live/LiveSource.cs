using VL.Core;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.FFmpeg.Internal.Live;

/// <summary>Gamma attachments. The live session owns media, never the consumer callback.</summary>
internal sealed class LiveSource : IAudioSource, IDisposable
{
    private readonly object _gate = new();
    private Attachment? _attachment;
    private bool _disposed;
    private int _changed;
    public LiveSession Session { get; } = new();
    public int ChangedTicket => Volatile.Read(ref _changed);

    public IVideoPlayer? Start(VideoPlaybackContext context)
    {
        lock (_gate)
        {
            if (_disposed || _attachment is not null) return null;
            Session.Attach(context);
            return _attachment = new Attachment(this);
        }
    }

    private void Detach(Attachment attachment)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(attachment, _attachment)) return;
            _attachment = null;
            Session.Attach(null);
            Interlocked.Increment(ref _changed);
        }
    }

    public IResourceProvider<AudioFrame>? GrabAudioFrame(int sampleCount, Optional<int> sampleRate,
        Optional<int> channelCount, Optional<bool> interleaved)
        => Session.GrabAudio(sampleCount, sampleRate.HasValue && sampleRate.Value > 0 ? sampleRate.Value : 48000,
            channelCount.HasValue ? Math.Max(0, channelCount.Value) : 0, interleaved.HasValue && interleaved.Value);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _attachment = null;
            Interlocked.Increment(ref _changed);
        }
        Session.Dispose();
    }

    private sealed class Attachment(LiveSource source) : IVideoPlayer
    {
        private int _disposed;
        public IResourceProvider<VideoFrame>? GrabVideoFrame()
            => Volatile.Read(ref _disposed) == 0 ? source.Session.GrabVideo() : null;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) source.Detach(this); }
    }
}
