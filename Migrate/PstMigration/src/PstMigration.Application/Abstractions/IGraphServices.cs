using PstMigration.Domain;
using PstMigration.Domain.Models;

namespace PstMigration.Application.Abstractions;

/// <summary>
/// Acquires short-lived Microsoft Graph access tokens. The certificate lives only
/// on the portal; the agent calls the portal to obtain a token, then talks to Graph
/// directly (the PST never leaves the endpoint).
/// </summary>
public interface IGraphTokenBroker
{
    Task<GraphAccessToken> AcquireTokenAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record GraphAccessToken(string AccessToken, DateTimeOffset ExpiresOn, string Resource = "https://graph.microsoft.com");

/// <summary>
/// Writes individual mailbox objects to Exchange Online via Microsoft Graph.
/// Implementations must be idempotent-friendly (caller provides source ids/hashes).
/// </summary>
public interface IGraphMailboxService
{
    Task<string> EnsureFolderAsync(string mailbox, string? parentFolderId, string folderName, CancellationToken cancellationToken);

    Task<MigrationResult> CreateContactAsync(string mailbox, string contactFolderId, MigrationContactItem contact, CancellationToken cancellationToken);

    Task<MigrationResult> CreateCalendarEventAsync(string mailbox, string calendarId, MigrationCalendarItem ev, CalendarMigrationMode mode, CancellationToken cancellationToken);

    Task<MigrationResult> CreateMailAsync(string mailbox, string destinationFolderId, MigrationMailItem mail, EmailMigrationMode mode, CancellationToken cancellationToken);

    Task<string> EnsureContactFolderAsync(string mailbox, string folderName, CancellationToken cancellationToken);

    Task<string> EnsureCalendarAsync(string mailbox, string calendarName, CancellationToken cancellationToken);
}
