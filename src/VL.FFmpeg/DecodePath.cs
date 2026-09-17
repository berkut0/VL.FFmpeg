namespace VL.FFmpeg.Nodes;

/// <summary>
/// Path used to decode and deliver the current video frame.
/// </summary>
public enum DecodePath
{
    /// <summary>No decoder path has been selected.</summary>
    None,

    /// <summary>Frames are decoded and converted in CPU memory.</summary>
    Software,

    /// <summary>Frames are decoded on the CPU and converted on the consumer D3D11 device.</summary>
    SoftwareGpuTexture,

    /// <summary>D3D11VA uses the consumer device and delivers a GPU texture.</summary>
    D3D11VaGpuTexture
}
