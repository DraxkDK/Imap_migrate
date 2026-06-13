using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PstMigration.Domain;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Portal.Pages;

public class ReportsModel : PageModel
{
    private readonly MigrationDbContext _db;
    public ReportsModel(MigrationDbContext db) => _db = db;

    public IReadOnlyList<MigrationJob> Jobs { get; private set; } = Array.Empty<MigrationJob>();

    public async Task OnGetAsync(CancellationToken ct)
        => Jobs = await _db.MigrationJobs.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);

    public async Task<IActionResult> OnGetSummaryCsvAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        var items = await _db.MigrationItems.Where(i => i.JobId == jobId).ToListAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("JobName,Status,StartedAt,CompletedAt,Total,Completed,Warning,Skipped,Failed");
        sb.AppendLine(string.Join(',',
            Csv(job.Name), job.Status,
            job.StartedAt?.ToString("o"), job.CompletedAt?.ToString("o"),
            items.Count,
            items.Count(i => i.Status == MigrationItemStatus.Completed),
            items.Count(i => i.Status == MigrationItemStatus.CompletedWithWarning),
            items.Count(i => i.Status == MigrationItemStatus.Skipped),
            items.Count(i => i.Status == MigrationItemStatus.Failed)));
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"summary_{jobId}.csv");
    }

    public async Task<IActionResult> OnGetItemsCsvAsync(Guid jobId, CancellationToken ct)
    {
        var items = await _db.MigrationItems.Where(i => i.JobId == jobId)
            .OrderBy(i => i.SourceFolderPath).ToListAsync(ct);

        var sb = new StringBuilder();
        // Body/attachment content is intentionally never included.
        sb.AppendLine("SourceFolderPath,ItemType,SourceItemId,DestinationItemId,Status,AttemptCount,ErrorCode,FidelityWarning");
        foreach (var i in items)
        {
            sb.AppendLine(string.Join(',',
                Csv(i.SourceFolderPath), i.ItemType, Csv(i.SourceItemId), Csv(i.DestinationItemId),
                i.Status, i.AttemptCount, Csv(i.LastErrorCode), Csv(i.FidelityWarning)));
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"items_{jobId}.csv");
    }

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
    }
}
