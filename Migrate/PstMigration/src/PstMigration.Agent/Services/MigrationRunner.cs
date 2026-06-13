using PstMigration.Application.Abstractions;
using PstMigration.Contracts;
using PstMigration.Domain;
using PstMigration.Domain.Models;

namespace PstMigration.Agent.Services;

/// <summary>
/// Drives one job assignment: per mailbox it recreates folders and migrates
/// contacts, calendar (safe) and mail (best-effort) via Graph. Already-completed
/// items are skipped (resume/idempotency). The PST never leaves the machine.
/// </summary>
public sealed class MigrationRunner
{
    private const int BatchSize = 50;

    private readonly IPstParser _parser;
    private readonly IGraphMailboxService _graph;
    private readonly PortalClient _portal;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(IPstParser parser, IGraphMailboxService graph, PortalClient portal, ILogger<MigrationRunner> logger)
    {
        _parser = parser;
        _graph = graph;
        _portal = portal;
        _logger = logger;
    }

    public async Task RunJobAsync(JobAssignmentDto job, CancellationToken ct)
    {
        _logger.LogInformation("Starting job {Job} ({Count} mailbox(es))", job.JobName, job.Mailboxes.Count);
        foreach (var mb in job.Mailboxes)
        {
            try { await RunMailboxAsync(job.JobId, mb, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Mailbox {Mailbox} failed", mb.TargetMailbox); }
        }
        await _portal.CompleteJobAsync(job.JobId, ct);
        _logger.LogInformation("Job {Job} complete", job.JobName);
    }

    private async Task RunMailboxAsync(Guid jobId, JobMailboxDto mb, CancellationToken ct)
    {
        var emailMode = ParseEnum(mb.EmailMode, EmailMigrationMode.MetadataOnly);
        var calMode = ParseEnum(mb.CalendarMode, CalendarMigrationMode.Safe);
        var contactMode = ParseEnum(mb.ContactMode, ContactMigrationMode.Enabled);

        var done = await _portal.GetCompletedSourceIdsAsync(jobId, mb.MailboxId, ct);
        var batch = new List<ItemStatusDto>();

        // 1) Folder structure under the configurable root.
        var rootId = await _graph.EnsureFolderAsync(mb.TargetMailbox, null, mb.RootFolderName, ct);
        var folderMap = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var f in _parser.ReadFoldersAsync(mb.PstPath, ct))
        {
            var parentId = f.ParentSourceFolderId is not null && folderMap.TryGetValue(f.ParentSourceFolderId, out var p) ? p : rootId;
            var destId = await _graph.EnsureFolderAsync(mb.TargetMailbox, parentId, f.DisplayName, ct);
            folderMap[f.SourceFolderId] = destId;
        }

        // 2) Contacts.
        if (contactMode == ContactMigrationMode.Enabled)
        {
            var contactFolder = await _graph.EnsureContactFolderAsync(mb.TargetMailbox, "Imported PST Contacts", ct);
            await foreach (var c in _parser.ReadContactsAsync(mb.PstPath, ct))
            {
                if (done.Contains(c.SourceItemId)) continue;
                var r = await _graph.CreateContactAsync(mb.TargetMailbox, contactFolder, c, ct);
                await Record(jobId, mb.MailboxId, batch, r, "Contacts", 0, ct);
            }
        }

        // 3) Calendar (safe by default — no invitations).
        if (calMode != CalendarMigrationMode.Disabled)
        {
            var calendar = await _graph.EnsureCalendarAsync(mb.TargetMailbox, "Imported PST Calendar", ct);
            await foreach (var ev in _parser.ReadCalendarItemsAsync(mb.PstPath, ct))
            {
                if (done.Contains(ev.SourceItemId)) continue;
                var r = await _graph.CreateCalendarEventAsync(mb.TargetMailbox, calendar, ev, calMode, ct);
                await Record(jobId, mb.MailboxId, batch, r, "Calendar", 0, ct);
            }
        }

        // 4) Mail (best-effort). MetadataOnly still records inventory (Graph returns Skipped).
        if (emailMode != EmailMigrationMode.Disabled)
        {
            foreach (var (sourceFolderId, destFolderId) in folderMap)
            {
                await foreach (var mail in _parser.ReadMailItemsAsync(mb.PstPath, sourceFolderId, ct))
                {
                    if (done.Contains(mail.SourceItemId)) continue;
                    var size = mail.Attachments.Sum(a => a.SizeBytes);
                    var r = await _graph.CreateMailAsync(mb.TargetMailbox, destFolderId, mail, emailMode, ct);
                    await Record(jobId, mb.MailboxId, batch, r, mail.SourceFolderPath, size, ct);
                }
            }
        }

        await Flush(jobId, mb.MailboxId, batch, ct);
    }

    private async Task Record(Guid jobId, Guid mailboxId, List<ItemStatusDto> batch, MigrationResult r,
        string folderPath, long size, CancellationToken ct)
    {
        batch.Add(new ItemStatusDto(
            SourceItemId: r.SourceItemId,
            SourceFolderPath: folderPath,
            ItemType: r.ItemType.ToString(),
            ItemSizeBytes: size,
            DestinationItemId: r.DestinationItemId,
            Status: r.Status.ToString(),
            ErrorCode: r.ErrorCode,
            WarningCode: r.FidelityWarning,
            ProcessingDurationMs: (int)r.Duration.TotalMilliseconds));

        if (batch.Count >= BatchSize)
            await Flush(jobId, mailboxId, batch, ct);
    }

    private async Task Flush(Guid jobId, Guid mailboxId, List<ItemStatusDto> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await _portal.ReportItemsAsync(new ItemStatusBatchRequest(jobId, mailboxId, batch.ToList()), ct);
        batch.Clear();
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
        => Enum.TryParse<T>(value, ignoreCase: true, out var v) ? v : fallback;
}
