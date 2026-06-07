using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MDaemonCalendarMigration.Services;

/// <summary>
/// Calls Microsoft Graph REST API using client credentials (app-only).
/// No Microsoft.Graph module or SDK required — raw HttpClient only.
///
/// Token endpoint:
///   POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
///
/// Import endpoint:
///   POST https://graph.microsoft.com/v1.0/users/{targetMailbox}/events
/// </summary>
public sealed class GraphApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private string?  _token;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // ── authentication ───────────────────────────────────────────────────────

    public async Task<string> GetTokenAsync(
        string tenantId, string clientId, string clientSecret,
        CancellationToken ct = default)
    {
        if (_token != null && DateTime.UtcNow < _tokenExpiry) return _token;

        var url  = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = clientId,
            ["scope"]         = "https://graph.microsoft.com/.default",
            ["client_secret"] = clientSecret,
            ["grant_type"]    = "client_credentials"
        });

        var resp = await _http.PostAsync(url, body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token request failed [{resp.StatusCode}]:\n{Trim(json)}");

        var doc = JsonDocument.Parse(json);
        _token      = doc.RootElement.GetProperty("access_token").GetString()
                      ?? throw new Exception("access_token missing in response");
        var expires = doc.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expires - 60);

        return _token;
    }

    // ── connection test ──────────────────────────────────────────────────────

    public async Task TestConnectionAsync(
        string tenantId, string clientId, string clientSecret,
        CancellationToken ct = default)
    {
        var token = await GetTokenAsync(tenantId, clientId, clientSecret, ct);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/users?$top=1&$select=mail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Graph connection test failed [{resp.StatusCode}]:\n{Trim(json)}");
    }

    // ── event import ─────────────────────────────────────────────────────────

    /// <summary>
    /// POST one Graph Event JSON to the target mailbox calendar.
    /// Retries up to 3 times on 429 (throttle).
    /// Returns (true, null) on success or (false, errorMessage) on failure.
    /// </summary>
    public async Task<(bool ok, string? error)> ImportEventAsync(
        string token, string targetMailbox, string eventJson,
        CancellationToken ct = default)
    {
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(targetMailbox)}/events";

        for (int attempt = 0; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(eventJson, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode) return (true, null);

            // 429 Too Many Requests — honour Retry-After
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, ct);
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return (false, $"[{(int)resp.StatusCode}] {Trim(body)}");
        }

        return (false, "Max retries exceeded");
    }

    // ── cleanup ──────────────────────────────────────────────────────────────

    public void InvalidateToken() { _token = null; _tokenExpiry = DateTime.MinValue; }

    public void Dispose() => _http.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Trim(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? json;
        }
        catch { }
        return json.Length > 300 ? json[..300] + "…" : json;
    }
}
