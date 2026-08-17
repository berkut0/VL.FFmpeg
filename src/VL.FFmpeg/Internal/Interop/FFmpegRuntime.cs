using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Interop;

internal sealed record FFmpegRuntimeProbe(
    bool Available,
    string Version,
    string NativeDirectory,
    string Status);

internal static class FFmpegRuntime
{
    private static readonly object SyncRoot = new();
    private static FFmpegRuntimeProbe? _terminalResult;

    public static FFmpegRuntimeProbe Probe(string? explicitPath = null)
    {
        lock (SyncRoot)
        {
            if (_terminalResult is not null)
                return CheckRequestedPath(_terminalResult, explicitPath);

            if (!OperatingSystem.IsWindows())
                return new(false, string.Empty, string.Empty, "The current runtime is Windows x64 only.");

            if (!Environment.Is64BitProcess)
                return new(false, string.Empty, string.Empty, "The FFmpeg runtime requires an x64 process.");

            if (!NativeRuntimePathResolver.TryResolve(
                    explicitPath,
                    typeof(FFmpegRuntime).Assembly,
                    out var nativeDirectory,
                    out var pathError))
            {
                return new(false, string.Empty, string.Empty, pathError);
            }

            try
            {
                if (DynamicallyLoadedBindings.FunctionResolver is not null)
                {
                    _terminalResult = new(
                        false,
                        string.Empty,
                        nativeDirectory,
                        "The relocated FFmpeg binding was initialized before VL.FFmpeg installed its pinned resolver.");
                    return _terminalResult;
                }

                DynamicallyLoadedBindings.FunctionResolver = new PinnedFunctionResolver(nativeDirectory);
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

                // Triggers the generated binding initializer only after our resolver is installed.
                ffmpeg.RootPath = nativeDirectory;

                var version = ffmpeg.av_version_info();
                RequireMajor("avutil", ffmpeg.avutil_version(), 60);
                RequireMajor("swresample", ffmpeg.swresample_version(), 6);
                RequireMajor("avcodec", ffmpeg.avcodec_version(), 62);
                RequireMajor("avformat", ffmpeg.avformat_version(), 62);
                RequireMajor("swscale", ffmpeg.swscale_version(), 9);

                _terminalResult = new(
                    true,
                    version,
                    nativeDirectory,
                    $"FFmpeg {version}; ABI avcodec/avformat 62, avutil 60, swresample 6, swscale 9.");
                return _terminalResult;
            }
            catch (Exception exception)
            {
                // Generated delegates and any successfully loaded modules keep native
                // pointers. Switching roots after a partial load would be unsafe.
                _terminalResult = new(
                    false,
                    string.Empty,
                    nativeDirectory,
                    $"FFmpeg runtime initialization failed: {exception.GetType().Name}: {exception.Message}");
                return _terminalResult;
            }
        }
    }

    private static FFmpegRuntimeProbe CheckRequestedPath(
        FFmpegRuntimeProbe terminalResult,
        string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
            return terminalResult;

        var requestedPath = Path.GetFullPath(explicitPath, AppContext.BaseDirectory);
        if (string.Equals(
                requestedPath,
                terminalResult.NativeDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return terminalResult;
        }

        return new(
            false,
            terminalResult.Version,
            terminalResult.NativeDirectory,
            $"FFmpeg is already pinned to '{terminalResult.NativeDirectory}' and cannot switch to '{requestedPath}' in-process.");
    }

    private static void RequireMajor(string library, uint version, int expectedMajor)
    {
        var actualMajor = (int)(version >> 16);
        if (actualMajor != expectedMajor)
        {
            throw new NotSupportedException(
                $"{library} ABI major is {actualMajor}; expected {expectedMajor}.");
        }
    }
}

