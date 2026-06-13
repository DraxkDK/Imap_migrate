using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using DKS.Migration.Portal.Models;

namespace DKS.Migration.Portal.Services;

/// <summary>
/// Acquires app-only Microsoft Graph tokens for a customer's tenant. The client
/// secret (decrypted) / certificate stays on the portal; the agent only receives
/// a short-lived token.
/// </summary>
public sealed class GraphTokenService
{
    private static readonly string[] Scopes = { "https://graph.microsoft.com/.default" };
    private readonly SecretProtector _protector;

    public GraphTokenService(SecretProtector protector) => _protector = protector;

    public async Task<(string Token, DateTimeOffset ExpiresOn)> GetTokenAsync(Customer c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.EntraTenantId) || string.IsNullOrWhiteSpace(c.GraphClientId))
            throw new InvalidOperationException($"Customer '{c.CustomerName}' has no Entra tenant/client configured.");

        TokenCredential credential;
        X509Certificate2? cert = null;

        if (!string.IsNullOrEmpty(c.ClientSecretEncrypted))
        {
            var secret = _protector.Unprotect(c.ClientSecretEncrypted);
            credential = new ClientSecretCredential(c.EntraTenantId, c.GraphClientId, secret);
        }
        else if (!string.IsNullOrEmpty(c.CertThumbprint) || !string.IsNullOrEmpty(c.CertLocation))
        {
            cert = LoadCertificate(c.CertLocation ?? "store:CurrentUser/My", c.CertThumbprint ?? "");
            credential = new ClientCertificateCredential(c.EntraTenantId, c.GraphClientId, cert,
                new ClientCertificateCredentialOptions { SendCertificateChain = true });
        }
        else
        {
            throw new InvalidOperationException($"Customer '{c.CustomerName}' has no client secret or certificate.");
        }

        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            return (token.Token, token.ExpiresOn);
        }
        finally { cert?.Dispose(); }
    }

    private static X509Certificate2 LoadCertificate(string location, string thumbprint)
    {
        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return new X509Certificate2(location["file:".Length..]);

        var spec = location.StartsWith("store:", StringComparison.OrdinalIgnoreCase) ? location["store:".Length..] : "CurrentUser/My";
        var parts = spec.Split('/', 2);
        var storeLocation = Enum.Parse<StoreLocation>(parts[0], true);
        var storeName = parts.Length > 1 ? Enum.Parse<StoreName>(parts[1], true) : StoreName.My;
        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        if (found.Count == 0) throw new InvalidOperationException($"Certificate {thumbprint} not found.");
        return found[0];
    }
}
