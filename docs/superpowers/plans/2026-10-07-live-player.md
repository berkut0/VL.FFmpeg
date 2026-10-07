# Live player implementation plan

> Execution: inline with executing-plans; the user explicitly requested implementation.

Goal: implement the approved RTSP/direct HTTP(S) live node with isolated connection ownership.
Spec: ../specs/2026-10-07-live-player-design.md
Stack: existing .NET 8, FFmpeg 8.1, Gamma resource/video/audio contracts.

Constraints: one assembly; no renderer packages/private GPU device; no `.vl` work;
no version bump or publication. Use the existing dedicated feature branch.

## Tasks

- [ ] Extract `Internal/MediaPipeline.cs`: bounded queues, native I/O routing,
  stage cancellation and join. `RunAsync` takes demux, stream indices and typed
  stage delegates. File pacing/preroll/presentation stay in PlaybackSession.
  Extract `MediaDecodePump` packet send/receive/drain, shared by both controllers.
  Run all existing file regressions before live changes.
- [ ] Add `Decoding/MediaInputOptions.cs`: protocol options and monotonic open/read
  deadlines; extend FFmpegDemuxContext with optional configuration. Tests use a
  stalled loopback HTTP server to prove interruption and cancellation.
- [ ] Add `Internal/Live/LiveConnectionPolicy.cs` and `LiveTimeline.cs`. Test URL
  validation, credential redaction, 1/2/4/8/10 retry delays, terminal errors,
  common A/V epoch, missing timestamps and backward/forward discontinuities.
  Preserve raw timestamp metadata from decoders without changing file outputs.
- [ ] Add `LiveSession`, `LiveSource`, and `LiveVideoPlayer`: one connection owner,
  consumer attachment/demand, generation-safe output, transport commands and
  bounded video/audio publication. Reuse codecs, converter, budgets and workers.
  Test reconnect edge, unchanged inputs, disable during I/O, retained frames,
  strict Hardware, late audio attachment and URL replacement.
- [ ] Run real HTTP/RTSP decode against controlled servers, HTTPS verification
  where a trusted endpoint is available; test stalled reads, EOF, reconnect,
  slow consumers and mixed file/live load. Run full Release suite and fresh
  code review. Update active architecture and record concrete validation limits.

## Review focus

1. Cancelled connection must join before native contexts or device references retire.
2. Audio demand/device rebinding cannot seek or reopen a live source.
3. Timestamp reset must invalidate queued A/V output from the previous epoch.
4. Failure recovery must not expose credential-bearing URLs or silently retry auth errors.
5. Queue exhaustion must preserve codec dependencies and bounded memory.

Verification: `dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release`.
New behavior gets a failing regression first; extraction uses existing regressions.
Each task records its result here. Commit only verified stages; never push automatically.
