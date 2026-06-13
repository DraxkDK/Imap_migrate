using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PstMigration.Contracts;
using PstMigration.Domain;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly MigrationDbContext _db;
    private readonly ILogger<JobsController> _logger;

    public JobsController(MigrationDbContext db, ILogger<JobsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Batched, idempotent item-status upserts (metadata only — no bodies).</summary>
    [HttpPost("{jobId:guid}/items/batch")]
    public async Task<IActionResult> UpsertItems(Guid jobId, [FromBody] ItemStatusBatchRequest request, CancellationToken ct)
    {
        foreach (var dto in request.Items)
        {
            var type = Enum.TryParse<MigrationItemType>(dto.ItemType, out var t) ? t : MigrationItemType.Mail;
            var status = Enum.TryParse<MigrationItemStatus>(dto.Status, out var s) ? s : MigrationItemStatus.Processing;

            var item = await _db.MigrationItems.FirstOrDefaultAsync(
                x => x.JobId == jobId && x.MailboxId == request.MailboxId
                     && x.SourceItemId == dto.SourceItemId && x.ItemType == type, ct);

            if (item is null)
            {
                item = new MigrationItem
                {
                    JobId = jobId,
                    MailboxId = request.MailboxId,
                    SourceItemId = dto.SourceItemId,
                    SourceFolderPath = dto.SourceFolderPath,
                    ItemType = type,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _db.MigrationItems.Add(item);
            }

            item.Status = status;
            item.DestinationItemId = dto.DestinationItemId ?? item.DestinationItemId;
            item.LastErrorCode = dto.ErrorCode;
            item.FidelityWarning = dto.WarningCode;
            item.AttemptCount += 1;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            if (status is MigrationItemStatus.Completed or MigrationItemStatus.CompletedWithWarning)
                item.CompletedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { received = request.Items.Count });
    }

    [HttpPost("{jobId:guid}/errors")]
    public async Task<IActionResult> ReportErrors(Guid jobId, [FromBody] ErrorReportRequest request, CancellationToken ct)
    {
        foreach (var e in request.Errors)
        {
            _db.MigrationErrors.Add(new MigrationError
            {
                JobId = jobId,
                MailboxId = request.MailboxId,
                SourceItemId = e.SourceItemId,
                ItemType = Enum.TryParse<MigrationItemType>(e.ItemType, out var t) ? t : null,
                ErrorCode = e.ErrorCode,
                Message = e.Message,
                HttpStatus = e.HttpStatus,
            });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { received = request.Errors.Count });
    }

    [HttpPost("{jobId:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid jobId, [FromBody] JobStatusUpdateRequest request, CancellationToken ct)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        if (Enum.TryParse<MigrationJobStatus>(request.Status, out var status))
        {
            job.Status = status;
            if (status == MigrationJobStatus.Running) job.StartedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok();
    }

    [HttpPost("{jobId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid jobId, CancellationToken ct)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        job.CompletedAt = DateTimeOffset.UtcNow;
        var anyFailed = await _db.MigrationItems.AnyAsync(i => i.JobId == jobId && i.Status == MigrationItemStatus.Failed, ct);
        job.Status = anyFailed ? MigrationJobStatus.CompletedWithErrors : MigrationJobStatus.Completed;
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>Source item ids already handled (for resume/idempotency on the agent).</summary>
    [HttpGet("{jobId:guid}/mailboxes/{mailboxId:guid}/completed")]
    public async Task<ActionResult<string[]>> Completed(Guid jobId, Guid mailboxId, CancellationToken ct)
    {
        var done = await _db.MigrationItems
            .Where(i => i.JobId == jobId && i.MailboxId == mailboxId
                && (i.Status == MigrationItemStatus.Completed
                    || i.Status == MigrationItemStatus.CompletedWithWarning
                    || i.Status == MigrationItemStatus.Skipped))
            .Select(i => i.SourceItemId)
            .ToArrayAsync(ct);
        return Ok(done);
    }

    /// <summary>Creates a Queued job covering every mailbox mapping (simple Phase-1 job creation).</summary>
    [HttpPost("create-from-mappings")]
    public async Task<IActionResult> CreateFromMappings([FromQuery] string? name, CancellationToken ct)
    {
        var tenant = await _db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (tenant is null) return Problem("No tenant configured.");

        var mappings = await _db.MailboxMappings.Where(m => m.TenantId == tenant.Id).ToListAsync(ct);
        if (mappings.Count == 0) return BadRequest(new { error = "No mailbox mappings to migrate." });

        var job = new MigrationJob
        {
            TenantId = tenant.Id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Migration {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : name,
            Status = MigrationJobStatus.Queued,
        };
        _db.MigrationJobs.Add(job);
        foreach (var m in mappings)
            _db.MigrationJobMailboxes.Add(new MigrationJobMailbox { JobId = job.Id, MailboxMappingId = m.Id, Status = MigrationJobStatus.Queued });

        await _db.SaveChangesAsync(ct);
        return Ok(new { jobId = job.Id, mailboxes = mappings.Count });
    }
}
