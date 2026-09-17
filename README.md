# VL.FFmpeg

A video player node for vvvv gamma. FFmpeg does the decoding, so you can play
the files you actually have — not only the formats a built-in player happens
to like.

Connect Video Source to the usual Skia or Stride nodes. Connect Audio Source
to `AudioSourceToAudioSignal` from VL.Audio and then to `AudioOut`.

Windows x64.

## Features

- Play, pause, seek, end-of-file and basic looping
- Optional `VideoPlayer (Advanced Controls)` node with a reusable transport-control object
- Works with Skia and Stride through the standard video nodes
- Decodes audio through the standard VL.Audio `IAudioSource` consumer
- D3D11VA decode when available
- GPU upload and color conversion for supported software-decoded pixel formats
- BT.601/BT.709/BT.2020 matrix and full/limited-range conversion
- Nonlinear BGRA8 or linear RGBA16F output according to the consumer
- Keeps decoding off the render thread

`Decode Mode` defaults to `Auto`: D3D11VA when the consumer and stream support
it, software decode otherwise. A software-decoded frame still stays on the GPU
after one plane upload when the consumer supplies a D3D11 device. Explicit
`Hardware` mode requires D3D11VA and fails instead of falling back. HDR tone
mapping is not implemented.

Use `VideoPlayer` for conventional pin-based transport. Use
`VideoPlayer (Advanced Controls)` when separate parts of a patch need to call `Open`,
`Play`, `Pause`, `Stop`, `Close` or `Seek` on the same player.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng\Acquire-FFmpegRuntime.ps1
dotnet test tests\VL.FFmpeg.Tests\VL.FFmpeg.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File eng\Pack.ps1
```

The precompiled HLSL bytecode is committed and embedded in `VL.FFmpeg.dll`.
After changing `src\VL.FFmpeg\Shaders\VideoConvert.hlsl`, rebuild it with
`eng\Compile-Shaders.ps1`; normal builds do not require the Windows SDK shader
compiler.

The five FFmpeg 8.1 shared libraries and their LGPL text are committed. The
acquire script verifies their SHA-256; it does not download anything.
`Pack.ps1` requires those files.

`Pack.ps1` writes `VL.FFmpeg.dll` to `lib/net8.0` and packs
[deployment/VL.FFmpeg.nuspec](deployment/VL.FFmpeg.nuspec) with `NuGet.exe`.
It does not compile the `.vl` document; validate the package in Gamma.

## Publish

[`.github/workflows/push_nuget.yml`](.github/workflows/push_nuget.yml)
publishes to nuget.org as `antokhio`. Trigger it with a `v*` tag or a manual
workflow run. NuGet.org must trust this GitHub repository for that account.

## Layout

```text
VL.FFmpeg.vl
deployment/VL.FFmpeg.nuspec
lib/net8.0/
runtimes/win-x64/native/
help/
```

The NuGet has no `VL.Core` dependency. Gamma hosts the VL runtime; the C#
project references `VL.Core` only at compile time. The package ships one
managed assembly, `VL.FFmpeg.dll`.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## License

MIT. Native FFmpeg libraries are LGPL 3. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
