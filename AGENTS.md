# VL.FFmpeg project rules

## Goal

Provide one FFmpeg video player for vvvv gamma. Skia and Stride use their
standard video-source consumers. Do not add renderer-specific packages unless
renderer-specific public code becomes necessary.

## Architecture

- Decode/timeline code must not depend on Skia or Stride.
- Gamma integration is `IVideoSource2`, `IVideoPlayer`, `VideoPlaybackContext`
  and `IResourceProvider<VideoFrame>`.
- The process-node assembly uses one `ImportAsIs` for `VL.FFmpeg.Nodes`.
  Do not replace it with per-type `ImportType` attributes without a Gamma
  PackageCompiler regression test.
- Workers must have bounded queues, cancellation and deterministic shutdown.
- Native FFmpeg libraries load from verified absolute paths. Never use
  process-global `SetDllDirectory`.
- A frame buffer cannot be reused until all consumer handles are released.
- Unsupported color, rotation or hardware paths must be reported explicitly.
- D3D11VA uses only the D3D11 device from `VideoPlaybackContext` and returns
  `GpuVideoFrame<BgraPixel>` through `IVideoSource2`. Keep software fallback
  renderer-neutral. Do not add Stride/Skia dependencies or a private hardware
  device without a new architecture review.

## NuGet layout

Classic external-package workflow only:

```text
VL.FFmpeg/
  VL.FFmpeg.vl
  deployment/VL.FFmpeg.nuspec
  lib/net8.0/
  runtimes/win-x64/native/
  help/
  src/
  tests/
```

- `deployment/VL.FFmpeg.nuspec` is the only package manifest.
- Build with `dotnet build`; pack with `eng/Pack.ps1` / `NuGet.exe`.
- Keep the version in the C# project and the nuspec equal. Increment both for
  every package installed into Gamma. Never replace an already installed version.
- C# `OutputPath` is package-root `lib` with the SDK default
  `AppendTargetFrameworkToOutputPath=true`, yielding `lib/net8.0`.
- `PublishVLNuget` does not copy DLLs. Populate `lib` before `nuget pack`.
- Local packing deletes generated `lib` first so wildcards cannot capture
  stale DLLs.
- The package contains exactly one managed assembly, `VL.FFmpeg.dll`. Relocated
  AutoGen source is embedded under `VL.FFmpeg.Interop.AutoGen`. Native FFmpeg
  DLLs stay under `runtimes/win-x64/native`.
- Do not use SDK-style `dotnet pack`, generated nuspec files or package-content
  MSBuild targets.
- The nuspec does not depend on `VL.Core`. The C# project uses it only to
  compile.
- Folder name, package ID and root document use exact casing `VL.FFmpeg`.

## VL documents

- `.vl` files are created and edited only by the project owner in Gamma.
- Do not read, generate or modify `.vl` without a new direct request.
- No packaged `.vl` may reference a `.csproj`.
- The root document has an empty Application patch, no absolute paths and no
  reference to its own NuGet ID.
- Its `PlatformDependency` on `lib/net8.0/VL.FFmpeg.dll` must have **Is Forward**
  enabled so C# nodes are exported to package consumers.
- Help patches live under `help/`.

## Verification

Required before a release:

1. `dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release`.
2. `eng/Pack.ps1` completes without warnings.
3. A fresh Gamma process compiles the installed binary NuGet.
4. Skia and Stride display the same source.
5. Native libraries are found in editor and exported application.

Do not claim VL package validity from a .NET restore or runtime smoke test.

## Documentation

Keep documentation current and compact. Record only the active architecture,
commands and unresolved product decisions.
