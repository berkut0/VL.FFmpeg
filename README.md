# VL.FFmpeg

FFmpeg-backed `IVideoSource2` for vvvv gamma. One source feeds the standard
Skia `VideoSourceToSKImage` and Stride `VideoSourceToTexture` nodes.

Windows x64. Alpha.

## Features

- Software decode to CPU-backed BGRA8 frames
- Shared-device D3D11VA decode to GPU-backed BGRA8 frames
- Play, pause, seek, EOF and basic looping
- Bounded decode queue and cancellable native I/O

`Decode Mode` defaults to `Auto`: consumers with `Prefer GPU` use D3D11VA,
otherwise software. Explicit `Hardware` mode faults instead of falling back.
NV12 and P010 are converted on the GPU. HDR tone mapping and audio are not
implemented.

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
