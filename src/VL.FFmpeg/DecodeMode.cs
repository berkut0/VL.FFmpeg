namespace VL.FFmpeg.Nodes;

/// <summary>
/// Selects how FFmpeg decodes video frames.
/// </summary>
public enum DecodeMode
{
    /// <summary>Use D3D11VA when the consumer supplies a D3D11 device, otherwise use software.</summary>
    Auto,

    /// <summary>Decode on the CPU. A D3D11 consumer may still receive a GPU-backed frame.</summary>
    Software,

    /// <summary>Require D3D11VA GPU-backed frames and fault when unavailable.</summary>
    Hardware
}
