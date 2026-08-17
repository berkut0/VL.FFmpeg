using System.Reflection;

namespace VL.FFmpeg.Internal.Interop;

internal static class NativeRuntimePathResolver
{
    public const string EnvironmentVariable = "VL_FFMPEG_NATIVE_PATH";

    public static readonly string[] RequiredFiles =
    [
        "avcodec-62.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll"
    ];

    public static bool TryResolve(
        string? explicitPath,
        Assembly assembly,
        out string nativeDirectory,
        out string error)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ValidateSingle(explicitPath, "explicit path", out nativeDirectory, out error);

        var environmentPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return ValidateSingle(
                environmentPath,
                $"environment variable {EnvironmentVariable}",
                out nativeDirectory,
                out error);

        var assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? string.Empty;
        var appDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDirectory, "runtimes", "win-x64", "native"),
            appDirectory,
            Path.Combine(assemblyDirectory, "runtimes", "win-x64", "native"),
            assemblyDirectory,
            Path.Combine(assemblyDirectory, "..", "..", "runtimes", "win-x64", "native")
        };

        var checkedPaths = new List<string>(candidates.Length);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!uniquePaths.Add(fullPath))
                continue;

            checkedPaths.Add(fullPath);
            if (HasRequiredFiles(fullPath, out _))
            {
                nativeDirectory = fullPath;
                error = string.Empty;
                return true;
            }
        }

        nativeDirectory = string.Empty;
        error = $"FFmpeg 8.1 native runtime was not found. Checked: {string.Join("; ", checkedPaths)}";
        return false;
    }

    private static bool ValidateSingle(
        string path,
        string source,
        out string nativeDirectory,
        out string error)
    {
        var fullPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        if (HasRequiredFiles(fullPath, out var missingFile))
        {
            nativeDirectory = fullPath;
            error = string.Empty;
            return true;
        }

        nativeDirectory = string.Empty;
        error = $"FFmpeg native {source} '{fullPath}' is invalid; missing '{missingFile}'.";
        return false;
    }

    private static bool HasRequiredFiles(string path, out string missingFile)
    {
        if (!Directory.Exists(path))
        {
            missingFile = "directory";
            return false;
        }

        foreach (var filename in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(path, filename)))
            {
                missingFile = filename;
                return false;
            }
        }

        missingFile = string.Empty;
        return true;
    }
}

