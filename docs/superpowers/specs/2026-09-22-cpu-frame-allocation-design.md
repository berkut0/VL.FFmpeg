# CPU frame-allocation design

## Goal

Remove needless managed work from the software CPU video conversion path without
changing frame ownership, public APIs, renderer isolation, or output pixels.

## Decision

`FFmpegVideoDecoder` remains the sole owner of `sws_scale` call arguments. It
will allocate its packed BGRA/RGBA64 output with
`GC.AllocateUninitializedArray<byte>` because `sws_scale` writes every exposed
output row and conversion failure throws before a frame is exposed. The decoder
will retain its four eight-element argument arrays and refresh all source
entries for each call.

No `ArrayPool<byte>` is introduced. A `CpuDecodedVideoFrame` transfers its
array into a `VideoFrame` resource provider whose consumer handles may outlive
the decoder queue entry. Pooling would therefore require a new release contract
and is outside this change.

## Invariants

- The CPU output remains tightly packed BGRA8 or RGBA16F with the existing
  stride and dimensions.
- `sws_scale` failure or a partial-height result never exposes the output.
- FFmpeg source plane pointers and strides are refreshed for all eight slots.
- No Skia, Stride, `.vl`, public API, or lifetime behavior changes.

## Validation

Run the existing decoder CPU output tests and the complete Release test suite.
Inspect the conversion path to confirm no per-frame `sws_scale` argument arrays
remain and that output allocation is uninitialized only at the point fully
overwritten by `sws_scale`.
