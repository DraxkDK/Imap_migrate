using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PstMigration.Application;
using PstMigration.Application.Abstractions;
using PstMigration.Domain;
using PstMigration.Domain.Models;

namespace PstMigration.Graph;

/// <summary>
/// In-memory fake of <see cref="IGraphMailboxService"/> for development and tests.
/// Produces deterministic destination ids and records the same fidelity warnings the
/// real Graph implementation will, so the pipeline and reporting can be exercised
/// without a Microsoft 365 tenant.
/// </summary>
public sealed class MockGraphMailboxService : IGraphMailboxService
{
    private readonly ILogger<MockGraphMailboxService> _logger;
    private readonly ConcurrentDictionary<string, string> _folders = new();

    public MockGraphMailboxService(ILogger<MockGraphMailboxService> logger) => _logger = logger;

    public Task<string> EnsureFolderAsync(string mailbox, string? parentFolderId, string folderName, CancellationToken cancellationToken)
    {
        var key = $"{mailbox}|{parentFolderId}|{folderName}";
        var id = _folders.GetOrAdd(key, _ => $"folder-{Guid.NewGuid():N}");
        _logger.LogDebug("[mock] EnsureFolder {Mailbox} '{Folder}' -> {Id}", mailbox, folderName, id);
        return Task.FromResult(id);
    }

    public Task<string> EnsureContactFolderAsync(string mailbox, string folderName, CancellationToken cancellationToken)
        => Task.FromResult(_folders.GetOrAdd($"{mailbox}|contacts|{folderName}", _ => $"contactfolder-{Guid.NewGuid():N}"));

    public Task<string> EnsureCalendarAsync(string mailbox, string calendarName, CancellationToken cancellationToken)
        => Task.FromResult(_folders.GetOrAdd($"{mailbox}|calendar|{calendarName}", _ => $"calendar-{Guid.NewGuid():N}"));

    public Task<MigrationResult> CreateContactAsync(string mailbox, string contactFolderId, MigrationContactItem contact, CancellationToken cancellationToken)
    {
        var destId = $"contact-{Guid.NewGuid():N}";
        return Task.FromResult(MigrationResult.Ok(contact.SourceItemId, MigrationItemType.Contact, destId, contactFolderId));
    }

    public Task<MigrationResult> CreateCalendarEventAsync(string mailbox, string calendarId, MigrationCalendarItem ev, CalendarMigrationMode mode, CancellationToken cancellationToken)
    {
        if (mode == CalendarMigrationMode.Disabled)
            return Task.FromResult(Skip(ev.SourceItemId, MigrationItemType.CalendarEvent));

        var destId = $"event-{Guid.NewGuid():N}";
        // Safe mode preserves attendees as metadata and never sends invitations.
        var warning = mode == CalendarMigrationMode.Safe && ev.Attendees.Count > 0
            ? FidelityWarnings.AttendeesPreservedAsMetadata
            : null;
        return Task.FromResult(MigrationResult.Ok(ev.SourceItemId, MigrationItemType.CalendarEvent, destId, calendarId, warning));
    }

    public Task<MigrationResult> CreateMailAsync(string mailbox, string destinationFolderId, MigrationMailItem mail, EmailMigrationMode mode, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case EmailMigrationMode.Disabled:
            case EmailMigrationMode.MetadataOnly:
                return Task.FromResult(Skip(mail.SourceItemId, MigrationItemType.Mail));
            default:
                var destId = $"message-{Guid.NewGuid():N}";
                // Best-effort: original timestamps/conversation cannot be fully preserved.
                var warning = string.Join(';',
                    FidelityWarnings.TimestampsNotPreserved,
                    FidelityWarnings.ConversationNotRecreated);
                return Task.FromResult(MigrationResult.Ok(mail.SourceItemId, MigrationItemType.Mail, destId, destinationFolderId, warning));
        }
    }

    private static MigrationResult Skip(string sourceId, MigrationItemType type) => new()
    {
        SourceItemId = sourceId,
        ItemType = type,
        Status = MigrationItemStatus.Skipped,
    };
}
