using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Core.Reolink;

/// <summary>Response of a single Reolink API command.</summary>
public sealed class ReolinkResponse
{
    public string Cmd { get; init; } = "";
    public int Code { get; init; } = -1;
    public JsonObject? Value { get; init; }
    public JsonObject? Range { get; init; }
    public JsonObject? Initial { get; init; }
    public string? ErrorDetail { get; init; }
    public int RspCode { get; init; }
    public bool Ok => Code == 0;

    public override string ToString() => Ok ? $"{Cmd}: OK" : $"{Cmd}: {ErrorDetail ?? "Fehler"} ({RspCode})";
}

public sealed class ReolinkException : Exception
{
    public bool IsAuthError { get; }
    public ReolinkException(string message, bool isAuthError = false, Exception? inner = null) : base(message, inner)
    {
        IsAuthError = isAuthError;
    }
}

/// <summary>
/// Authenticated session against the Reolink HTTP API (cgi-bin/api.cgi).
/// Login → token; the token is renewed automatically when the camera reports an expired session.
/// Tries HTTP on the configured port first, then HTTPS (newer firmware).
/// </summary>
public sealed class ReolinkSession : IAsyncDisposable
{
    private const int RspCodeLoginRequired = -6;

    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;
    private string? _token;

    public string BaseUrl { get; }

    private ReolinkSession(HttpClient http, string baseUrl, string user, string password)
    {
        _http = http;
        BaseUrl = baseUrl;
        _user = user;
        _password = password;
    }

    public static async Task<ReolinkSession> ConnectAsync(string host, int httpPort, string user, string password, CancellationToken ct)
    {
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var bases = new List<string> { httpPort == 443 ? $"https://{h}" : $"http://{h}:{httpPort}" };
        if (httpPort != 443) bases.Add($"https://{h}:443");

        Exception? last = null;
        foreach (var baseUrl in bases)
        {
            var handler = new HttpClientHandler
            {
                // Reolink devices use self-signed certificates (local network only).
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                UseProxy = false,
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var session = new ReolinkSession(http, baseUrl, user, password);
            try
            {
                await session.LoginAsync(ct);
                return session;
            }
            catch (ReolinkException ex) when (ex.IsAuthError)
            {
                http.Dispose();
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                http.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                http.Dispose();
                Log.Debug($"Reolink API {baseUrl}: {ex.Message}");
            }
        }
        throw new ReolinkException("Reolink-Weboberfläche/API nicht erreichbar (HTTP-Port prüfen, HTTP/HTTPS in der Kamera aktivieren).", false, last);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var body = new JsonArray(new JsonObject
        {
            ["cmd"] = "Login",
            ["param"] = new JsonObject { ["User"] = new JsonObject { ["Version"] = "0", ["userName"] = _user, ["password"] = _password } },
        });
        var r = (await PostAsync("Login", body, ct)).FirstOrDefault();
        var token = GetString(r?.Value, "Token", "name");
        if (r == null || !r.Ok || string.IsNullOrEmpty(token))
        {
            var detail = r?.ErrorDetail ?? "keine Antwort";
            bool auth = r != null && (r.RspCode is -7 or -26 or -27 || detail.Contains("login", StringComparison.OrdinalIgnoreCase) || detail.Contains("password", StringComparison.OrdinalIgnoreCase));
            throw new ReolinkException(auth ? "Anmeldung an der Kamera fehlgeschlagen (Benutzername/Passwort prüfen)." : $"Anmeldung fehlgeschlagen: {detail}", auth);
        }
        _token = token;
    }

    /// <summary>Executes one command. <paramref name="action"/> 1 = also return "range" and "initial".</summary>
    public async Task<ReolinkResponse> ExecuteAsync(string cmd, JsonNode? param, int action = 0, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var item = new JsonObject { ["cmd"] = cmd, ["action"] = action };
            if (param != null) item["param"] = param.DeepClone();
            var r = (await PostAsync(cmd, new JsonArray(item), ct)).FirstOrDefault()
                    ?? new ReolinkResponse { Cmd = cmd, Code = -1, ErrorDetail = "Keine Antwort" };
            if (!r.Ok && r.RspCode == RspCodeLoginRequired && attempt == 0)
            {
                await LoginAsync(ct); // token expired
                continue;
            }
            return r;
        }
        return new ReolinkResponse { Cmd = cmd, Code = -1, ErrorDetail = "Sitzung abgelaufen" };
    }

    private async Task<List<ReolinkResponse>> PostAsync(string cmd, JsonArray body, CancellationToken ct)
    {
        var url = $"{BaseUrl}/cgi-bin/api.cgi?cmd={cmd}" + (_token != null && cmd != "Login" ? $"&token={_token}" : "");
        using var resp = await _http.PostAsJsonAsync(url, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text))
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");

        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { throw new HttpRequestException("Keine gültige Reolink-API-Antwort"); }

        var list = new List<ReolinkResponse>();
        IEnumerable<JsonNode?> items = node is JsonArray arr ? arr : new[] { node };
        foreach (var it in items)
        {
            if (it is not JsonObject o) continue;
            int code = o["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : -1;
            var err = o["error"] as JsonObject;
            list.Add(new ReolinkResponse
            {
                Cmd = o["cmd"]?.GetValue<string>() ?? cmd,
                Code = code,
                Value = o["value"] as JsonObject,
                Range = o["range"] as JsonObject,
                Initial = o["initial"] as JsonObject,
                ErrorDetail = err?["detail"]?.ToString(),
                RspCode = err?["rspCode"] is JsonValue rv && rv.TryGetValue<int>(out var rc) ? rc : 0,
            });
        }
        return list;
    }

    /// <summary>Type-safe nested lookup: returns null instead of throwing if a node has an unexpected type.</summary>
    public static string? GetString(JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(key, out node)) return null;
        }
        return node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_token != null)
        {
            try
            {
                // Reolink limits the number of concurrent sessions – always log out.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await PostAsync("Logout", new JsonArray(new JsonObject { ["cmd"] = "Logout", ["param"] = new JsonObject() }), cts.Token);
            }
            catch { /* ignore */ }
            _token = null;
        }
        _http.Dispose();
    }
}
