using System.Text.Json.Nodes;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Core.Reolink;

/// <summary>
/// Convenience calls on top of <see cref="ReolinkSession"/>.
/// </summary>
public static class ReolinkApiClient
{
    /// <summary>
    /// Returns the name configured in the camera (NVR: the channel name), or null if the
    /// camera does not offer the Reolink HTTP API or the credentials are wrong.
    /// </summary>
    /// <param name="channel">1-based channel number.</param>
    public static async Task<string?> GetDeviceNameAsync(string host, int httpPort, string user, string password, int channel, CancellationToken ct)
    {
        try
        {
            await using var s = await ReolinkSession.ConnectAsync(host, httpPort, user, password, ct);
            string? name = null;
            if (channel <= 1)
            {
                var r = await s.ExecuteAsync("GetDevName", new JsonObject { ["channel"] = 0 }, 0, ct);
                name = ReolinkSession.GetString(r.Value, "DevName", "name");
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                // NVR channels (and fallback): the OSD channel name.
                var r = await s.ExecuteAsync("GetOsd", new JsonObject { ["channel"] = Math.Max(0, channel - 1) }, 0, ct);
                name = ReolinkSession.GetString(r.Value, "Osd", "osdChannel", "name");
            }
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"Reolink name lookup for {host} failed: {ex.Message}");
            return null;
        }
    }
}
