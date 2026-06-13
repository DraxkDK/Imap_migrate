using Microsoft.EntityFrameworkCore;
using PstMigration.Application.Abstractions;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Infrastructure;

/// <summary>Reads app-registration details (for the Graph token broker) from the portal DB.</summary>
public sealed class EfAppRegistrationProvider : IAppRegistrationProvider
{
    private readonly MigrationDbContext _db;

    public EfAppRegistrationProvider(MigrationDbContext db) => _db = db;

    public async Task<AppRegistrationInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var row = await _db.AppRegistrations
            .AsNoTracking()
            .Join(_db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { r, t })
            .FirstOrDefaultAsync(x => x.t.Id == tenantId, cancellationToken);

        return row is null
            ? null
            : new AppRegistrationInfo(row.t.EntraTenantId, row.r.ClientId, row.r.CertificateThumbprint, row.r.CertificateLocation);
    }
}
