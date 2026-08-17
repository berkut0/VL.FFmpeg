using System.Runtime.InteropServices;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Interop;

/// <summary>
/// Resolves the relocated FFmpeg.AutoGen delegates from one immutable directory.
/// Loaded libraries intentionally live for the process lifetime because generated
/// delegates retain their function pointers.
/// </summary>
internal sealed class PinnedFunctionResolver : IFunctionResolver
{
    private static readonly IReadOnlyDictionary<string, int> LibraryMajors
        = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["avcodec"] = 62,
            ["avformat"] = 62,
            ["avutil"] = 60,
            ["swresample"] = 6,
            ["swscale"] = 9
        };

    private static readonly IReadOnlyDictionary<string, string[]> Dependencies
        = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["avcodec"] = ["avutil", "swresample"],
            ["avformat"] = ["avcodec", "avutil"],
            ["avutil"] = [],
            ["swresample"] = ["avutil"],
            ["swscale"] = ["avutil"]
        };

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, nint> _handles = new(StringComparer.Ordinal);

    public PinnedFunctionResolver(string nativeDirectory)
    {
        NativeDirectory = Path.GetFullPath(nativeDirectory);
    }

    public string NativeDirectory { get; }

    public T GetFunctionDelegate<T>(
        string libraryName,
        string functionName,
        bool throwOnError = true)
    {
        var handle = GetOrLoadLibrary(libraryName, throwOnError);
        if (handle == 0)
            return default!;

        if (!NativeLibrary.TryGetExport(handle, functionName, out var functionPointer))
        {
            if (throwOnError)
                throw new EntryPointNotFoundException(
                    $"FFmpeg entry point '{functionName}' was not found in '{libraryName}'.");
            return default!;
        }

        if (!typeof(Delegate).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException($"'{typeof(T)}' is not a delegate type.");

        return (T)(object)Marshal.GetDelegateForFunctionPointer(functionPointer, typeof(T));
    }

    private nint GetOrLoadLibrary(string libraryName, bool throwOnError)
    {
        lock (_syncRoot)
        {
            if (_handles.TryGetValue(libraryName, out var existing))
                return existing;

            if (!LibraryMajors.TryGetValue(libraryName, out var major))
            {
                if (throwOnError)
                    throw new DllNotFoundException(
                        $"FFmpeg library '{libraryName}' is outside the pinned runtime manifest.");
                return 0;
            }

            foreach (var dependency in Dependencies[libraryName])
                GetOrLoadLibrary(dependency, throwOnError: true);

            var filename = $"{libraryName}-{major}.dll";
            var absolutePath = Path.Combine(NativeDirectory, filename);
            try
            {
                var handle = NativeLibrary.Load(absolutePath);
                _handles.Add(libraryName, handle);
                return handle;
            }
            catch when (!throwOnError)
            {
                return 0;
            }
        }
    }
}

