using System.Text.Json.Serialization;

namespace OpenReo.Core.Models;

/// <summary>
/// Persistent configuration of a single camera.
/// </summary>
public sealed class CameraConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Kamera";

    /// <summary>IP address or host name.</summary>
    public string Host { get; set; } = "";

    public int RtspPort { get; set; } = 554;

    /// <summary>ONVIF (HTTP) port – Reolink default is 8000.</summary>
    public int OnvifPort { get; set; } = 8000;

    /// <summary>HTTP(S) port of the Reolink web API (used to read the camera name). 443 = HTTPS.</summary>
    public int HttpPort { get; set; } = 80;

    public string Username { get; set; } = "admin";

    /// <summary>Plain password – only held in memory, never written to disk.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>Password encrypted with Windows DPAPI (current user), Base64.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Channel number (1 for stand-alone cameras, 1..n on an NVR).</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Currently selected quality for this camera.</summary>
    public StreamQuality Quality { get; set; } = StreamQuality.Mid;

    /// <summary>
    /// RTSP path (e.g. "h264Preview_01_main") or a complete rtsp:// URL for the Clear stream.
    /// Credentials are inserted automatically.
    /// </summary>
    public string ClearPath { get; set; } = "";
    public string MidPath { get; set; } = "";
    public string LowPath { get; set; } = "";

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SerialNumber { get; set; }

    public string GetPath(StreamQuality q) => q switch
    {
        StreamQuality.Clear => ClearPath,
        StreamQuality.Mid => MidPath,
        _ => LowPath,
    };

    public void SetPath(StreamQuality q, string path)
    {
        switch (q)
        {
            case StreamQuality.Clear: ClearPath = path; break;
            case StreamQuality.Mid: MidPath = path; break;
            default: LowPath = path; break;
        }
    }

    /// <summary>Returns true when the connection relevant parts differ.</summary>
    public bool ConnectionEquals(CameraConfig other) =>
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && RtspPort == other.RtspPort
        && Username == other.Username
        && Password == other.Password
        && Channel == other.Channel
        && ClearPath == other.ClearPath
        && MidPath == other.MidPath
        && LowPath == other.LowPath;

    public CameraConfig Clone() => (CameraConfig)MemberwiseClone();

    /// <summary>
    /// True if the name is only a placeholder (empty, IP address, model / hardware id such as "IPC_...")
    /// and may be replaced by the name configured in the camera.
    /// </summary>
    public bool HasGenericName =>
        string.IsNullOrWhiteSpace(Name)
        || Name == "Kamera"
        || string.Equals(Name, Host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Name, Model, StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("IPC", StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("Reolink", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies all values (except the Id) from another instance.</summary>
    public void CopyFrom(CameraConfig o)
    {
        Name = o.Name; Host = o.Host; RtspPort = o.RtspPort; OnvifPort = o.OnvifPort; HttpPort = o.HttpPort;
        Username = o.Username; Password = o.Password; Channel = o.Channel; Quality = o.Quality;
        ClearPath = o.ClearPath; MidPath = o.MidPath; LowPath = o.LowPath;
        Manufacturer = o.Manufacturer; Model = o.Model; FirmwareVersion = o.FirmwareVersion; SerialNumber = o.SerialNumber;
    }
}
