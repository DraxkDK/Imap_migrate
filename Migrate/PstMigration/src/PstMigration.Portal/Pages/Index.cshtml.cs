using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PstMigration.Domain;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Portal.Pages;

public class IndexModel : PageModel
{
    private readonly MigrationDbContext _db;
    public IndexModel(MigrationDbContext db) => _db = db;

    public int Tenants { get; private set; }
    public int Agents { get; private set; }
    public int OnlineAgents { get; private set; }
    public int PstFiles { get; private set; }
    public int Jobs { get; private set; }
    public int ItemsCompleted { get; private set; }
    public int ItemsFailed { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Tenants = await _db.Tenants.CountAsync(ct);
        Agents = await _db.Agents.CountAsync(ct);
        OnlineAgents = await _db.Agents.CountAsync(a => a.Status == AgentStatus.Online, ct);
        PstFiles = await _db.PstFiles.CountAsync(ct);
        Jobs = await _db.MigrationJobs.CountAsync(ct);
        ItemsCompleted = await _db.MigrationItems.CountAsync(
            i => i.Status == MigrationItemStatus.Completed || i.Status == MigrationItemStatus.CompletedWithWarning, ct);
        ItemsFailed = await _db.MigrationItems.CountAsync(i => i.Status == MigrationItemStatus.Failed, ct);
    }
}
