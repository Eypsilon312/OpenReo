using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace OpenReo.Core.Onvif;

public sealed class OnvifException : Exception
{
    public bool IsAuthError { get; }
    public OnvifException(string message, bool isAuthError = false, Exception? inner = null) : base(message, inner)
    {
        IsAuthError = isAuthError;
    }
}

public sealed record OnvifDeviceInfo(string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber, string? HardwareId);

public sealed record OnvifProfile(string Token, string Name, string? Encoding, int Width, int Height, int FrameRate, int BitrateKbps)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Minimal ONVIF SOAP client (Device + Media service) with WS-UsernameToken
/// digest authentication and HTTP digest as fallback.
/// Only the calls required for camera setup are implemented.
/// </summary>
public sealed class OnvifClient : IDisposable
{
    private const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
    private const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
    private const string NsSchema = "http://www.onvif.org/ver10/schema";

    private readonly HttpClient _http;
    private readonly string _host;
    private readonly string _deviceUrl;
    private readonly string _user;
    private readonly string _password;
    private TimeSpan _clockOffset = TimeSpan.Zero;
    private string? _mediaUrl;

    public OnvifClient(string host, int port, string user, string password, TimeSpan? timeout = null)
    {
        _host = host;
        _user = user ?? "";
        _password = password ?? "";
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        _deviceUrl = $"http://{h}:{port}/onvif/device_service";
        var handler = new HttpClientHandler
        {
            Credentials = string.IsNullOrEmpty(_user) ? null : new NetworkCredential(_user, _password),
            PreAuthenticate = false,
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
    }

    /// <summary>Reads the camera clock so that the WS-Security timestamps are accepted.</summary>
    public async Task SyncClockAsync(CancellationToken ct)
    {
        try
        {
            var doc = await SendAsync(_deviceUrl, $"{NsDevice}/GetSystemDateAndTime",
                $"<GetSystemDateAndTime xmlns=\"{NsDevice}\"/>", authenticate: false, ct);
            var utc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
            if (utc == null) return;
            int V(string name) => int.Parse(utc.Descendants().First(e => e.Name.LocalName == name).Value, CultureInfo.InvariantCulture);
            var camTime = new DateTime(V("Year"), V("Month"), V("Day"), V("Hour"), V("Minute"), V("Second"), DateTimeKind.Utc);
            _clockOffset = camTime - DateTime.UtcNow;
        }
        catch
        {
            _clockOffset = TimeSpan.Zero;
        }
    }

    public async Task<OnvifDeviceInfo> GetDeviceInformationAsync(CancellationToken ct)
    {
        var doc = await SendAsync(_deviceUrl, $"{NsDevice}/GetDeviceInformation",
            $"<GetDeviceInformation xmlns=\"{NsDevice}\"/>", true, ct);
        string? V(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim();
        return new OnvifDeviceInfo(V("Manufacturer"), V("Model"), V("FirmwareVersion"), V("SerialNumber"), V("HardwareId"));
    }

    private async Task<string> GetMediaUrlAsync(CancellationToken ct)
    {
        if (_mediaUrl != null) return _mediaUrl;
        try
        {
            var doc = await SendAsync(_deviceUrl, $"{NsDevice}/GetCapabilities",
                $"<GetCapabilities xmlns=\"{NsDevice}\"><Category>Media</Category></GetCapabilities>", true, ct);
            var media = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media");
            var xaddr = media?.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value?.Trim();
            if (!string.IsNullOrEmpty(xaddr) && Uri.TryCreate(xaddr, UriKind.Absolute, out var u))
            {
                // The camera may report an internal address – always use the host we connected to.
                var b = new UriBuilder(u) { Host = new Uri(_deviceUrl).Host };
                _mediaUrl = b.Uri.ToString();
                return _mediaUrl;
            }
        }
        catch (OnvifException ex) when (ex.IsAuthError) { throw; }
        catch { /* fall through to device service url */ }
        _mediaUrl = _deviceUrl;
        return _mediaUrl;
    }

    public async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken ct)
    {
        var url = await GetMediaUrlAsync(ct);
        var doc = await SendAsync(url, $"{NsMedia}/GetProfiles", $"<GetProfiles xmlns=\"{NsMedia}\"/>", true, ct);
        var list = new List<OnvifProfile>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = p.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token)) continue;
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? token;
            var vec = p.Elements().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            int I(XElement? parent, string n) =>
                int.TryParse(parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var res = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolution");
            var rate = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "RateControl");
            var enc = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value;
            list.Add(new OnvifProfile(token, name, enc, I(res, "Width"), I(res, "Height"), I(rate, "FrameRateLimit"), I(rate, "BitrateLimit")));
        }
        return list;
    }

    public async Task<Uri> GetStreamUriAsync(string profileToken, CancellationToken ct)
    {
        var url = await GetMediaUrlAsync(ct);
        var body =
            $"<GetStreamUri xmlns=\"{NsMedia}\">" +
            $"<StreamSetup><Stream xmlns=\"{NsSchema}\">RTP-Unicast</Stream>" +
            $"<Transport xmlns=\"{NsSchema}\"><Protocol>RTSP</Protocol></Transport></StreamSetup>" +
            $"<ProfileToken>{SecurityElement.Escape(profileToken)}</ProfileToken></GetStreamUri>";
        var doc = await SendAsync(url, $"{NsMedia}/GetStreamUri", body, true, ct);
        var uri = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
            throw new OnvifException("Kamera lieferte keine Stream-URI.");
        return u;
    }

    private async Task<XDocument> SendAsync(string url, string action, string body, bool authenticate, CancellationToken ct)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            (authenticate && !string.IsNullOrEmpty(_user) ? "<s:Header>" + BuildSecurityHeader() + "</s:Header>" : "") +
            "<s:Body xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
            body + "</s:Body></s:Envelope>";

        using var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{action}\"");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(url, content, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OnvifException($"ONVIF-Zeitüberschreitung ({_host}).", false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OnvifException($"ONVIF nicht erreichbar ({_host}): {ex.Message}", false, ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            XDocument? doc = null;
            try { doc = XDocument.Parse(text); } catch { /* not xml */ }

            var fault = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault != null || !resp.IsSuccessStatusCode)
            {
                var reason = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                             ?? fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                             ?? resp.ReasonPhrase ?? "Unbekannter Fehler";
                var subcode = string.Join(" ", fault?.Descendants().Where(e => e.Name.LocalName == "Value").Select(v => v.Value) ?? Array.Empty<string>());
                bool auth = resp.StatusCode == HttpStatusCode.Unauthorized
                            || subcode.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("auth", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("password", StringComparison.OrdinalIgnoreCase);
                throw new OnvifException(auth ? "ONVIF-Anmeldung fehlgeschlagen (Benutzername/Passwort prüfen)." : $"ONVIF-Fehler: {reason}", auth);
            }
            return doc ?? throw new OnvifException("Ungültige ONVIF-Antwort.");
        }
    }

    private string BuildSecurityHeader()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = (DateTime.UtcNow + _clockOffset).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var pwBytes = Encoding.UTF8.GetBytes(_password);
        var all = new byte[nonce.Length + createdBytes.Length + pwBytes.Length];
        Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, all, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(pwBytes, 0, all, nonce.Length + createdBytes.Length, pwBytes.Length);
        var digest = Convert.ToBase64String(SHA1.HashData(all));

        return
            "<Security s:mustUnderstand=\"1\" xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
            "<UsernameToken>" +
            $"<Username>{SecurityElement.Escape(_user)}</Username>" +
            "<Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" + digest + "</Password>" +
            "<Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" + Convert.ToBase64String(nonce) + "</Nonce>" +
            "<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" + created + "</Created>" +
            "</UsernameToken></Security>";
    }

    public void Dispose() => _http.Dispose();
}
