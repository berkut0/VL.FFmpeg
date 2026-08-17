namespace VL.FFmpeg.Nodes;

/// <summary>
/// Path used to decode and deliver the current video frame.
/// </summary>
public enum FFmpegDecodePath
{
    /// <summary>No decoder path has been selected.</summary>
    None,

    /// <summary>Frames are decoded and converted in CPU memory.</summary>
    Software,

    /// <summary>D3D11VA uses the consumer device and delivers a GPU texture.</summary>
    D3D11VaGpuTexture
}
