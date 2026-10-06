# CPU Frame Allocation Implementation Plan

> Superseded by the leased CPU frame pool in [the active architecture](../../ARCHITECTURE.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate unnecessary initialization and tiny per-frame managed arrays from software CPU video conversion.

**Architecture:** `FFmpegVideoDecoder` owns reusable `sws_scale` argument arrays because it is a single synchronous worker primitive. CPU frame arrays remain individually owned by `CpuDecodedVideoFrame` and later by the resource provider; no pooling or consumer lifetime change is introduced.

**Tech Stack:** .NET 8, C# unsafe FFmpeg.AutoGen bindings, NUnit.

**Spec:** `docs/superpowers/specs/2026-09-22-cpu-frame-allocation-design.md`

## Global Constraints

- Do not read or modify `.vl` files.
- Decode/timeline code stays renderer-neutral and uses no Skia or Stride dependency.
- A frame buffer remains valid until all consumer resource handles release it.
- Preserve FFmpeg error handling, cancellation, packed output layout, and native-library behavior.

---

### Task 1: Reuse conversion arguments without changing output ownership

**Files:**
- Modify: `src/VL.FFmpeg/Internal/Decoding/FFmpegVideoDecoder.cs:16-44, 478-502`
- Test: `tests/VL.FFmpeg.Tests/FFmpegVideoDecoderTests.cs`

**Interfaces:**
- Consumes: `ffmpeg.sws_scale(SwsContext*, byte*[], int[], int, int, byte*[], int[])`.
- Produces: the existing `CpuDecodedVideoFrame(byte[], int, int, TimeSpan, (int N, int D), string, bool)` with unchanged pixels and metadata.

- [ ] **Step 1: Run the focused CPU decoder regression tests before the change**

Run: `dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FFmpegVideoDecoderTests`

Expected: existing CPU BGRA/RGBA16F and seek tests pass (tests requiring a local Gamma clip may be skipped).

- [ ] **Step 2: Replace per-frame argument arrays and output zeroing**

Add decoder-owned `byte*[]` source/destination arrays and `int[]` source/destination stride arrays, each sized for eight FFmpeg data slots. Immediately before `sws_scale`, copy `frame->data[index]` and `frame->linesize[index]` for indices `0..7`, set destination slot zero to the pinned output pointer and stride, and leave remaining destination slots null/zero. Replace `new byte[outputLength]` with `GC.AllocateUninitializedArray<byte>(outputLength)`.

- [ ] **Step 3: Run focused tests after the change**

Run: `dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FFmpegVideoDecoderTests`

Expected: the same output-format, alpha, dimensions, and seek behavior as Step 1.

- [ ] **Step 4: Verify the allocation boundary by inspection**

Run: `rg -n 'new byte\*\[8\]|new int\[8\]|frame->data\.ToArray|frame->linesize\.ToArray|AllocateUninitializedArray' src/VL.FFmpeg/Internal/Decoding/FFmpegVideoDecoder.cs`

Expected: no per-frame argument-array construction or fixed-array `ToArray()` call remains; one uninitialized output allocation remains.
