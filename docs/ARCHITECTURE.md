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

## Realtime core

`VideoPlayerSource` owns one `PlaybackSession`; `FFmpegPlayerSession` is its
video attachment. Audio demand is configured asynchronously. Both streams use
one persistent `FFmpegDemuxContext`. Container I/O runs separately from the
bounded CPU work admission in `PlaybackWork`; codec thread counts contribute
to its concurrency budget; codecs without threading reserve one worker. Packet
read-ahead follows the media clock with allowance for reorder/thread delay.
Until video preroll finds its containing frame, only queue budgets limit reading:
the next timestamp may lie beyond normal read-ahead at low or variable frame rates.
Each loop cycle has its own preroll completion signal. GPU submission is serialized
per consumer device.

Read the core by responsibility:

| Component | Owns |
| --- | --- |
| `PlaybackSession` | Control generations, presentation state, worker coordination and safe retirement |
| `MasterClock` | Execution-to-media time mapping |
| `VideoSchedulingPolicy` / `RealtimeRecovery` | Preview, frame usefulness, read-ahead and recovery cooldown |
| `FFmpegDemuxContext` / decoders | Container I/O / codec state; decoders borrow the container |
| `VideoFrameConverter` | Hardware, software-GPU and CPU conversion resources |
| `PlaybackDiagnostics` | Atomic stage counters, immutable snapshots and cached status text |

Decoders accept packets and return frames; they never open, seek or read a file.
The session disposes them before their borrowed demux. Tests feed these same
codec primitives through `DecoderPump`; no alternate decoder loop ships in the library.

Video packets -> codec -> two referenced `NativeVideoFrame` slots -> candidate
selection -> `VideoFrameConverter` -> up to six leased frames -> Gamma.
Audio packets -> codec/resampler -> bounded `AudioSampleBuffer` -> Gamma.

`MasterClock` waits for the first video frame at initial startup, then follows
the consumer frame clock through starvation. Audio-only playback uses monotonic
time; attaching video preserves elapsed media time. Workers/audio extrapolate
an atomic clock snapshot. Paused seek publishes the containing frame. Playing
seek continues advancing from the command. Before conversion the scheduler
selects the newest due raw frame, replacing older candidates. It repeats that
selection after CPU/device admission, because waiting may have made the original
candidate obsolete. A late frame can still improve the displayed image: age
alone never rejects the only useful candidate. Frames behind the displayed
timeline or from a completed cycle are rejected; paused preview remains explicit.
The 150 ms window bounds normal read-ahead and labels late conversions. Queues,
worker admission and leases bound work; no separate periodic progress timer is
needed. The media clock continues running through overload.
Sustained lag over one second for 500 ms requests recovery seeking, limited to
once per two seconds. Automatic recovery retains the displayed-image state and
health history, so it can retry and does not latch `Buffering`. Its status message
clears when a presented frame catches up.
Pause holds the displayed image even if late producer results arrive below the
frozen clock; an explicit paused seek may publish one preview. A manual request
replacing realtime recovery also clears its diagnostic message.

Seek and loop reuse the open container, codecs and compatible converters.
Generation-scoped queues prevent cancelled work from publishing into a newer
request. A loop adds a cycle offset to a common container timeline, preserving
stream offsets. Codec and resampler draining precede rewind; ready leases survive
it. Audio reconfiguration starts from execution time, never the producer tail.
Commands invalidate generations immediately; a control worker delivers
cancellation and retires queued resources outside the presentation lock.
Consumer-device bindings retire separately, after their pipeline has stopped.
Timed waits explicitly check cancellation even when a signal is already complete,
and round positive sub-millisecond delays up to avoid spinning during pacing.

Audio pulls only read a published buffer and enqueue format demand: they do not
retire video resources, perform native I/O, or wait for a producer. Underruns and
the final partial block are padded with silence. Future PCM waits asynchronously
for capacity; expired samples are not replayed. A sample cursor absorbs callback
jitter, correcting lag beyond max(50 ms, two requested blocks); prefetch is
limited to 250 ms. Audio packet overflow records a discontinuity and resets its
codec/resampler, without blocking video. The API supplies no DAC position, so
sample-accurate hardware A/V sync is not claimed.

## Resource ownership and budgets

- Video lead target: 150 ms, at most six ready frames and two raw queued frames.
- Packet queues: eight packets / 64 MiB each. A single larger packet is permitted
  within the shared byte budget; video packet dependencies are preserved.
- Audio target: 250 ms or one requested block, whichever is larger. One decoded
  block can exceed the target. Queued PCM and outstanding audio output leases are
  charged to the CPU budget.
- Owned CPU buffers/packets: 1 GiB process-wide. GPU output/upload textures:
  1 GiB per device. Allocation is lazy; idle caches can be reclaimed between
  players. FFmpeg internal working memory, graphics-driver allocations and
  shared .NET pool caches are outside these owned-resource counters.
- CPU and GPU storage returns only when the last consumer handle releases it.
  A retired pool remains responsible for outstanding leases. Configuration
  changes retire pools instead of disabling GPU conversion permanently.
- D3D11 input views are cached with retained surfaces and bounded to 64 entries;
  output/upload slots are allocated on demand, up to eight per pool.
- Native contexts are released after all workers finish. Final `Dispose` joins
  them deterministically; no fire-and-forget native cleanup is used.

`PlaybackOverload` includes late presentation and resource exhaustion. `Status`
reports queue starvation, skipped frames, I/O/decode/conversion durations
(maxima, including native video send and receive calls), recent video packet read
time/size (exponential averages with weight 1/16), late conversions,
resource waits, audio gaps and container open/seek counts. Diagnostic text is
refreshed at most four times per second; source/conversion metadata is cached.
Stage maxima are independent observations; their sum is not reported as a frame's
production latency.

The native runtime remains pinned to verified absolute paths. Relocated AutoGen
is embedded in `VL.FFmpeg.dll`; Gamma imports only `VL.FFmpeg.Nodes`.

## Alpha

Alpha stays generic after decode: conversion follows the actual FFmpeg pixel
layout and preserves formats marked with `AV_PIX_FMT_FLAG_ALPHA`.

For Matroska/WebM VP8/VP9 streams that declare alpha, `Auto` and `Software`
prefer the corresponding libvpx decoder. Explicit `Hardware` mode keeps its
hardware-only contract. If declared alpha decodes to a format without alpha,
playback continues opaque and the existing `Status` output reports the
degradation.

Auxiliary-layer and separate-video-stream alpha require composition outside the
current single-stream decoder and are not supported.

## Scope

Implemented: local-file software and shared-device D3D11VA decode, GPU color
conversion for common planar YUV(A), planar RGB(A), semiplanar and packed RGBA
software frames, CPU fallback for other layouts, nonlinear BGRA8 and linear RGBA16F frames,
VL.Audio-compatible float audio frames, play/pause/stop/close, seek, EOF,
basic loop and object-based transport control.
Auto mode falls back to software;
explicit Hardware mode faults when unavailable.

Not implemented: D3D11VA private-device CPU transfer, playback rate,
subtitles, encoding, camera capture, HDR tone mapping, network streams and
auxiliary-layer or multi-stream alpha composition.

CPU-only linear RGBA16F conversion remains expensive at UHD resolutions.
Its swscale conversion and exact transfer LUT are retained; GPU conversion is
preferred when the consumer provides a supported device/layout. Alternative
upload methods and SIMD transfer conversion require measurements and pixel
regressions before adoption.

## Invariants

- Native decode and container I/O never run inside frame/audio callbacks.
- Audio pulls never block on the decode worker.
- Queues are bounded.
- Native I/O observes cancellation.
- Worker shutdown completes before native contexts are released.
- Native runtime location is process-stable after first successful load.
- A GPU texture slot is not reused until the consumer handle releases it.
- Every GPU path uses only the device from `VideoPlaybackContext`. The package
  has no renderer dependency and does not create a hidden graphics device.
- Software GPU conversion is selected from the decoded pixel layout, never the codec ID.
- Alpha is accepted only from a decoded pixel layout that FFmpeg marks as
  alpha-bearing; container declarations affect decoder selection and status,
  not frame conversion.
- Software decode performs one CPU-to-GPU plane upload and no CPU color conversion
  when its pixel layout is supported by the D3D11 converter.
- FFmpeg software decoding uses at most eight codec threads so high-resolution
  frame-threaded formats gain throughput without unbounded native frame memory.
- Frame format follows `VideoPlaybackContext.UsesLinearColorspace`.

## Verification

`dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release` covers
transport, cancellation ownership, shared-container seek/loop, CPU/GPU color,
alpha, frame leases, audio drain, budget reclamation and realtime dropping.

Measured results and test conditions are summarized in [PERFORMANCE.md](PERFORMANCE.md).
Temporary tracing, benchmark executables and raw investigation logs are not part
of the playback library. Gamma/Skia/Stride/export and package validation still
require the project owner's release checks.
