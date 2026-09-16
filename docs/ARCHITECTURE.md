# Architecture

## Public flow

```text
VideoPlayer | VideoPlayer (Advanced Controls)
  -> IVideoSource2
  -> IVideoPlayer
  -> IResourceProvider<VideoFrame>
  -> VideoSourceToSKImage | VideoSourceToTexture

VideoPlayer | VideoPlayer (Advanced Controls)
  -> IAudioSource
  -> AudioSourceToAudioSignal
  -> AudioOut
```

There is no Skia- or Stride-specific public API. Both renderers consume
`VideoFrame` through standard Gamma nodes.

## Components

- `FFmpegVideoDecoder` owns demux, codec and software/D3D11VA decode contexts.
- `FFmpegAudioDecoder` owns a separate audio demux/codec pipeline and converts
  decoded samples to planar float through libswresample.
- `FFmpegAudioSession` fills a bounded `AudioSampleBuffer`; audio pulls never
  perform native I/O or wait for decode.
- `FFmpegPlayerSession` owns playback coordination, the bounded frame queue and worker.
- `PlaybackTimeline` maps clock time and transport state to media time.
- `PlaybackControl` serializes option changes before notifying the active session.
- `VideoPlayerSource` is the shared `IVideoSource2` and `IAudioSource` boundary.
- `VideoPlayer` maps conventional pins to transport options.
- `VideoPlayer (Advanced Controls)` exposes a reusable `VideoPlayerControl` object instead
  of transport inputs. Its operations can be called from separate patch locations.
- `D3D11TexturePool` converts NV12/P010 decoder surfaces into leased nonlinear
  BGRA8 or linear RGBA16F textures on the consumer device.
- `FFmpegRuntime` resolves the pinned Windows x64 native runtime.
- Relocated FFmpeg.AutoGen source is compiled into `VL.FFmpeg.dll` under
  `VL.FFmpeg.Interop.AutoGen`. Gamma imports only `VL.FFmpeg.Nodes`.

## Scope

Implemented: local-file software and shared-device D3D11VA decode, color
matrix/range conversion, nonlinear BGRA8 and linear RGBA16F frames,
VL.Audio-compatible float audio frames, play/pause/stop/close, seek, EOF,
basic loop and object-based transport control.
Auto mode falls back to software;
explicit Hardware mode faults when unavailable.

Not implemented: D3D11VA private-device CPU transfer, playback rate,
subtitles, encoding, camera capture, HDR tone mapping and network streams.

Audio and video currently use separate FFmpeg demux contexts. They share
transport commands, while video presentation remains frame-clock-driven. The
long-term A/V clock policy is an unresolved product decision.

## Invariants

- Decode never blocks Gamma's frame thread.
- Audio pulls never block on the decode worker.
- Queues are bounded.
- Native I/O observes cancellation.
- Worker shutdown completes before native contexts are released.
- Native runtime location is process-stable after first successful load.
- A GPU texture slot is not reused until the consumer handle releases it.
- D3D11VA binds to the device from `VideoPlaybackContext`. The package has no
  renderer dependency and does not create a hidden graphics device.
- Frame format follows `VideoPlaybackContext.UsesLinearColorspace`.
