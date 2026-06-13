using PstMigration.Domain.Models;

namespace PstMigration.Application.Abstractions;

/// <summary>
/// Abstraction over the PST reading library so the implementation can be swapped
/// (mock for tests/dev, a real library later) without touching business logic.
/// Returns normalized domain models directly to avoid a duplicate model layer.
/// All content streams stay on the local machine.
/// </summary>
public interface IPstParser
{
    Task<PstMailboxInfo> InspectAsync(string pstPath, CancellationToken cancellationToken);

    IAsyncEnumerable<MigrationFolder> ReadFoldersAsync(string pstPath, CancellationToken cancellationToken);

    IAsyncEnumerable<MigrationMailItem> ReadMailItemsAsync(string pstPath, string folderId, CancellationToken cancellationToken);

    IAsyncEnumerable<MigrationCalendarItem> ReadCalendarItemsAsync(string pstPath, CancellationToken cancellationToken);

    IAsyncEnumerable<MigrationContactItem> ReadContactsAsync(string pstPath, CancellationToken cancellationToken);
}
