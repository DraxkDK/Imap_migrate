using PstMigration.Application.Abstractions;
using PstMigration.Contracts;

namespace PstMigration.Agent.Services;

/// <summary>
/// Agent-side token provider: fetches a short-lived Graph token from the portal
/// (which holds the certificate) and caches it until shortly before expiry.
/// </summary>
public sealed class GraphTokenProvider : IGraphTokenProvider
{
    private readonly PortalClient _portal;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresOn;

    public GraphTokenProvider(PortalClient portal) => _portal = portal;

    /// <summary>Set by the worker after registration.</summary>
    public Guid AgentId { get; set; }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresOn.AddMinutes(-2))
            return _token;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresOn.AddMinutes(-2))
                return _token;

            var resp = await _portal.GetGraphTokenAsync(new GraphTokenRequest(AgentId, ""), cancellationToken)
                ?? throw new InvalidOperationException("Portal did not return a Graph token.");
            _token = resp.AccessToken;
            _expiresOn = resp.ExpiresOn;
            return _token;
        }
        finally { _lock.Release(); }
    }
}
