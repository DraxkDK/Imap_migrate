namespace PstMigration.Application.Abstractions;

/// <summary>Resolves the Entra app-registration details for a tenant.</summary>
public interface IAppRegistrationProvider
{
    Task<AppRegistrationInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// App-only credential details. Use <see cref="ClientSecret"/> when set, otherwise
/// the certificate. The cert private key / decrypted secret stay on the portal.
/// </summary>
public sealed record AppRegistrationInfo(
    string EntraTenantId,
    string ClientId,
    string? ClientSecret,
    string? CertificateThumbprint,
    string? CertificateLocation);
