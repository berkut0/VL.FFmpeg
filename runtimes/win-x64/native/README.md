# FFmpeg runtime

The five LGPL shared FFmpeg 8.1 DLLs are committed here. GitHub Actions and
`eng/Pack.ps1` use these files directly.

`eng/Acquire-FFmpegRuntime.ps1` verifies their SHA-256. It does not download
BtbN autobuilds. Provenance is in `THIRD-PARTY-NOTICES.md`.
