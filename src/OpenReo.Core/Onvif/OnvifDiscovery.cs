using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Core.Onvif;

/// <summary>A device that answered a WS-Discovery probe.</summary>
public sealed record DiscoveredDevice(
    string Address,
    int OnvifPort,
    string DeviceServiceUrl,
    string? Name,
    string? Hardware,
    string? MacAddress,
    string? EndpointId)
{
    public bool IsLikelyReolink =>
        (Name?.Contains("reolink", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Hardware?.StartsWith("RL", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Hardware?.Contains("reolink", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// ONVIF device discovery via WS-Discovery (UDP multicast 239.255.255.250:3702).
/// The probe is sent on every active IPv4 interface so that cameras in all local
/// networks are found.
/// </summary>
public static class OnvifDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    private const string ProbeTemplate =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
        "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
        "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
        "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\" " +
        "xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">" +
        "<e:Header>" +
        "<w:MessageID>uuid:{0}</w:MessageID>" +
        "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
        "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
        "</e:Header>" +
        "<e:Body><d:Probe><d:Types>{1}</d:Types></d:Probe></e:Body>" +
        "</e:Envelope>";

    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, DiscoveredDevice>();
        var locals = GetLocalAddresses();
        Log.Info($"ONVIF discovery on {locals.Count} interface(s): {string.Join(", ", locals)}");
        await Task.WhenAll(locals.Select(ip => ProbeInterfaceAsync(ip, duration, found, ct)));
        return found.Values
            .OrderBy(d => IPAddress.TryParse(d.Address, out var a) ? BitConverter.ToUInt32(a.GetAddressBytes().Reverse().ToArray(), 0) : uint.MaxValue)
            .ToList();
    }

    private static List<IPAddress> GetLocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.SupportsMulticast) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    result.Add(ua.Address);
        }
        if (result.Count == 0) result.Add(IPAddress.Any);
        return result;
    }

    private static async Task ProbeInterfaceAsync(IPAddress local, TimeSpan duration,
        ConcurrentDictionary<string, DiscoveredDevice> found, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, 0));
            if (!local.Equals(IPAddress.Any))
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

            // Two probe variants – some devices only answer one of them. Each is sent twice (UDP may drop).
            var probes = new[] { "dn:NetworkVideoTransmitter", "tds:Device" }
                .Select(t => Encoding.UTF8.GetBytes(string.Format(ProbeTemplate, Guid.NewGuid(), t)))
                .ToArray();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);

            var receiveTask = ReceiveLoopAsync(udp, found, cts.Token);
            for (int round = 0; round < 2 && !cts.IsCancellationRequested; round++)
            {
                foreach (var p in probes)
                    await udp.SendAsync(p, p.Length, MulticastEndpoint);
                try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { }
            }
            await receiveTask;
        }
        catch (Exception ex)
        {
            Log.Warn($"ONVIF probe on {local} failed: {ex.Message}");
        }
    }

    private static async Task ReceiveLoopAsync(UdpClient udp, ConcurrentDictionary<string, DiscoveredDevice> found, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            try
            {
                foreach (var dev in ParseProbeMatches(r.Buffer, r.RemoteEndPoint.Address))
                    found.AddOrUpdate(dev.Address, dev, (_, old) => old.Name == null ? dev : old);
            }
            catch (Exception ex)
            {
                Log.Debug($"Invalid discovery answer from {r.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    internal static IEnumerable<DiscoveredDevice> ParseProbeMatches(byte[] data, IPAddress sender)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(data));
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = match.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? "";
            var scopes = (match.Elements().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var endpoint = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value;

            // Prefer the XAddr that matches the sender address, otherwise the first IPv4 one.
            Uri? best = null;
            foreach (var s in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) continue;
                if (u.HostNameType != UriHostNameType.IPv4) continue;
                if (best == null || u.Host == sender.ToString()) best = u;
            }
            var address = best?.Host ?? sender.ToString();
            var port = best?.Port ?? 80;
            var url = best?.ToString() ?? $"http://{address}/onvif/device_service";

            yield return new DiscoveredDevice(
                address, port, url,
                ScopeValue(scopes, "name"),
                ScopeValue(scopes, "hardware"),
                ScopeValue(scopes, "MAC") ?? ScopeValue(scopes, "mac"),
                endpoint);
        }
    }

    private static string? ScopeValue(IEnumerable<string> scopes, string key)
    {
        var prefix = $"onvif://www.onvif.org/{key}/";
        var s = scopes.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return s == null ? null : Uri.UnescapeDataString(s[prefix.Length..]).Replace('_', ' ').Trim();
    }
}
