# Network playback validation

Validated 2026-10-07 on Windows x64, .NET 8, bundled FFmpeg `9b6c8969e0`.
The standard Release suite passes 189 tests. Explicit trusted-HTTPS recording
and Live audio-continuity tests also pass. Native diagnostics about intentionally lost packets, rejected
certificates and cancelled requests are expected in these fault-injection tests.

| Scenario | Result |
| --- | --- |
| HTTP recording with Range support | Paused seek, resume and loop pass; tests observe real nonzero Range offsets |
| Server without Range support | Forward seek can read sequentially; backward seek reports failure instead of hanging or presenting the wrong frame |
| Truncated HTTP response | Ordinary player reports Faulted; explicit Open of the same URL restores playback and subsequent seek |
| URL change during stalled HTTP seek | Old operation cancels; replacement source presents its first frame |
| Network seek deadline | Stalled response observes the configured deadline, independently of explicit cancellation |
| TLS certificate trust | Untrusted certificate rejected directly, with custom deadlines, after HTTP redirect, and after a redirect on a later Range request |
| Trusted HTTPS recording | Public W3C H.264/AAC sample passes pause, seek, resume and loop with certificate verification enabled |
| RTSP server disconnect, TCP and UDP | Live node reconnects and presents frames without seeking |
| RTSP Basic authentication | Correct credentials play; wrong credentials and SETUP 403 are terminal, without a retry storm |
| Stalled RTSP DESCRIBE | Changing URL cancels the handshake and opens the replacement source |
| UDP packet loss/reordering | VP8 fragmented frames continue without reconnect; H.264 FU-A fragment loss is followed by continued playback across the next repeated GOP |
| A/V timestamp reset | Video and audible audio resume after a backward source-time jump, on the same connection |
| Fast input with realtime audio consumption | VP8/L16, H.264/L16 and 1 fps video deliver the continuous test tone without audio queue drops or silent blocks after startup |
| Reported Sintel Live audio interruption | Before correction: 29 queue discontinuities in an 8-second reproduction. After correction: zero discontinuities and zero underruns over the same interval |
| Ordinary player on RTSP live source | Initial playback works; a server-rejected seek becomes an explicit error. Use the Live node for this source type |

The TLS fixture uses a generated certificate and a temporary imported Windows
private key, disposed with the server. It never installs a trusted certificate.
A positive control with verification explicitly disabled only in the test proves
that the fixture serves valid media before checking rejection. The suite needs
normal Windows Schannel credentials; a restricted sandbox can prevent TLS setup.

Commands:

```powershell
dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release
dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release --filter FullyQualifiedName~TrustedHttpsRecordingSupportsPauseSeekAndLoop
dotnet test tests/VL.FFmpeg.Tests/VL.FFmpeg.Tests.csproj -c Release --filter FullyQualifiedName~SintelLiveAudioDoesNotOverflow
```

The explicit HTTPS test requires external access. The H.264 RTP fixture uses the
installed Gamma reference MP4 and is skipped where that asset is unavailable.
No additional runtime dependency or packaged test server is introduced.

## Support limits

- Ordinary playback is intended for network recordings. Seek/loop depend on the
  resource and server. It has bounded network operations but no automatic
  connection-recovery policy; Live owns automatic retry.
- RTSP tests cover software H.264/VP8 decode, RTP loss and Basic authentication.
  They do not certify every camera, Digest-auth variant, HEVC stream or hardware
  decoder under packet loss. Successful frame delivery is not a pixel-fidelity
  guarantee for damaged compressed frames.
- Native deadlines are cooperative; OS DNS and driver calls can have their own
  cancellation limits. Bounded local queues do not bound upstream buffering.
- HLS/DASH, SRT and DVR remain outside the Live node's supported scope.
- C# and native protocol tests do not replace owner validation in Gamma with
  Skia/Stride and an exported application. No `.vl` document was read or changed
  during this validation, and no NuGet publication was performed.
