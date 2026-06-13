using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PstMigration.Application;
using PstMigration.Application.Abstractions;
using PstMigration.Domain;
using PstMigration.Domain.Models;

namespace PstMigration.Graph;

/// <summary>
/// Real <see cref="IGraphMailboxService"/> over Microsoft Graph using a typed
/// HttpClient (base https://graph.microsoft.com/v1.0/). Bearer + retry are added
/// by delegating handlers. Bodies/attachments are never logged.
/// </summary>
public sealed class GraphMailboxService : IGraphMailboxService
{
    private const long InlineAttachmentLimit = 3 * 1024 * 1024; // 3 MB
    private const int UploadChunkSize = 4 * 1024 * 1024;        // 4 MB (multiple of 320 KB)

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<GraphMailboxService> _logger;

    public GraphMailboxService(HttpClient http, ILogger<GraphMailboxService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ---- folders -----------------------------------------------------------

    public async Task<string> EnsureFolderAsync(string mailbox, string? parentFolderId, string folderName, CancellationToken ct)
    {
        var baseUrl = parentFolderId is null
            ? $"users/{Uri.EscapeDataString(mailbox)}/mailFolders"
            : $"users/{Uri.EscapeDataString(mailbox)}/mailFolders/{parentFolderId}/childFolders";

        var existing = await FindByDisplayNameAsync($"{baseUrl}?$select=id,displayName&$top=500", folderName, ct);
        if (existing is not null) return existing;

        using var resp = await _http.PostAsJsonAsync(baseUrl, new { displayName = folderName }, Json, ct);
        return await ReadIdAsync(resp, ct);
    }

    public async Task<string> EnsureContactFolderAsync(string mailbox, string folderName, CancellationToken ct)
    {
        var baseUrl = $"users/{Uri.EscapeDataString(mailbox)}/contactFolders";
        var existing = await FindByDisplayNameAsync($"{baseUrl}?$select=id,displayName&$top=200", folderName, ct);
        if (existing is not null) return existing;
        using var resp = await _http.PostAsJsonAsync(baseUrl, new { displayName = folderName }, Json, ct);
        return await ReadIdAsync(resp, ct);
    }

    public async Task<string> EnsureCalendarAsync(string mailbox, string calendarName, CancellationToken ct)
    {
        var baseUrl = $"users/{Uri.EscapeDataString(mailbox)}/calendars";
        var existing = await FindByNameAsync($"{baseUrl}?$select=id,name&$top=200", "name", calendarName, ct);
        if (existing is not null) return existing;
        using var resp = await _http.PostAsJsonAsync(baseUrl, new { name = calendarName }, Json, ct);
        return await ReadIdAsync(resp, ct);
    }

    // ---- contacts ----------------------------------------------------------

    public async Task<MigrationResult> CreateContactAsync(string mailbox, string contactFolderId, MigrationContactItem c, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["givenName"] = c.GivenName,
            ["middleName"] = c.MiddleName,
            ["surname"] = c.Surname,
            ["displayName"] = c.DisplayName,
            ["companyName"] = c.CompanyName,
            ["department"] = c.Department,
            ["jobTitle"] = c.JobTitle,
            ["mobilePhone"] = c.MobilePhone,
            ["personalNotes"] = c.PersonalNotes,
            ["emailAddresses"] = c.EmailAddresses.Select(a => new { address = a, name = c.DisplayName }).ToArray(),
            ["businessPhones"] = c.BusinessPhone is null ? Array.Empty<string>() : new[] { c.BusinessPhone },
            ["homePhones"] = c.HomePhone is null ? Array.Empty<string>() : new[] { c.HomePhone },
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"users/{Uri.EscapeDataString(mailbox)}/contactFolders/{contactFolderId}/contacts", body, Json, ct);
            var id = await ReadIdAsync(resp, ct);
            return MigrationResult.Ok(c.SourceItemId, MigrationItemType.Contact, id, contactFolderId);
        }
        catch (GraphException ex)
        {
            return MigrationResult.Fail(c.SourceItemId, MigrationItemType.Contact, ex.Code, ex.Message);
        }
    }

    // ---- calendar ----------------------------------------------------------

    public async Task<MigrationResult> CreateCalendarEventAsync(string mailbox, string calendarId, MigrationCalendarItem ev, CalendarMigrationMode mode, CancellationToken ct)
    {
        if (mode == CalendarMigrationMode.Disabled)
            return Skip(ev.SourceItemId, MigrationItemType.CalendarEvent);

        var bodyHtml = ev.HtmlBody ?? "";
        string? warning = null;

        // Safe mode: never send invitations; preserve attendees as metadata in the body.
        if (mode == CalendarMigrationMode.Safe && ev.Attendees.Count > 0)
        {
            var names = string.Join(", ", ev.Attendees.Select(a => a.Address));
            bodyHtml += $"<hr/><p><b>[Imported] Original attendees:</b> {WebUtility.HtmlEncode(names)}</p>";
            warning = FidelityWarnings.AttendeesPreservedAsMetadata;
        }

        var body = new Dictionary<string, object?>
        {
            ["subject"] = ev.Subject,
            ["body"] = new { contentType = "HTML", content = bodyHtml },
            ["start"] = new { dateTime = (ev.Start ?? DateTimeOffset.UtcNow).DateTime.ToString("s"), timeZone = ev.TimeZone },
            ["end"] = new { dateTime = (ev.End ?? ev.Start ?? DateTimeOffset.UtcNow).DateTime.ToString("s"), timeZone = ev.TimeZone },
            ["location"] = ev.Location is null ? null : new { displayName = ev.Location },
            ["isAllDay"] = ev.IsAllDay,
            ["showAs"] = ev.ShowAs,
            ["isReminderOn"] = ev.ReminderMinutesBeforeStart.HasValue,
            ["reminderMinutesBeforeStart"] = ev.ReminderMinutesBeforeStart,
        };

        // Advanced mode includes attendees (may notify) — caller opted in explicitly.
        if (mode == CalendarMigrationMode.Advanced && ev.Attendees.Count > 0)
        {
            body["attendees"] = ev.Attendees.Select(a => new
            {
                emailAddress = new { address = a.Address, name = a.Name },
                type = "required",
            }).ToArray();
        }

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"users/{Uri.EscapeDataString(mailbox)}/calendars/{calendarId}/events", body, Json, ct);
            var id = await ReadIdAsync(resp, ct);
            return MigrationResult.Ok(ev.SourceItemId, MigrationItemType.CalendarEvent, id, calendarId, warning);
        }
        catch (GraphException ex)
        {
            return MigrationResult.Fail(ev.SourceItemId, MigrationItemType.CalendarEvent, ex.Code, ex.Message);
        }
    }

    // ---- mail (best-effort) ------------------------------------------------

    public async Task<MigrationResult> CreateMailAsync(string mailbox, string destinationFolderId, MigrationMailItem mail, EmailMigrationMode mode, CancellationToken ct)
    {
        if (mode is EmailMigrationMode.Disabled or EmailMigrationMode.MetadataOnly)
            return Skip(mail.SourceItemId, MigrationItemType.Mail);

        var small = new List<MigrationAttachment>();
        var large = new List<MigrationAttachment>();
        foreach (var a in mail.Attachments)
            (a.SizeBytes <= InlineAttachmentLimit ? small : large).Add(a);

        var body = new Dictionary<string, object?>
        {
            ["subject"] = mail.Subject,
            ["importance"] = mail.Importance,
            ["isRead"] = mail.IsRead,
            ["body"] = new { contentType = mail.HtmlBody is not null ? "HTML" : "Text", content = mail.HtmlBody ?? mail.TextBody ?? "" },
            ["from"] = mail.From is null ? null : new { emailAddress = new { address = mail.From.Address, name = mail.From.Name } },
            ["toRecipients"] = mail.ToRecipients.Select(ToRecipient).ToArray(),
            ["ccRecipients"] = mail.CcRecipients.Select(ToRecipient).ToArray(),
            ["bccRecipients"] = mail.BccRecipients.Select(ToRecipient).ToArray(),
            ["sentDateTime"] = mail.OriginalSentDateTime?.ToString("o"),
            ["receivedDateTime"] = mail.OriginalReceivedDateTime?.ToString("o"),
            ["singleValueExtendedProperties"] = new[]
            {
                // Store the original internet message id for de-dup/audit.
                new { id = "String {00020386-0000-0000-c000-000000000046} Name PstMigrationSourceId", value = mail.SourceItemId },
            },
        };

        if (small.Count > 0)
            body["attachments"] = await BuildInlineAttachmentsAsync(small, ct);

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"users/{Uri.EscapeDataString(mailbox)}/mailFolders/{destinationFolderId}/messages", body, Json, ct);
            var id = await ReadIdAsync(resp, ct);

            foreach (var att in large)
                await UploadLargeAttachmentAsync(mailbox, id, att, ct);

            var warning = string.Join(';', FidelityWarnings.TimestampsNotPreserved, FidelityWarnings.ConversationNotRecreated);
            return MigrationResult.Ok(mail.SourceItemId, MigrationItemType.Mail, id, destinationFolderId, warning);
        }
        catch (GraphException ex)
        {
            return MigrationResult.Fail(mail.SourceItemId, MigrationItemType.Mail, ex.Code, ex.Message);
        }
    }

    private async Task<object[]> BuildInlineAttachmentsAsync(List<MigrationAttachment> attachments, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var a in attachments)
        {
            await using var stream = await a.OpenReadAsync(ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            list.Add(new Dictionary<string, object?>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = a.FileName,
                ["contentType"] = a.ContentType,
                ["isInline"] = a.IsInline,
                ["contentId"] = a.ContentId,
                ["contentBytes"] = Convert.ToBase64String(ms.ToArray()),
            });
        }
        return list.ToArray();
    }

    private async Task UploadLargeAttachmentAsync(string mailbox, string messageId, MigrationAttachment att, CancellationToken ct)
    {
        var sessionBody = new
        {
            AttachmentItem = new { attachmentType = "file", name = att.FileName, size = att.SizeBytes },
        };
        using var sessionResp = await _http.PostAsJsonAsync(
            $"users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/attachments/createUploadSession",
            sessionBody, Json, ct);
        await EnsureSuccessAsync(sessionResp, ct);

        using var doc = JsonDocument.Parse(await sessionResp.Content.ReadAsStringAsync(ct));
        var uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()!;

        await using var stream = await att.OpenReadAsync(ct);
        var buffer = new byte[UploadChunkSize];
        long position = 0;
        var total = att.SizeBytes;
        int read;
        // A separate HttpClient: the upload URL is pre-authenticated (no bearer needed).
        using var raw = new HttpClient();
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.Add("Content-Range", $"bytes {position}-{position + read - 1}/{total}");
            using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
            using var putResp = await raw.SendAsync(put, ct);
            putResp.EnsureSuccessStatusCode();
            position += read;
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static object ToRecipient(MailAddressModel a)
        => new { emailAddress = new { address = a.Address, name = a.Name } };

    private async Task<string?> FindByDisplayNameAsync(string url, string displayName, CancellationToken ct)
        => await FindByNameAsync(url, "displayName", displayName, ct);

    private async Task<string?> FindByNameAsync(string url, string nameProp, string value, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var arr)) return null;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.TryGetProperty(nameProp, out var n) &&
                string.Equals(n.GetString(), value, StringComparison.OrdinalIgnoreCase))
                return el.GetProperty("id").GetString();
        }
        return null;
    }

    private async Task<string> ReadIdAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await EnsureSuccessAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        var code = $"HTTP_{(int)resp.StatusCode}";
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c))
                code = c.GetString() ?? code;
        }
        catch { /* non-JSON error body */ }
        throw new GraphException(code, $"Graph returned {(int)resp.StatusCode}");
    }

    private static MigrationResult Skip(string id, MigrationItemType type)
        => new() { SourceItemId = id, ItemType = type, Status = MigrationItemStatus.Skipped };
}

/// <summary>Graph error carrying the Graph error code (no response body content).</summary>
public sealed class GraphException : Exception
{
    public string Code { get; }
    public GraphException(string code, string message) : base(message) => Code = code;
}
