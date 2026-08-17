namespace VL.FFmpeg.Nodes;

/// <summary>
/// Selects how FFmpeg decodes video frames.
/// </summary>
public enum FFmpegDecodeMode
{
    /// <summary>Use D3D11VA when the consumer supplies a D3D11 device, otherwise use software.</summary>
    Auto,

    /// <summary>Always decode to CPU-backed BGRA8 frames.</summary>
    Software,

    /// <summary>Require D3D11VA GPU-backed frames and fault when unavailable.</summary>
    Hardware
}
