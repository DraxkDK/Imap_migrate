namespace PstMigration.Application.Abstractions;

/// <summary>
/// Supplies a bearer token for Graph calls. On the agent this is implemented by
/// calling the portal's token broker (the agent never holds the certificate).
/// </summary>
public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
