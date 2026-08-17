# Acquired FFmpeg runtime

Run `eng/Acquire-FFmpegRuntime.ps1` to place the five pinned FFmpeg 8.1 shared
DLLs here. Git ignores the binaries; `eng/Pack.ps1` requires them and the LGPL
license text.

The exact URL and SHA-256 are in the acquire script and
`THIRD-PARTY-NOTICES.md`.
