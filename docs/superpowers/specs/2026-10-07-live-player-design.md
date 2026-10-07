# VideoPlayer (Live)

Status: implemented on feat/live-player; native protocol tests pass, owner Gamma validation remains.

## Intent

Add a small live-player node for RTSP and direct HTTP/HTTPS streams. Keep
connection and recovery behavior together. Reuse decoding, conversion and
resource ownership with the existing file player. Preserve its public API and
seek/loop behavior. Stay in one assembly, without renderer dependencies or a
private D3D11 device. Do not read or modify `.vl` documents during implementation.

The first release targets continuous streams, including video with audio,
video-only and audio-only sources. Network recordings remain the responsibility
of `VideoPlayer`. HLS/DASH, SRT, DVR, pause/freeze, stream selection and arbitrary
FFmpeg option dictionaries are outside the initial supported contract.

## Public node

Expose `LiveVideoPlayer` as `VideoPlayer (Live)` in `VL.FFmpeg.Nodes`, using the
existing assembly import. Return standard Gamma video and audio sources.

| Input | Default | Meaning |
| --- | --- | --- |
| URL | Empty | Address of the live source |
| Enabled | True | Maintain a connection while a consumer is attached |
| Reconnect | False, optional bang | Cancel the current connection and start a fresh one |
| Decode Mode | Auto, optional | Existing Auto/Software/strict Hardware behavior |
| Transport | TCP, optional | RTSP transport: TCP or UDP; ignored for HTTP/HTTPS |

Outputs: Video Source, Audio Source, Phase, Playback Overload; optional Decode
Path and Status. Do not expose Position, Duration, Seek, Loop or an estimated
end-to-end latency. `LivePlaybackPhase` describes Idle, Connecting, Buffering,
Playing, Reconnecting and Faulted. Idle includes disabled, empty URL and no
consumer; Status distinguishes these cases.

Stable inputs do not reopen the source. Reconnect triggers on a rising edge.
URL, transport or decode-mode changes replace the connection. Enabled=False
cancels connection/retry work and stops publication without blocking Update.
Enabling again starts a fresh connection. The consumer may retain its last
image; the node never invalidates an outstanding resource handle.

## Architecture

Use separate file/live session controllers over a shared media pipeline.

```text
VideoPlayer / AdvancedVideoPlayer -> PlaybackSession (file transport)
VideoPlayer (Live)                -> LiveSession (connection lifecycle)
                                      |
                               shared MediaPipeline
                                      |
                       demux -> decoders -> conversion
                                      |
                           bounded video/audio output
```

`Internal/Live/` contains LiveSource (Gamma integration), LiveSession,
LiveConnectionPolicy and LiveTimeline. The node is a thin adapter. Connection
state has one owner; callbacks only consume published output and signal demand.

Extract MediaPipeline from the working stages of `PlaybackSession.Pipeline.cs`.
It owns one run's bounded packet/raw-frame queues and stage tasks. It borrows
demux/codecs from its session, which disposes them only after the run has joined.
It reports completion, failure and owned decoded results. It does not open,
seek, reconnect, loop or decide whether to pause. Its small internal contract
provides time mapping, read-ahead admission and output acceptance. Define only
operations required by both controllers; avoid a generic plug-in framework.

The file session continues to own seek, preroll, loop and file-clock behavior.
The live session owns connection attempts and live-time recovery. Neither
controller calls the other. No `IsLive` branches spread through the file player.
Keep resource pools, PlaybackWork, codec primitives and converters shared.

Rejected alternatives: copying the file pipeline into Live would duplicate
ownership and cancellation bugs; adding live branches to PlaybackSession would
mix incompatible transport lifecycles. The shared extraction is the necessary
common change and must land with file regressions passing before live behavior.

## Connection lifecycle

Accept RTSP, HTTP and HTTPS URLs. Do not infer live semantics from duration or
seekability: choosing this node explicitly requests live behavior. Other schemes
and detected playlist demuxers outside the supported scope report a clear error.

Open/probe/read remain on the native-I/O owner. Add an optional internal input
configuration to FFmpegDemuxContext: native options and operation deadlines.
File callers retain current defaults. Free AVDictionary on every exit, and keep
the interrupt delegate rooted until native close completes. Unknown requested
options must not be silently ignored.

Initial internal limits: 10 seconds for open/probe and 5 seconds without a
completed read. Use a monotonic deadline plus cancellation in the interrupt
callback and the applicable protocol timeout. Do not apply I/O deadlines while
waiting for queue space. These are cooperative native deadlines, not a promise
that every OS resolver call can be forcibly interrupted.

Retry transient connection/read failures and live EOF with delays of 1, 2, 4,
8 and 10 seconds, then Faulted. Reset the retry allowance after 10 seconds of
healthy output. Manual Reconnect or an input change starts a new allowance.
Authentication, unsupported input/codec and strict Hardware failures are terminal.
Preserve native error codes for classification. Cancellation is never a failure.
The session owns retry; do not enable a second independent FFmpeg retry loop.

Every connection has a generation. Late output from a retired connection is
discarded. The previous attempt must stop before its replacement opens. During
reconnection retain the consumer's last valid image and provide no stale audio.
Final Dispose joins all tasks before releasing native contexts.

No live path calls seek. A finite file URL can reach EOF and be retried under
this contract; direct users to VideoPlayer for recordings rather than guessing
source type from incomplete metadata.

## Time and backpressure

Preserve signed source timestamps until the live session maps them. Decoded
frames carry optional SourceTime separately from their established file Timecode.
This recognizes live timestamp resets while preserving file outputs and preroll.

LiveTimeline uses one connection epoch for audio and video, preserving their
relative offsets. Video establishes presentation when attached; audio cannot
delay first video. Audio-only uses monotonic time. Gamma FrameClock remains the
presentation clock when video is attached.

Use a bounded startup/jitter reserve, initially 150 ms, without waiting forever
for a low-rate source to fill it. Future decoded frames retain their timestamps.
Late superseded frames are removed before conversion. The six-ready/two-raw-frame
limits and process/device budgets remain hard limits; the reserve is a target.

Detect backward resets and large forward discontinuities after decoder reorder,
not by comparing adjacent packet PTS. A backward jump over 500 ms, or a forward
jump exceeding the monotonic arrival-time gap by over 5 seconds, starts recovery.
Rejoin both tracks under one new epoch, retire old queued output and reset audio
scheduling. Ordinary network gaps do not alone change epoch. Missing timestamps
use accumulated frame/sample durations; video without a usable duration uses
monotonic arrival time. Test these rules with B-frames, sparse video and gaps.

Do not use automatic seek for catch-up or reconnect merely because rendering is
slow. Remove stale decoded candidates before conversion. The first version does
not reconnect based on backlog metrics: a reconnect can worsen sustained decode
overload. Report backlog and keep bounded queues; manual Reconnect is available.
Never discard arbitrary interdependent compressed video packets. Bounded local
queues do not guarantee bounded camera-to-screen latency when TCP or the sender
buffers data upstream.

Audio demand must not seek or reopen the network source. Reconfigure its decoder/
resampler at the owning stage, discard obsolete PCM, and resume on the current
live timeline. Video stays independent. Audio callbacks never wait for producer
work; missing samples are silence. Device changes may replace the video decoder
and wait for a keyframe, but do not seek the source.

Status reports connection/retry state, timeout stage, decode/conversion trouble,
resource starvation and local backlog. Local backlog is not camera-to-screen
latency. Redact URL credentials and query tokens from diagnostics.

## Verification and completion

1. Extract the shared pipeline with existing file regressions passing: seek,
   pause/resume, loop, low FPS, audio demand, fallback and retained handles.
2. Test the live controller and clock deterministically: input changes, retries,
   cancellation, stale generations, timestamp resets and missing timestamps.
3. Use local controlled HTTP and RTSP sources for actual native decode. Cover
   stalled open/read, disconnect/restart, EOF, video-only, audio-only and A/V.
   Validate HTTPS against a trusted test endpoint without disabling certificate
   verification. Protocol claims require a real protocol test, not just mocks.
4. Exercise slow consumers, resource exhaustion and several concurrent live/file
   players. Memory stays bounded, callbacks remain responsive and shutdown joins.
5. Verify unchanged consumer handles across reconnect, format changes and Dispose.
   Reuse existing color/alpha and strict Hardware tests.

New tests live under `tests/VL.FFmpeg.Tests/Live/`. Keep test servers separate from
production code. Do not add runtime packages merely to host a test server.
Gamma node discovery, Skia/Stride and export validation remain owner-run unless
separately authorized. No package publication or version bump is part of this work.

Implementation proceeds in reviewable steps: shared extraction; live lifecycle
and time mapping; node and A/V integration; native protocol/overload verification.
Before implementation, specify the pipeline contract and timestamp/retry tests
in the implementation plan. Do not introduce public tuning knobs to compensate
for an unverified lifecycle design.
