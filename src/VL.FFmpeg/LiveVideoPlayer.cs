using VL.Core.Import;
using VL.FFmpeg.Internal.Live;
using VL.Lib.Basics.Audio;
using VL.Lib.Basics.Video;
using VL.Model;

namespace VL.FFmpeg.Nodes;

/// <summary>Receives a live RTSP or direct HTTP/HTTPS source.</summary>
[ProcessNode(Name = "VideoPlayer (Live)")]
public sealed class LiveVideoPlayer : IVideoSource2, IDisposable
{
    private readonly LiveSource _source = new();
    internal LiveSession Session => _source.Session;

    public void Update(out IVideoSource videoSource, out IAudioSource audioSource,
        out LivePlaybackPhase phase, out bool playbackOverload,
        [Pin(Visibility = PinVisibility.Optional)] out DecodePath decodePath,
        [Pin(Visibility = PinVisibility.Optional)] out string status,
        string url = "", bool enabled = true,
        [Pin(Visibility = PinVisibility.Optional)] bool reconnect = false,
        [Pin(Visibility = PinVisibility.Optional)] DecodeMode decodeMode = DecodeMode.Auto,
        [Pin(Visibility = PinVisibility.Optional)] LiveTransport transport = LiveTransport.Tcp)
    {
        Session.Update(new(url ?? "", enabled, decodeMode, transport), reconnect);
        var snapshot = Session.Status;
        videoSource = this;
        audioSource = _source;
        phase = snapshot.Phase;
        playbackOverload = snapshot.Overload;
        decodePath = snapshot.Path;
        status = snapshot.Message;
    }

    IVideoPlayer? IVideoSource2.Start(VideoPlaybackContext context) => _source.Start(context);
    int IVideoSource2.ChangedTicket => _source.ChangedTicket;
    void IDisposable.Dispose() => _source.Dispose();
}

/// <summary>Transport used by RTSP; HTTP/HTTPS use their native transport.</summary>
public enum LiveTransport { Tcp, Udp }

/// <summary>Connection and output state of a live source.</summary>
public enum LivePlaybackPhase { Idle, Connecting, Buffering, Playing, Reconnecting, Faulted }
