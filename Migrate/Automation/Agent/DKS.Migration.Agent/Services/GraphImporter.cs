using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using XstReader;

namespace DKS.Migration.Agent.Services;

/// <summary>
/// Imports a local PST into a Microsoft 365 mailbox via Microsoft Graph, using a
/// short-lived token brokered by the portal. The PST stays on this machine; only
/// individual items are streamed to Graph. Best-effort (Graph has no PST import API).
/// </summary>
public sealed class GraphImporter : IDisposable
{
    private const long InlineLimit = 3 * 1024 * 1024;   // 3 MB
    private const int ChunkSize = 4 * 1024 * 1024;      // 4 MB
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public GraphImporter(string accessToken, ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"), Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public async Task<(int Imported, int Failed)> ImportPstAsync(string pstPath, string mailbox, string rootFolderName,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        int imported = 0, failed = 0, total = 0;
        long bytesSent = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        using var xst = new XstFile(pstPath);

        var rootId = await EnsureFolderAsync(mailbox, null, rootFolderName, ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var folder in EnumerateFolders(xst.RootFolder))
        {
            var parentId = folder.ParentFolder?.Path is { } pp && map.TryGetValue(pp, out var pid) ? pid : rootId;
            map[folder.Path] = await EnsureFolderAsync(mailbox, parentId, folder.DisplayName ?? "Folder", ct);
            total += folder.Messages.Count();   // header-only pass, lets us report % and ETA
        }

        foreach (var folder in EnumerateFolders(xst.RootFolder))
        {
            var destId = map[folder.Path];
            foreach (var msg in folder.Messages)
            {
                ct.ThrowIfCancellationRequested();
                try { bytesSent += await CreateMessageAsync(mailbox, destId, msg, ct); imported++; }
                catch (Exception ex) { failed++; _logger.LogWarning("Import failed for '{Subject}': {Err}", msg.Subject, ex.Message); }

                if (progress is not null && sw.Elapsed - lastReport >= TimeSpan.FromSeconds(5))
                {
                    lastReport = sw.Elapsed;
                    progress.Report(new ImportProgress(imported, failed, total, bytesSent, sw.Elapsed));
                }
            }
        }
        progress?.Report(new ImportProgress(imported, failed, total, bytesSent, sw.Elapsed));
        return (imported, failed);
    }

    private async Task<string> EnsureFolderAsync(string mailbox, string? parentId, string name, CancellationToken ct)
    {
        var baseUrl = parentId is null
            ? $"users/{Uri.EscapeDataString(mailbox)}/mailFolders"
            : $"users/{Uri.EscapeDataString(mailbox)}/mailFolders/{parentId}/childFolders";

        using var list = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}?$select=id,displayName&$top=500"), ct);
        if (list.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var arr))
                foreach (var el in arr.EnumerateArray())
                    if (string.Equals(el.GetProperty("displayName").GetString(), name, StringComparison.OrdinalIgnoreCase))
                        return el.GetProperty("id").GetString()!;
        }
        // Surface auth/mailbox problems at the first mailbox access with Graph's real reason.
        else if (list.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            await EnsureGraphSuccessAsync(list, $"Access mailbox '{mailbox}'", ct);

        using var create = await SendAsync(() => JsonReq(HttpMethod.Post, baseUrl, new { displayName = name }), ct);
        await EnsureGraphSuccessAsync(create, $"Create folder '{name}' in '{mailbox}'", ct);
        using var cdoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        return cdoc.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<long> CreateMessageAsync(string mailbox, string folderId, XstMessage msg, CancellationToken ct)
    {
        var html = msg.Body is { } b && string.Equals(b.Format.ToString(), "Html", StringComparison.OrdinalIgnoreCase) ? b.Text : null;
        var text = html is null ? msg.Body?.Text : null;

        var small = new List<XstAttachment>();
        var large = new List<XstAttachment>();
        foreach (var a in msg.AttachmentsFiles)
            (a.Size <= InlineLimit ? small : large).Add(a);
        long bytes = Encoding.UTF8.GetByteCount(html ?? text ?? "")
            + small.Sum(a => (long)a.Size) + large.Sum(a => (long)a.Size);

        var body = new Dictionary<string, object?>
        {
            ["subject"] = msg.Subject,
            ["importance"] = msg.Importance?.ToString().ToLowerInvariant() ?? "normal",
            ["isRead"] = msg.IsRead,
            ["body"] = new { contentType = html is not null ? "HTML" : "Text", content = html ?? text ?? "" },
            ["toRecipients"] = Recipients(msg.Recipients?.To),
            ["ccRecipients"] = Recipients(msg.Recipients?.Cc),
            ["sentDateTime"] = msg.SubmittedTime?.ToUniversalTime().ToString("o"),
            ["receivedDateTime"] = msg.ReceivedTime?.ToUniversalTime().ToString("o"),
        };
        var sender = msg.Recipients?.Sender;
        if (sender is not null && !string.IsNullOrWhiteSpace(sender.Address))
            body["from"] = new { emailAddress = new { address = sender.Address, name = sender.DisplayName } };
        if (small.Count > 0)
            body["attachments"] = small.Select(InlineAttachment).ToArray();

        using var resp = await SendAsync(() => JsonReq(HttpMethod.Post,
            $"users/{Uri.EscapeDataString(mailbox)}/mailFolders/{folderId}/messages", body), ct);
        await EnsureGraphSuccessAsync(resp, $"Create message '{msg.Subject}'", ct);

        if (large.Count > 0)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var messageId = doc.RootElement.GetProperty("id").GetString()!;
            foreach (var a in large)
                await UploadLargeAttachmentAsync(mailbox, messageId, a, ct);
        }
        return bytes;
    }

    private object InlineAttachment(XstAttachment a)
    {
        using var ms = new MemoryStream();
        a.SaveToStream(ms);
        return new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.fileAttachment",
            ["name"] = a.LongFileName ?? a.FileName ?? "attachment.bin",
            ["contentBytes"] = Convert.ToBase64String(ms.ToArray()),
            ["isInline"] = a.IsInlineAttachment,
            ["contentId"] = a.ContentId,
        };
    }

    private async Task UploadLargeAttachmentAsync(string mailbox, string messageId, XstAttachment a, CancellationToken ct)
    {
        var name = a.LongFileName ?? a.FileName ?? "attachment.bin";
        using var session = await SendAsync(() => JsonReq(HttpMethod.Post,
            $"users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/attachments/createUploadSession",
            new { AttachmentItem = new { attachmentType = "file", name, size = (long)a.Size } }), ct);
        await EnsureGraphSuccessAsync(session, $"Create upload session for '{name}'", ct);
        using var sdoc = JsonDocument.Parse(await session.Content.ReadAsStringAsync(ct));
        var uploadUrl = sdoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using var ms = new MemoryStream();
        a.SaveToStream(ms);
        ms.Position = 0;
        long total = ms.Length, pos = 0;
        var buffer = new byte[ChunkSize];
        using var raw = new HttpClient();
        int read;
        while ((read = await ms.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.Add("Content-Range", $"bytes {pos}-{pos + read - 1}/{total}");
            using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
            using var putResp = await raw.SendAsync(put, ct);
            putResp.EnsureSuccessStatusCode();
            pos += read;
        }
    }

    /// <summary>Throws with Graph's actual error code/message (e.g. "Authorization_RequestDenied",
    /// "ErrorNonExistentMailbox") instead of the opaque "403 Forbidden", so failures are diagnosable.</summary>
    private static async Task EnsureGraphSuccessAsync(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                detail = string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
            }
        }
        catch { /* body not JSON — keep raw */ }
        var hint = (int)resp.StatusCode == 403
            ? " (403 → grant the app the APPLICATION permission Mail.ReadWrite + admin consent, and make sure the target is a licensed Exchange Online mailbox in THIS tenant)"
            : "";
        throw new HttpRequestException($"{operation} → {(int)resp.StatusCode} {resp.StatusCode}. {detail}{hint}");
    }

    private static object[] Recipients(IEnumerable<XstRecipient>? recipients)
        => recipients is null ? Array.Empty<object>()
            : recipients.Where(r => !string.IsNullOrWhiteSpace(r.Address))
                .Select(r => (object)new { emailAddress = new { address = r.Address, name = r.DisplayName } }).ToArray();

    private static HttpRequestMessage JsonReq(HttpMethod method, string url, object body)
        => new(method, url) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    /// <summary>Sends with simple retry on 429/503 honoring Retry-After. Factory rebuilds the request per attempt.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resp = await _http.SendAsync(factory(), ct);
            if (resp.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= 6)
                return resp;
            var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static IEnumerable<XstFolder> EnumerateFolders(XstFolder root)
    {
        foreach (var child in root.Folders)
        {
            yield return child;
            foreach (var d in EnumerateFolders(child)) yield return d;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Throughput snapshot reported during an import (items/s, MB/s, % done, ETA).</summary>
public readonly record struct ImportProgress(int Imported, int Failed, int Total, long BytesSent, TimeSpan Elapsed)
{
    public double ItemsPerSec => Elapsed.TotalSeconds > 0 ? Imported / Elapsed.TotalSeconds : 0;
    public double MBPerSec => Elapsed.TotalSeconds > 0 ? BytesSent / 1_048_576.0 / Elapsed.TotalSeconds : 0;
    public double Percent => Total > 0 ? (Imported + Failed) * 100.0 / Total : 0;
    public TimeSpan Eta
    {
        get
        {
            var done = Imported + Failed;
            if (done <= 0 || Total <= done) return TimeSpan.Zero;
            var perItem = Elapsed.TotalSeconds / done;
            return TimeSpan.FromSeconds(perItem * (Total - done));
        }
    }

    public string Summary() => Total > 0
        ? $"{Imported}/{Total} ({Percent:F0}%) — {ItemsPerSec:F1} items/s, {MBPerSec:F2} MB/s, {BytesSent / 1_048_576.0:F1} MB, ETA {Eta:hh\\:mm\\:ss}"
        : $"{Imported} items — {ItemsPerSec:F1} items/s, {MBPerSec:F2} MB/s, {BytesSent / 1_048_576.0:F1} MB";
}
