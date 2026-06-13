namespace PstMigration.Application.Abstractions;

/// <summary>Resolves the Entra app-registration details for a tenant (cert stays on the portal).</summary>
public interface IAppRegistrationProvider
{
    Task<AppRegistrationInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record AppRegistrationInfo(
    string EntraTenantId,
    string ClientId,
    string CertificateThumbprint,
    string CertificateLocation);
