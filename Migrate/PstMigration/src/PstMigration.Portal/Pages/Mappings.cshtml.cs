using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PstMigration.Domain;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Portal.Pages;

public class MappingsModel : PageModel
{
    private readonly MigrationDbContext _db;
    public MappingsModel(MigrationDbContext db) => _db = db;

    public IReadOnlyList<MailboxMapping> Mappings { get; private set; } = Array.Empty<MailboxMapping>();
    public IReadOnlyList<PstFile> PstFiles { get; private set; } = Array.Empty<PstFile>();
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Mappings = await _db.MailboxMappings.Include(m => m.PstFile).OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        PstFiles = await _db.PstFiles.OrderBy(p => p.Path).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostImportCsvAsync(IFormFile? csv, CancellationToken ct)
    {
        if (csv is null || csv.Length == 0)
        {
            Message = "No CSV selected.";
            return RedirectToPage();
        }

        var tenant = await _db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (tenant is null) { Message = "No tenant configured."; return RedirectToPage(); }

        using var reader = new StreamReader(csv.OpenReadStream());
        var text = await reader.ReadToEndAsync(ct);
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) { Message = "CSV has no data rows."; return RedirectToPage(); }

        var added = 0;
        // Header: PstPath,TargetMailbox,DisplayName,EmailMode,CalendarMode,ContactMode
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split(',');
            if (c.Length < 2) continue;
            var pstPath = c[0].Trim();
            var target = c[1].Trim();
            if (string.IsNullOrEmpty(pstPath) || string.IsNullOrEmpty(target)) continue;

            var pst = await _db.PstFiles.FirstOrDefaultAsync(p => p.Path == pstPath, ct);
            if (pst is null) continue; // PST must be discovered first

            if (await _db.MailboxMappings.AnyAsync(m => m.PstFileId == pst.Id && m.TargetMailbox == target, ct))
                continue;

            _db.MailboxMappings.Add(new MailboxMapping
            {
                TenantId = tenant.Id,
                PstFileId = pst.Id,
                TargetMailbox = target,
                DisplayName = c.Length > 2 ? c[2].Trim() : null,
                EmailMode = ParseEnum(c, 3, EmailMigrationMode.MetadataOnly),
                CalendarMode = ParseEnum(c, 4, CalendarMigrationMode.Safe),
                ContactMode = ParseEnum(c, 5, ContactMigrationMode.Enabled),
            });
            added++;
        }
        await _db.SaveChangesAsync(ct);
        Message = $"Imported {added} mapping(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateJobAsync(CancellationToken ct)
    {
        var tenant = await _db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        var mappings = tenant is null
            ? new List<MailboxMapping>()
            : await _db.MailboxMappings.Where(m => m.TenantId == tenant.Id).ToListAsync(ct);
        if (mappings.Count == 0) { Message = "No mappings to migrate."; return RedirectToPage(); }

        var job = new MigrationJob
        {
            TenantId = tenant!.Id,
            Name = $"Migration {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            Status = MigrationJobStatus.Queued,
        };
        _db.MigrationJobs.Add(job);
        foreach (var m in mappings)
            _db.MigrationJobMailboxes.Add(new MigrationJobMailbox { JobId = job.Id, MailboxMappingId = m.Id, Status = MigrationJobStatus.Queued });
        await _db.SaveChangesAsync(ct);

        Message = $"Job created for {mappings.Count} mailbox(es). An agent will pick it up.";
        return RedirectToPage();
    }

    private static T ParseEnum<T>(string[] cols, int index, T fallback) where T : struct
        => cols.Length > index && Enum.TryParse<T>(cols[index].Trim(), ignoreCase: true, out var v) ? v : fallback;
}
