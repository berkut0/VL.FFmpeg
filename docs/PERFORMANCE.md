# Playback measurements and verification

These development measurements record specific workloads, not guaranteed rates
for all files or machines. The owner's large HAP file remained visibly limited
by input throughput; subjective improvement was modest. No codec-specific
workaround or automatic decoder-threading change was retained.

## Real Gamma investigation — 2026-10-07

Gamma 7.4 (`7.4-0015-ge855ca6589`), Windows, eight logical processors, FFmpeg
`n8.1.2-34-g9b6c8969e0-20260812`. Source: 8192×1080 HAP, 50 fps, 299.94 seconds,
58.29 GB. Output: shared consumer-device D3D11 linear RGBA16F. Trace identified
the workspace DLL and reported no lost events.

- A seek to 150 seconds retained roughly 50 fps for another 25 seconds.
  The first large read stall (595 ms) preceded automatic recovery.
- In an 80-second slow interval, video reads occupied 79.58 seconds and supplied
  59.51 MiB/s. There were 918 decoded frames, 665 explicit expiry drops and 224
  presentations. Average conversion time was 10.93 ms; no resource waits occurred
  in that interval.
- Owned GPU storage stayed at 810 MiB; process private memory remained around
  1.9–2.1 GiB. No accumulating worker backlog was observed. Returning to zero
  restored roughly 50 fps within the same open session; slower reading recurred
  later in the first half too.
- Separate read-only raw-file measurements bypassing Windows data caching gave
  about 53–67 MiB/s at the beginning and 64–65 MiB/s near 200 seconds on the
  `WDC WD20SPZX-08UA7` SATA drive. Cached reads exceeded 1 GiB/s. Near 200 seconds,
  the source requires roughly 200 MiB/s. Other processes' disk load was not isolated.

The read/demux stage was the initiating bottleneck. The previous 250 ms progress
rule additionally discarded useful output. The trace alone cannot isolate every
filesystem, filter-driver or cache effect, nor prove the absence of all native
or GPU-driver leaks.

## Scheduler comparison — 2026-10-07

Baseline: commit `5750473` plus the already-developed pause/status diagnostics
fixes, instrumented DLL MVID `fffca243-22f4-45b7-997e-40a90f0dd2c3`.
Scheduler candidate: MVID `4aee3f58-2d90-4dc4-b14b-758db29044c1`. These measurements
precede final trace removal and the low-frame-rate preroll fix.

One standalone consumer on the same machine acquires/releases actual GPU frames,
without a Gamma rendering window. Tracing is disabled. A test-only wrapper around
the native read delegate enforces at least 70 ms per video packet during seconds
3–13 (observed means 77.2–77.7 ms because of sleep scheduling). A manual seek to
1 second occurs at elapsed 7; input becomes unrestricted at 13. Native reads
themselves remain well below the injected delay. No hook enters the library.

Slow measurements cover seconds 8–13 after the first delivered frame. Gaps include
adjacent presentations spanning the measurement boundary. Recovery rate covers
seconds 15–19. New delivered frames are counted, not monitor refreshes.

| Metric | HAP baseline | HAP scheduler | H.264 baseline | H.264 scheduler |
| --- | ---: | ---: | ---: | ---: |
| Delivered frames/s under restriction | 2.0 | 9.4 | 1.2 | 4.4 |
| Presentation gap p95, ms | 921 | 391 | 3476 | 1003 |
| Longest overlapping gap, ms | 921 | 620 | 3476 | 3169 |
| Presentation lag p95, seconds | 1.542 | 1.562 | 2.702 | 2.748 |
| First frame after seek, ms | 696 | 595 | 2604 | 2600 |
| Delivered frames/s after restriction | 50.0 | 50.0 | 24.5 | 24.5 |

HAP is the owner's large source above; H.264 is Gamma's `Birds_H264.mp4` at
25 fps. Unrestricted HAP stays near 50 fps with gap p95 around 31.5 ms.
This controls packet service time but does not reproduce physical disk seek and
read-ahead effects. It demonstrates more useful output, not elimination of
latency or a guarantee of smooth playback under sustained bandwidth shortage.

Two large HAP players sharing one device and the 1 GiB GPU budget were measured
for eight seconds after four seconds of warmup. Aggregate output was 87.5 fps
before and approximately 100 fps with the scheduler (100.625 and 100.875 in the
two runs). Slightly exceeding 100 reflects finite-window catch-up. An experimental
intra/slice-threading preference fell to 81.625 and 76.75 fps despite reducing
single-player seek latency; it was rejected. Native decoder defaults remain.

## Earlier CPU-pool baseline

The initial realtime refactor was compared with `ddfcaab` on identical cached
Y4M inputs, not compressed-media/Gamma playback. Forty warmed isolated 4K CPU
conversions reduced per-frame managed allocation from approximately 33/66 MB
(BGRA/linear) to about 200 bytes; linear conversion remained around 57 ms.
The final four-player 4K comparison used one second of warmup and five measured
seconds: 843 versus 851 frames, managed allocation 27.9 GB versus 5.33 MB, and
Gen2 collections 138 versus zero. These are historical measurements of pooling.

## Retained regression coverage

Run `dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release`.
Coverage includes mixed H.264/VP8/VP9/raw playback at 1/4/12 players, rapid seeks,
pause preview, low-frame-rate seek/loop, file and dimension changes, open-error
recovery, CPU/GPU handles retained through shutdown, worker-wait generation
changes, alpha/color, audio drain/reconfiguration and strict Hardware behavior.

Temporary trace infrastructure, raw logs and benchmark executables were removed
after investigation. This compact record preserves conditions and results.
Fresh Gamma/Skia/Stride, binary NuGet and exported-runtime release validation
remain separate owner checks; .NET tests do not establish package validity.
