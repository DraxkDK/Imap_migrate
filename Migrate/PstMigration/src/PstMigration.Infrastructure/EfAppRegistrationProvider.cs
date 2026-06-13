using Microsoft.EntityFrameworkCore;
using PstMigration.Application.Abstractions;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Infrastructure;

/// <summary>Reads app-registration details from the portal DB, decrypting the client secret.</summary>
public sealed class EfAppRegistrationProvider : IAppRegistrationProvider
{
    private readonly MigrationDbContext _db;
    private readonly ISecretProtector _protector;

    public EfAppRegistrationProvider(MigrationDbContext db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<AppRegistrationInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var row = await _db.AppRegistrations
            .AsNoTracking()
            .Join(_db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { r, t })
            .FirstOrDefaultAsync(x => x.t.Id == tenantId, cancellationToken);

        if (row is null) return null;

        var secret = string.IsNullOrEmpty(row.r.ClientSecretEncrypted)
            ? null
            : _protector.Unprotect(row.r.ClientSecretEncrypted);

        return new AppRegistrationInfo(
            row.t.EntraTenantId,
            row.r.ClientId,
            secret,
            row.r.CertificateThumbprint,
            row.r.CertificateLocation);
    }
}
