# Architecture

## Public flow

```text
FFmpegVideoPlayer
  -> IVideoSource2
  -> IVideoPlayer
  -> IResourceProvider<VideoFrame>
  -> VideoSourceToSKImage | VideoSourceToTexture
```

There is no Skia- or Stride-specific public API. Both renderers consume
`VideoFrame` through standard Gamma nodes.

## Components

- `FFmpegVideoDecoder` owns demux, codec and software/D3D11VA decode contexts.
- `FFmpegPlayerSession` owns the playback clock, bounded frame queue and worker.
- `FFmpegVideoPlayer` is the Gamma process node and `IVideoSource2` boundary.
- `D3D11TexturePool` converts NV12/P010 decoder surfaces into leased BGRA8
  textures on the consumer device.
- `FFmpegRuntime` resolves the pinned Windows x64 native runtime.
- Relocated FFmpeg.AutoGen source is compiled into `VL.FFmpeg.dll` under
  `VL.FFmpeg.Interop.AutoGen`. Gamma imports only `VL.FFmpeg.Nodes`.

## Scope

Implemented: local-file software and shared-device D3D11VA decode, memory- and
texture-backed BGRA8 frames, play/pause, seek, EOF and basic loop. Auto mode
falls back to software; explicit Hardware mode faults when unavailable.

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
