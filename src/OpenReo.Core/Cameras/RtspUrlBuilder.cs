using OpenReo.Core.Models;

namespace OpenReo.Core.Cameras;

/// <summary>
/// Builds RTSP URLs for a camera / quality combination.
/// </summary>
public static class RtspUrlBuilder
{
    /// <summary>Reolink default path candidates (tried in this order during detection).</summary>
    public static IReadOnlyList<string> ReolinkCandidates(StreamQuality q, int channel)
    {
        var ch = channel.ToString("D2");
        var suffix = q switch { StreamQuality.Clear => "main", StreamQuality.Mid => "ext", _ => "sub" };
        return new[] { $"h264Preview_{ch}_{suffix}", $"Preview_{ch}_{suffix}", $"h265Preview_{ch}_{suffix}" };
    }

    /// <summary>Fallback path when nothing could be detected.</summary>
    public static string ReolinkDefault(StreamQuality q, int channel)
    {
        var ch = channel.ToString("D2");
        return q == StreamQuality.Clear ? $"h264Preview_{ch}_main" : $"h264Preview_{ch}_sub";
    }

    /// <summary>
    /// Reolink HTTP-FLV stream (same transport the Reolink apps use internally – usually much more
    /// even than Reolink's RTSP server). Requires the camera's HTTP(S) port; newer firmware needs RTMP enabled.
    /// </summary>
    public static string ReolinkFlv(CameraConfig cam, StreamQuality q, bool includeCredentials = true)
    {
        var host = cam.Host.Contains(':') && !cam.Host.StartsWith('[') ? $"[{cam.Host}]" : cam.Host;
        var scheme = cam.HttpPort == 443 ? "https" : "http";
        var port = cam.HttpPort is 80 or 443 ? "" : ":" + cam.HttpPort;
        var stream = q switch { StreamQuality.Clear => "main", StreamQuality.Mid => "ext", _ => "sub" };
        var pw = includeCredentials ? Uri.EscapeDataString(cam.Password ?? "") : "***";
        return $"{scheme}://{host}{port}/flv?port=1935&app=bcs&stream=channel{Math.Max(0, cam.Channel - 1)}_{stream}.bcs" +
               $"&user={Uri.EscapeDataString(cam.Username)}&password={pw}";
    }

    /// <summary>Full URL including credentials – never log or display this.</summary>
    public static string Build(CameraConfig cam, StreamQuality q) => Build(cam, cam.GetPath(q), includeCredentials: true);

    /// <summary>URL without password for display / logging.</summary>
    public static string BuildDisplay(CameraConfig cam, StreamQuality q) => Build(cam, cam.GetPath(q), includeCredentials: false);

    public static string Build(CameraConfig cam, string? path, bool includeCredentials)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) path = ReolinkDefault(StreamQuality.Low, cam.Channel);

        string userInfo = "";
        if (!string.IsNullOrEmpty(cam.Username))
        {
            userInfo = includeCredentials
                ? $"{Uri.EscapeDataString(cam.Username)}:{Uri.EscapeDataString(cam.Password ?? "")}@"
                : $"{Uri.EscapeDataString(cam.Username)}:***@";
        }

        if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            // Complete URL given: only insert credentials if none are present.
            if (Uri.TryCreate(path, UriKind.Absolute, out var u))
            {
                if (!string.IsNullOrEmpty(u.UserInfo))
                {
                    if (includeCredentials) return path;
                    return path.Replace(u.UserInfo + "@", "***@");
                }
                var port = u.IsDefaultPort || u.Port <= 0 ? "" : ":" + u.Port;
                return $"{u.Scheme}://{userInfo}{u.Host}{port}{u.PathAndQuery}";
            }
            return path;
        }

        // Other protocols supported by FFmpeg (rtmp://, http:// FLV ...) or local files are used verbatim.
        if (path.Contains("://") || Path.IsPathFullyQualified(path))
            return path;

        var host = cam.Host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]"; // IPv6
        return $"rtsp://{userInfo}{host}:{cam.RtspPort}/{path.TrimStart('/')}";
    }
}
