namespace VL.FFmpeg.Internal.Decoding;

/// <summary>Optional native I/O limits. Local-file callers retain FFmpeg defaults.</summary>
internal sealed record MediaInputOptions(TimeSpan OpenTimeout, TimeSpan ReadTimeout,
    IReadOnlyDictionary<string, string>? NativeOptions = null)
{
    public static MediaInputOptions? WithNetworkDefaults(string source, MediaInputOptions? options)
    {
        var defaults = ForNetwork(source);
        if (options is null) return defaults;
        if (defaults?.NativeOptions is not { } values) return options;
        var native = new Dictionary<string, string>(values);
        if (options.NativeOptions is { } overrides)
            foreach (var (key, value) in overrides) native[key] = value;
        return options with { NativeOptions = native };
    }

    public static MediaInputOptions? ForNetwork(string source, string rtspTransport = "tcp")
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "rtsp")) return null;
        var native = new Dictionary<string, string>();
        if (uri.Scheme == "rtsp")
        {
            native["rtsp_transport"] = rtspTransport;
            native["timeout"] = "5000000";
        }
        else
        {
            native["rw_timeout"] = "5000000";
            // An HTTP URL can redirect to HTTPS; trust checks follow the entire request.
            native["tls_verify"] = "1";
        }
        return new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), native);
    }
}
