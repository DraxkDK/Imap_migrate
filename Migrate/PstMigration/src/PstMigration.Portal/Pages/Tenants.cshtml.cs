using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PstMigration.Application.Security;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Portal.Pages;

public class TenantsModel : PageModel
{
    private readonly MigrationDbContext _db;
    private readonly PstMigration.Application.Abstractions.ISecretProtector _protector;
    public TenantsModel(MigrationDbContext db, PstMigration.Application.Abstractions.ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public IReadOnlyList<Tenant> Tenants { get; private set; } = Array.Empty<Tenant>();
    [TempData] public string? Message { get; set; }
    [TempData] public string? NewToken { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
        => Tenants = await _db.Tenants.Include(t => t.AppRegistration).OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(string name, string domain, string entraTenantId,
        string clientId, string? clientSecret, string? certThumbprint, string? certLocation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain))
        {
            Message = "Name and domain are required.";
            return RedirectToPage();
        }

        var token = NewRegistrationToken();
        var tenant = new Tenant
        {
            Name = name.Trim(),
            TenantDomain = domain.Trim(),
            EntraTenantId = (entraTenantId ?? "").Trim(),
            RegistrationTokenHash = TokenHasher.Sha256Hex(token),
        };
        _db.Tenants.Add(tenant);
        _db.AppRegistrations.Add(new AppRegistration
        {
            TenantId = tenant.Id,
            ClientId = (clientId ?? "").Trim(),
            ClientSecretEncrypted = string.IsNullOrWhiteSpace(clientSecret) ? null : _protector.Protect(clientSecret.Trim()),
            CertificateThumbprint = (certThumbprint ?? "").Trim(),
            CertificateLocation = string.IsNullOrWhiteSpace(certLocation) ? "" : certLocation.Trim(),
        });
        await _db.SaveChangesAsync(ct);

        NewToken = token;
        Message = $"Tenant '{tenant.Name}' created. Copy the registration token now — it is shown only once.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id, string name, string domain, string entraTenantId,
        string clientId, string? clientSecret, string? certThumbprint, string? certLocation, CancellationToken ct)
    {
        var tenant = await _db.Tenants.Include(t => t.AppRegistration).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return RedirectToPage();

        tenant.Name = name.Trim();
        tenant.TenantDomain = domain.Trim();
        tenant.EntraTenantId = (entraTenantId ?? "").Trim();
        tenant.AppRegistration ??= new AppRegistration { TenantId = tenant.Id };
        tenant.AppRegistration.ClientId = (clientId ?? "").Trim();
        tenant.AppRegistration.CertificateThumbprint = (certThumbprint ?? "").Trim();
        tenant.AppRegistration.CertificateLocation = (certLocation ?? "").Trim();
        // Only replace the secret when a new one is typed (blank = keep existing).
        if (!string.IsNullOrWhiteSpace(clientSecret))
            tenant.AppRegistration.ClientSecretEncrypted = _protector.Protect(clientSecret.Trim());
        if (_db.Entry(tenant.AppRegistration).State == EntityState.Detached)
            _db.AppRegistrations.Add(tenant.AppRegistration);

        await _db.SaveChangesAsync(ct);
        Message = "Tenant updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegenTokenAsync(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return RedirectToPage();
        var token = NewRegistrationToken();
        tenant.RegistrationTokenHash = TokenHasher.Sha256Hex(token);
        await _db.SaveChangesAsync(ct);
        NewToken = token;
        Message = $"New registration token for '{tenant.Name}'. Copy it now — shown only once.";
        return RedirectToPage();
    }

    private static string NewRegistrationToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
