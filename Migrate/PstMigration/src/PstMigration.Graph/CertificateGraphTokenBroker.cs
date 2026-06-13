using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using PstMigration.Application.Abstractions;

namespace PstMigration.Graph;

/// <summary>
/// Acquires app-only Graph tokens using a certificate that lives only on the portal.
/// The agent never holds the certificate; it requests a short-lived token from the portal.
/// </summary>
public sealed class CertificateGraphTokenBroker : IGraphTokenBroker
{
    private static readonly string[] Scopes = { "https://graph.microsoft.com/.default" };

    private readonly IAppRegistrationProvider _provider;
    private readonly ILogger<CertificateGraphTokenBroker> _logger;

    public CertificateGraphTokenBroker(IAppRegistrationProvider provider, ILogger<CertificateGraphTokenBroker> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task<GraphAccessToken> AcquireTokenAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var reg = await _provider.GetAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"No app registration configured for tenant {tenantId}.");

        using var cert = LoadCertificate(reg.CertificateLocation, reg.CertificateThumbprint);
        var credential = new ClientCertificateCredential(reg.EntraTenantId, reg.ClientId, cert,
            new ClientCertificateCredentialOptions { SendCertificateChain = true });

        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        _logger.LogInformation("Acquired Graph token for tenant {TenantId} (expires {Expiry:o})", tenantId, token.ExpiresOn);
        return new GraphAccessToken(token.Token, token.ExpiresOn);
    }

    /// <summary>
    /// Loads the signing certificate from either a Windows cert store
    /// ("store:CurrentUser/My") or a PFX file ("file:/path/cert.pfx").
    /// </summary>
    private static X509Certificate2 LoadCertificate(string location, string thumbprint)
    {
        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = location["file:".Length..];
            return new X509Certificate2(path); // password handling added when ProtectedCertPasswordRef is wired up
        }

        // store:CurrentUser/My  or  store:LocalMachine/My
        var spec = location.StartsWith("store:", StringComparison.OrdinalIgnoreCase)
            ? location["store:".Length..]
            : "CurrentUser/My";
        var parts = spec.Split('/', 2);
        var storeLocation = Enum.Parse<StoreLocation>(parts[0], ignoreCase: true);
        var storeName = parts.Length > 1 ? Enum.Parse<StoreName>(parts[1], ignoreCase: true) : StoreName.My;

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (found.Count == 0)
            throw new InvalidOperationException($"Certificate {thumbprint} not found in {storeLocation}/{storeName}.");
        return found[0];
    }
}
