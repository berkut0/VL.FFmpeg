# Third-party notices

## Original vvvv-FFmpeg project

This project is derived from
[`kbln/vvvv-FFmpeg`](https://github.com/kbln/vvvv-FFmpeg), licensed under the
MIT License. Copyright (c) 2026 Ivan Kabalin.

## vvvv gamma

The package references `VL.Core` at compile time. Its license and notices are
supplied by its NuGet package and upstream repository.

## FFmpeg

The release package dynamically links to this BtbN shared build:

- archive: `ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip`
- release: `autobuild-2026-08-12-13-15`
- archive SHA-256: `375df631ddf38bf38feb7bbd67259c454045b8ea75b96af62c33a440ba799f48`
- FFmpeg revision: `9b6c8969e0`
- source: <https://github.com/FFmpeg/FFmpeg/commit/9b6c8969e0>
- build: <https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-12-13-15>
- configuration: LGPL shared, `--enable-version3`, without `--enable-gpl` or
  `--enable-nonfree`
- libraries: `avcodec-62`, `avformat-62`, `avutil-60`, `swresample-6`,
  `swscale-9`

FFmpeg and enabled components are LGPL 3. The shared libraries may be replaced
with ABI-compatible modified versions. The package does not restrict reverse
engineering for debugging such modifications.

## FFmpeg.AutoGen

Generated binding source from `FFmpeg.AutoGen 8.1.0`, upstream commit
`444925cd53d3611fd4c8c295873fb631be56ab21`, is vendored under the MIT License.
It is compiled into `VL.FFmpeg.dll` with a relocated namespace so it does not
conflict with `FFmpeg.AutoGen 3.4.0.2` used by Stride.Video. See
`LICENSES/FFmpeg.AutoGen-MIT.txt` in the package and
`third_party/FFmpeg.AutoGen/UPSTREAM.md` in the source repository.
