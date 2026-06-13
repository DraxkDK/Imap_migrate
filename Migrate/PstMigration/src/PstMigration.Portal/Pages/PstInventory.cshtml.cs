using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Portal.Pages;

public class PstInventoryModel : PageModel
{
    private readonly MigrationDbContext _db;
    public PstInventoryModel(MigrationDbContext db) => _db = db;

    public IReadOnlyList<PstFile> Files { get; private set; } = Array.Empty<PstFile>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Files = await _db.PstFiles
            .Include(p => p.Agent)
            .OrderByDescending(p => p.LastScannedAt)
            .ToListAsync(ct);
    }
}
