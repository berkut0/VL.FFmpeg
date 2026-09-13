# Architecture

## Public flow

```text
VideoPlayer | VideoPlayer (Advanced)
  -> IVideoSource2
  -> IVideoPlayer
  -> IResourceProvider<VideoFrame>
  -> VideoSourceToSKImage | VideoSourceToTexture
```

There is no Skia- or Stride-specific public API. Both renderers consume
`VideoFrame` through standard Gamma nodes.

## Components

- `FFmpegVideoDecoder` owns demux, codec and software/D3D11VA decode contexts.
- `FFmpegPlayerSession` owns playback coordination, the bounded frame queue and worker.
- `PlaybackTimeline` maps clock time and transport state to media time.
- `PlaybackControl` serializes option changes before notifying the active session.
- `VideoPlayerSource` is the shared `IVideoSource2` session boundary.
- `VideoPlayer` maps conventional pins to transport options.
- `VideoPlayer (Advanced)` exposes a reusable `VideoPlayerControl` object instead
  of transport inputs. Its operations can be called from separate patch locations.
- `D3D11TexturePool` converts NV12/P010 decoder surfaces into leased nonlinear
  BGRA8 or linear RGBA16F textures on the consumer device.
- `FFmpegRuntime` resolves the pinned Windows x64 native runtime.
- Relocated FFmpeg.AutoGen source is compiled into `VL.FFmpeg.dll` under
  `VL.FFmpeg.Interop.AutoGen`. Gamma imports only `VL.FFmpeg.Nodes`.

## Scope

Implemented: local-file software and shared-device D3D11VA decode, color
matrix/range conversion, nonlinear BGRA8 and linear RGBA16F frames,
play/pause/stop/close, seek, EOF, basic loop and object-based transport control.
Auto mode falls back to software;
explicit Hardware mode faults when unavailable.

Not implemented: audio, D3D11VA private-device CPU transfer, playback rate,
subtitles, encoding, camera capture, HDR tone mapping and network streams.

## Invariants

- Decode never blocks Gamma's frame thread.
- Queues are bounded.
- Native I/O observes cancellation.
- Worker shutdown completes before native contexts are released.
- Native runtime location is process-stable after first successful load.
- A GPU texture slot is not reused until the consumer handle releases it.
- D3D11VA binds to the device from `VideoPlaybackContext`. The package has no
  renderer dependency and does not create a hidden graphics device.
- Frame format follows `VideoPlaybackContext.UsesLinearColorspace`.
