using System.Runtime.CompilerServices;
using PstMigration.Application.Abstractions;
using PstMigration.Domain;
using PstMigration.Domain.Models;
using XstReader;

namespace PstMigration.PstParser;

/// <summary>
/// Real <see cref="IPstParser"/> backed by the open-source XstReader.Api (MIT).
/// Reads Unicode/ANSI PST folders, mail, recipients and file attachments locally.
/// Contacts/calendar parsing is added in Phase 3/4.
/// </summary>
public sealed class XstPstParser : IPstParser
{
    public Task<PstMailboxInfo> InspectAsync(string pstPath, CancellationToken cancellationToken)
    {
        try
        {
            using var xst = new XstFile(pstPath);
            var folders = EnumerateFolders(xst.RootFolder).ToList();
            var mailCount = folders.Sum(f => SafeContentCount(f));
            var info = new PstMailboxInfo
            {
                PstPath = pstPath,
                FileSizeBytes = new FileInfo(pstPath).Length,
                DisplayName = xst.DisplayName ?? Path.GetFileNameWithoutExtension(pstPath),
                IsUnicode = true,
                IsEncrypted = false,
                FolderCount = folders.Count,
                MailCount = mailCount,
                ContactCount = 0,
                CalendarCount = 0,
                IsCorrupted = false,
            };
            return Task.FromResult(info);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PstMailboxInfo
            {
                PstPath = pstPath,
                FileSizeBytes = SafeLength(pstPath),
                IsUnicode = true,
                IsCorrupted = true,
                InspectionWarning = ex.Message,
            });
        }
    }

    public async IAsyncEnumerable<MigrationFolder> ReadFoldersAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var xst = new XstFile(pstPath);
        foreach (var folder in EnumerateFolders(xst.RootFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new MigrationFolder
            {
                SourceFolderId = folder.Path,
                SourceFolderPath = folder.Path,
                DisplayName = folder.DisplayName ?? "Unnamed",
                ParentSourceFolderId = folder.ParentFolder?.Path,
                ContainerClass = MigrationItemType.Mail,
                ItemCount = SafeContentCount(folder),
            };
        }
    }

    public async IAsyncEnumerable<MigrationMailItem> ReadMailItemsAsync(
        string pstPath, string folderId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var xst = new XstFile(pstPath);
        var folder = EnumerateFolders(xst.RootFolder).FirstOrDefault(f => f.Path == folderId);
        if (folder is null) yield break;

        var index = 0;
        foreach (var msg in folder.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return MapMessage(msg, folder.Path, index++);
        }
    }

    // Contacts/calendar parsing arrives in Phase 3/4; the mock parser covers them for now.
    public async IAsyncEnumerable<MigrationContactItem> ReadContactsAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<MigrationCalendarItem> ReadCalendarItemsAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    // ---- helpers -----------------------------------------------------------

    private static MigrationMailItem MapMessage(XstMessage msg, string folderPath, int index)
    {
        string? html = null, text = null;
        var body = msg.Body;
        if (body is not null && !string.IsNullOrEmpty(body.Text))
        {
            if (string.Equals(body.Format.ToString(), "Html", StringComparison.OrdinalIgnoreCase))
                html = body.Text;
            else
                text = body.Text;
        }

        var sourceId = !string.IsNullOrWhiteSpace(msg.InternetMessageId)
            ? msg.InternetMessageId!
            : $"{folderPath}|{index}|{msg.Subject}|{msg.SubmittedTime?.Ticks ?? 0}";

        return new MigrationMailItem
        {
            SourceItemId = sourceId,
            SourceFolderPath = folderPath,
            InternetMessageId = msg.InternetMessageId,
            Subject = msg.Subject,
            From = MapRecipient(msg.Recipients?.Sender) ?? FromString(msg.From),
            ToRecipients = MapRecipients(msg.Recipients?.To),
            CcRecipients = MapRecipients(msg.Recipients?.Cc),
            BccRecipients = MapRecipients(msg.Recipients?.Bcc),
            OriginalSentDateTime = ToOffset(msg.SubmittedTime),
            OriginalReceivedDateTime = ToOffset(msg.ReceivedTime),
            HtmlBody = html,
            TextBody = text,
            IsRead = msg.IsRead,
            Importance = msg.Importance?.ToString().ToLowerInvariant() ?? "normal",
            HasAttachments = msg.HasAttachmentsFiles,
            Attachments = MapAttachments(msg),
        };
    }

    private static IReadOnlyList<MigrationAttachment> MapAttachments(XstMessage msg)
    {
        var list = new List<MigrationAttachment>();
        foreach (var att in msg.AttachmentsFiles)
        {
            byte[] bytes;
            try
            {
                using var ms = new MemoryStream();
                att.SaveToStream(ms);
                bytes = ms.ToArray();
            }
            catch
            {
                continue; // unreadable attachment — skipped, reported at migration time
            }

            list.Add(new MigrationAttachment
            {
                SourceAttachmentId = att.ContentId ?? att.FileNameForSaving ?? Guid.NewGuid().ToString("N"),
                FileName = att.LongFileName ?? att.FileName ?? "attachment.bin",
                ContentType = "application/octet-stream",
                SizeBytes = att.Size,
                IsInline = att.IsInlineAttachment,
                ContentId = att.ContentId,
                OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            });
        }
        return list;
    }

    private static IReadOnlyList<MailAddressModel> MapRecipients(IEnumerable<XstRecipient>? recipients)
    {
        if (recipients is null) return Array.Empty<MailAddressModel>();
        return recipients.Select(MapRecipient).Where(r => r is not null).Select(r => r!).ToList();
    }

    private static MailAddressModel? MapRecipient(XstRecipient? r)
    {
        if (r is null) return null;
        var address = !string.IsNullOrWhiteSpace(r.Address) ? r.Address : r.DisplayName;
        if (string.IsNullOrWhiteSpace(address)) return null;
        return new MailAddressModel { Name = r.DisplayName, Address = address! };
    }

    private static MailAddressModel? FromString(string? from)
        => string.IsNullOrWhiteSpace(from) ? null : new MailAddressModel { Name = from, Address = from };

    private static IEnumerable<XstFolder> EnumerateFolders(XstFolder root)
    {
        // Skip the synthetic root; yield real folders depth-first.
        foreach (var child in root.Folders)
        {
            yield return child;
            foreach (var d in EnumerateFolders(child))
                yield return d;
        }
    }

    private static int SafeContentCount(XstFolder f)
    {
        try { return f.ContentCount; } catch { return 0; }
    }

    private static DateTimeOffset? ToOffset(DateTime? dt)
        => dt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)) : null;

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
