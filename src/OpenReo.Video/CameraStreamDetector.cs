using OpenReo.Core.Cameras;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Core.Onvif;
using OpenReo.Core.Reolink;
using OpenReo.Core.Localization;

namespace OpenReo.Video;

/// <summary>Result of the automatic stream detection for a camera.</summary>
public sealed class StreamDetectionResult
{
    public Dictionary<StreamQuality, string> Paths { get; } = new();
    public Dictionary<StreamQuality, StreamProbeResult> Probes { get; } = new();
    public int? RtspPort { get; set; }
    public OnvifDeviceInfo? DeviceInfo { get; set; }

    /// <summary>Name configured in the camera (Reolink HTTP API), if available.</summary>
    public string? DeviceName { get; set; }
    public List<string> Messages { get; } = new();
    public bool OnvifSucceeded { get; set; }
    public bool AuthFailed { get; set; }
}

/// <summary>
/// Determines the RTSP paths for Clear / Mid / Low:
/// 1. via ONVIF (profiles sorted by resolution, highest = Clear),
/// 2. otherwise by probing the known Reolink path names.
/// This happens once while configuring a camera – during operation the configured
/// stream is always used as is (no automatic quality changes).
/// </summary>
public static class CameraStreamDetector
{
    public static async Task<StreamDetectionResult> DetectAsync(CameraConfig cam, RtspTransport transport,
        IProgress<string>? progress, CancellationToken ct)
    {
        var result = new StreamDetectionResult();
        try
        {
            await DetectViaOnvifAsync(cam, transport, result, progress, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (OnvifException ex)
        {
            result.AuthFailed |= ex.IsAuthError;
            result.Messages.Add(ex.Message);
        }
        catch (Exception ex)
        {
            result.Messages.Add("ONVIF: " + ex.Message);
        }

        // Camera name as configured in the Reolink web interface.
        progress?.Report(Lang.T("Lese Kameranamen …"));
        result.DeviceName = await ReolinkApiClient.GetDeviceNameAsync(cam.Host, cam.HttpPort, cam.Username, cam.Password, cam.Channel, ct);

        // Reolink path probing for whatever ONVIF did not deliver.
        var probeCam = cam.Clone();
        if (result.RtspPort is int p) probeCam.RtspPort = p;
        foreach (var q in new[] { StreamQuality.Clear, StreamQuality.Low, StreamQuality.Mid })
        {
            if (result.Paths.ContainsKey(q)) continue;
            foreach (var candidate in RtspUrlBuilder.ReolinkCandidates(q, cam.Channel))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(Lang.F("Prüfe RTSP-Pfad {0} …", candidate));
                var r = await StreamProbe.ProbeAsync(RtspUrlBuilder.Build(probeCam, candidate, true), transport, TimeSpan.FromSeconds(6), ct);
                if (r.Success)
                {
                    result.Paths[q] = candidate;
                    result.Probes[q] = r;
                    break;
                }
                if (r.Error != null && r.Error.StartsWith("Anmeldung")) { result.AuthFailed = true; break; }
                if (r.Error != null && (r.Error.StartsWith(Lang.T("Zeitüberschreitung")) || r.Error.StartsWith(Lang.T("Verbindung abgelehnt")))) break;
            }
        }

        // Cameras with only two streams: Mid uses the sub stream.
        if (!result.Paths.ContainsKey(StreamQuality.Mid) && result.Paths.TryGetValue(StreamQuality.Low, out var low))
        {
            result.Paths[StreamQuality.Mid] = low;
            if (result.Probes.TryGetValue(StreamQuality.Low, out var lp)) result.Probes[StreamQuality.Mid] = lp;
            result.Messages.Add(Lang.T("Kamera bietet keinen separaten mittleren Stream – Mid verwendet den Substream."));
        }

        if (result.AuthFailed) result.Messages.Add(Lang.T("Anmeldung fehlgeschlagen – Benutzername/Passwort prüfen."));
        return result;
    }

    private static async Task DetectViaOnvifAsync(CameraConfig cam, RtspTransport transport, StreamDetectionResult result,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Lang.T("Verbinde über ONVIF …"));
        using var onvif = new OnvifClient(cam.Host, cam.OnvifPort, cam.Username, cam.Password);
        await onvif.SyncClockAsync(ct);
        try { result.DeviceInfo = await onvif.GetDeviceInformationAsync(ct); }
        catch (OnvifException ex) when (ex.IsAuthError) { throw; }
        catch (Exception ex) { Log.Debug($"GetDeviceInformation failed: {ex.Message}"); }

        progress?.Report(Lang.T("Lese Stream-Profile …"));
        var profiles = await onvif.GetProfilesAsync(ct);
        var entries = new List<(OnvifProfile Profile, Uri Uri)>();
        foreach (var prof in profiles)
        {
            try
            {
                var uri = await onvif.GetStreamUriAsync(prof.Token, ct);
                if (entries.All(e => e.Uri.PathAndQuery != uri.PathAndQuery))
                    entries.Add((prof, uri));
            }
            catch (OnvifException ex) when (ex.IsAuthError) { throw; }
            catch (Exception ex) { Log.Debug($"GetStreamUri({prof.Token}) failed: {ex.Message}"); }
        }

        // NVR: only profiles of the selected channel (Reolink uses "..._01_..." / "Channel1" naming).
        var ch = cam.Channel.ToString("D2");
        var channelEntries = entries.Where(e => e.Uri.PathAndQuery.Contains($"_{ch}_") || e.Uri.PathAndQuery.Contains($"/{ch}")).ToList();
        if (channelEntries.Count > 0) entries = channelEntries;

        if (entries.Count == 0) throw new OnvifException(Lang.T("Keine Stream-Profile über ONVIF gefunden."));
        result.OnvifSucceeded = true;

        entries = entries.OrderByDescending(e => e.Profile.Pixels).ToList();
        result.RtspPort = entries[0].Uri.IsDefaultPort || entries[0].Uri.Port <= 0 ? 554 : entries[0].Uri.Port;
        string P(int i) => entries[i].Uri.PathAndQuery.TrimStart('/');

        result.Paths[StreamQuality.Clear] = P(0);
        if (entries.Count >= 3)
        {
            result.Paths[StreamQuality.Mid] = P(1);
            result.Paths[StreamQuality.Low] = P(entries.Count - 1);
        }
        else if (entries.Count == 2)
        {
            result.Paths[StreamQuality.Low] = P(1);
            // Reolink "Balanced" stream is often not published via ONVIF: try "_ext".
            var sub = P(1);
            if (sub.Contains("_sub", StringComparison.OrdinalIgnoreCase))
            {
                var ext = sub.Replace("_sub", "_ext", StringComparison.OrdinalIgnoreCase);
                progress?.Report(Lang.F("Prüfe mittleren Stream {0} …", ext));
                var probeCam = cam.Clone();
                probeCam.RtspPort = result.RtspPort.Value;
                var r = await StreamProbe.ProbeAsync(RtspUrlBuilder.Build(probeCam, ext, true), transport, TimeSpan.FromSeconds(6), ct);
                if (r.Success) { result.Paths[StreamQuality.Mid] = ext; result.Probes[StreamQuality.Mid] = r; }
            }
        }
        else
        {
            result.Messages.Add(Lang.T("Kamera bietet nur einen Stream – alle Qualitätsstufen verwenden diesen."));
            result.Paths[StreamQuality.Mid] = P(0);
            result.Paths[StreamQuality.Low] = P(0);
        }
        progress?.Report(Lang.F("ONVIF: {0} Stream-Profil(e) gefunden.", entries.Count));
    }
}
