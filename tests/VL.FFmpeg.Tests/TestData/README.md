# Video fixtures

- `vp8-alpha.webm` declares alpha and contains a VP8 alpha payload.
- `vp9-alpha.webm` declares alpha and contains a VP9 alpha payload.
- `vp9-declared-alpha-without-payload.webm` declares alpha but contains only
  opaque VP9 video. It exercises non-fatal degradation and `Status` reporting.

Equivalent one-frame fixtures can be generated with an FFmpeg build that
includes libvpx:

```powershell
ffmpeg -y -f lavfi -i "color=c=red@0.25:s=16x16:r=25:d=0.04,format=yuva420p" -frames:v 1 -an -c:v libvpx -crf 4 -b:v 0 -pix_fmt yuva420p -auto-alt-ref 0 vp8-alpha.webm

ffmpeg -y -f lavfi -i "color=c=red@0.25:s=16x16:r=25:d=0.04,format=yuva420p" -frames:v 1 -an -c:v libvpx-vp9 -lossless 1 -pix_fmt yuva420p -auto-alt-ref 0 vp9-alpha.webm

ffmpeg -y -f lavfi -i "color=c=red:s=16x16:r=25:d=0.04,format=yuv420p" -frames:v 1 -an -c:v libvpx-vp9 -lossless 1 -pix_fmt yuv420p -metadata:s:v:0 alpha_mode=1 vp9-declared-alpha-without-payload.webm
```

Encoder and muxer versions may produce different bytes; tests depend only on
the committed fixtures. Their alpha frames decode to a uniform `A=63`, which
the regression test checks for every pixel.
