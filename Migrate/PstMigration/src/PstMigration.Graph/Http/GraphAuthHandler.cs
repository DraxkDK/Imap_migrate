using System.Net.Http.Headers;
using PstMigration.Application.Abstractions;

namespace PstMigration.Graph.Http;

/// <summary>Attaches the brokered Graph bearer token to every outgoing request.</summary>
public sealed class GraphAuthHandler : DelegatingHandler
{
    private readonly IGraphTokenProvider _tokenProvider;

    public GraphAuthHandler(IGraphTokenProvider tokenProvider) => _tokenProvider = tokenProvider;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
