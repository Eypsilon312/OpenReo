namespace OpenReo.Core.Models;

/// <summary>
/// Stream quality levels as offered by Reolink cameras.
/// The selected level is ALWAYS honoured – there is no automatic adaption.
/// </summary>
public enum StreamQuality
{
    /// <summary>Main stream – highest resolution ("Clear").</summary>
    Clear = 0,
    /// <summary>Medium resolution ("Balanced"). Default on first start.</summary>
    Mid = 1,
    /// <summary>Lowest resolution / sub stream ("Fluent").</summary>
    Low = 2,
}

/// <summary>Video decoder selection.</summary>
public enum HardwareAccelerationMode
{
    /// <summary>Try D3D11VA, then DXVA2, then fall back to software.</summary>
    Auto = 0,
    D3D11VA = 1,
    DXVA2 = 2,
    /// <summary>NVIDIA NVDEC via CUDA (NVIDIA GPUs only).</summary>
    Cuda = 3,
    /// <summary>Pure CPU decoding.</summary>
    Software = 4,
}

/// <summary>RTSP transport protocol.</summary>
public enum RtspTransport
{
    Tcp = 0,
    Udp = 1,
}

/// <summary>Lifecycle state of a single RTSP stream.</summary>
public enum StreamStatus
{
    Idle = 0,
    Connecting = 1,
    Playing = 2,
    Reconnecting = 3,
    Stopped = 4,
}

public static class StreamQualityExtensions
{
    public static string DisplayName(this StreamQuality q) => q switch
    {
        StreamQuality.Clear => "Clear",
        StreamQuality.Mid => "Mid",
        StreamQuality.Low => "Low",
        _ => q.ToString(),
    };
}
