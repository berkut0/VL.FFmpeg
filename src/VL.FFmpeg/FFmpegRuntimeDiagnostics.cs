using VL.Core.Import;
using VL.FFmpeg.Internal.Interop;
using VL.Model;

namespace VL.FFmpeg.Nodes;

/// <summary>
/// Diagnoses the pinned FFmpeg 8.1 native runtime used by VL.FFmpeg.
/// </summary>
public static class FFmpegRuntimeDiagnostics
{
    /// <summary>
    /// Resolves and probes the native libraries without changing the process DLL search path.
    /// </summary>
    public static void Query(
        out bool available,
        out string version,
        out string resolvedPath,
        out string status,
        [Pin(Visibility = PinVisibility.Optional)] string nativePath = "")
    {
        var result = FFmpegRuntime.Probe(nativePath);
        available = result.Available;
        version = result.Version;
        resolvedPath = result.NativeDirectory;
        status = result.Status;
    }
}
