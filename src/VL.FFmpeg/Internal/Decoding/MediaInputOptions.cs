namespace VL.FFmpeg.Internal.Decoding;

/// <summary>Optional native I/O limits. Local-file callers retain FFmpeg defaults.</summary>
internal sealed record MediaInputOptions(TimeSpan OpenTimeout, TimeSpan ReadTimeout,
    IReadOnlyDictionary<string, string>? NativeOptions = null);
