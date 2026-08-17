# VL.FFmpeg

A video player node for vvvv gamma. FFmpeg does the decoding, so you can play
the files you actually have — not only the formats a built-in player happens
to like.

Connect it to the usual Skia or Stride video nodes.

Windows x64. Alpha.

## Features

- Play, pause, seek, end-of-file and basic looping
- Works with Skia and Stride through the standard video nodes
- GPU decode when the renderer can take a texture, otherwise software
- Keeps decoding off the render thread

`Decode Mode` defaults to `Auto`: GPU when the consumer asks for it, software
otherwise. Explicit `Hardware` mode fails instead of falling back. HDR tone
mapping and audio are not implemented.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng\Acquire-FFmpegRuntime.ps1
dotnet test tests\VL.FFmpeg.Tests\VL.FFmpeg.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File eng\Pack.ps1
```

The acquire script downloads the pinned FFmpeg 8.1 shared libraries. They are
gitignored and required by `Pack.ps1`.

`Pack.ps1` writes `VL.FFmpeg.dll` to `lib/net8.0` and packs
[deployment/VL.FFmpeg.nuspec](deployment/VL.FFmpeg.nuspec) with `NuGet.exe`.
It does not compile the `.vl` document; validate the package in Gamma.

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
