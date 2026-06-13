using System.Runtime.CompilerServices;
using System.Text;
using PstMigration.Application.Abstractions;
using PstMigration.Domain;
using PstMigration.Domain.Models;

namespace PstMigration.PstParser;

/// <summary>
/// Deterministic in-memory PST parser for development and automated tests.
/// Generates a small, stable mailbox so the full pipeline can run without a real
/// PST library or Microsoft 365 tenant.
/// </summary>
public sealed class MockPstParser : IPstParser
{
    private const int MailPerFolder = 3;

    public Task<PstMailboxInfo> InspectAsync(string pstPath, CancellationToken cancellationToken)
    {
        var folders = BuildFolders();
        var info = new PstMailboxInfo
        {
            PstPath = pstPath,
            FileSizeBytes = 5 * 1024 * 1024,
            DisplayName = "Mock Mailbox",
            IsUnicode = true,
            IsEncrypted = false,
            FolderCount = folders.Count,
            MailCount = folders.Count(f => f.ContainerClass == MigrationItemType.Mail) * MailPerFolder,
            ContactCount = 2,
            CalendarCount = 2,
            IsCorrupted = false,
        };
        return Task.FromResult(info);
    }

    public async IAsyncEnumerable<MigrationFolder> ReadFoldersAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var f in BuildFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return f;
        }
    }

    public async IAsyncEnumerable<MigrationMailItem> ReadMailItemsAsync(
        string pstPath, string folderId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < MailPerFolder; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var id = $"{folderId}:mail:{i}";
            yield return new MigrationMailItem
            {
                SourceItemId = id,
                SourceFolderPath = folderId,
                InternetMessageId = $"<{id}@mock.local>",
                Subject = $"Mock message {i} in {folderId}",
                From = new MailAddressModel { Name = "Sender", Address = "sender@mock.local" },
                ToRecipients = new[] { new MailAddressModel { Name = "Recipient", Address = "recipient@mock.local" } },
                OriginalSentDateTime = DateTimeOffset.UtcNow.AddDays(-i - 1),
                OriginalReceivedDateTime = DateTimeOffset.UtcNow.AddDays(-i - 1).AddMinutes(2),
                HtmlBody = $"<p>Mock body for message {i}.</p>",
                TextBody = $"Mock body for message {i}.",
                IsRead = i % 2 == 0,
                HasAttachments = i == 0,
                Attachments = i == 0
                    ? new[] { BuildMockAttachment(id) }
                    : Array.Empty<MigrationAttachment>(),
            };
        }
    }

    public async IAsyncEnumerable<MigrationContactItem> ReadContactsAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new MigrationContactItem
            {
                SourceItemId = $"contact:{i}",
                DisplayName = $"Mock Contact {i}",
                GivenName = "Mock",
                Surname = $"Contact{i}",
                EmailAddresses = new[] { $"contact{i}@mock.local" },
                MobilePhone = "+10000000000",
                CompanyName = "Mock Co",
            };
        }
    }

    public async IAsyncEnumerable<MigrationCalendarItem> ReadCalendarItemsAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var start = DateTimeOffset.UtcNow.Date.AddDays(i + 1).AddHours(9);
            yield return new MigrationCalendarItem
            {
                SourceItemId = $"event:{i}",
                Subject = $"Mock Event {i}",
                Location = "Mock Room",
                HtmlBody = $"<p>Mock event {i} body.</p>",
                Start = start,
                End = start.AddHours(1),
                TimeZone = "UTC",
                ShowAs = "busy",
                ReminderMinutesBeforeStart = 15,
                Attendees = new[] { new MailAddressModel { Name = "Attendee", Address = "attendee@mock.local" } },
                Organizer = new MailAddressModel { Name = "Organizer", Address = "organizer@mock.local" },
            };
        }
    }

    private static MigrationAttachment BuildMockAttachment(string itemId)
    {
        var bytes = Encoding.UTF8.GetBytes($"Mock attachment content for {itemId}");
        return new MigrationAttachment
        {
            SourceAttachmentId = $"{itemId}:att:0",
            FileName = "mock.txt",
            ContentType = "text/plain",
            SizeBytes = bytes.Length,
            IsInline = false,
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
        };
    }

    private static List<MigrationFolder> BuildFolders()
    {
        return new List<MigrationFolder>
        {
            new() { SourceFolderId = "Inbox", SourceFolderPath = "Inbox", DisplayName = "Inbox", ItemCount = MailPerFolder },
            new() { SourceFolderId = "Sent", SourceFolderPath = "Sent Items", DisplayName = "Sent Items", ItemCount = MailPerFolder },
            new() { SourceFolderId = "Archive2024", SourceFolderPath = "Customer/2024", DisplayName = "2024", ParentSourceFolderId = "Customer", ItemCount = MailPerFolder },
            new() { SourceFolderId = "Customer", SourceFolderPath = "Customer", DisplayName = "Customer", ItemCount = 0 },
        };
    }
}
