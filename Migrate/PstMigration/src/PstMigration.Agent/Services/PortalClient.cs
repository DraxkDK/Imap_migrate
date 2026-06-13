using System.Net.Http.Json;
using PstMigration.Contracts;

namespace PstMigration.Agent.Services;

/// <summary>Typed HTTP client for the agent-to-portal API. Sends metadata only.</summary>
public sealed class PortalClient
{
    private readonly HttpClient _http;

    public PortalClient(HttpClient http) => _http = http;

    public async Task<AgentRegistrationResponse?> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/agents/register", request, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AgentRegistrationResponse>(cancellationToken: ct);
    }

    public async Task<bool> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/agents/heartbeat", request, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<AgentConfigurationDto?> GetConfigurationAsync(CancellationToken ct)
        => await _http.GetFromJsonAsync<AgentConfigurationDto>("api/configuration/agent", ct);

    public async Task<GraphTokenResponse?> GetGraphTokenAsync(GraphTokenRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/agents/graph-token", request, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<GraphTokenResponse>(cancellationToken: ct);
    }

    public async Task<bool> ReportInventoryAsync(PstInventoryRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/agents/pst-inventory", request, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<JobAssignmentDto?> GetNextJobAsync(Guid agentId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"api/agents/{agentId}/jobs/next", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JobAssignmentDto>(cancellationToken: ct);
    }

    public async Task UpdateJobStatusAsync(Guid jobId, string status, CancellationToken ct)
        => await _http.PostAsJsonAsync($"api/jobs/{jobId}/status", new JobStatusUpdateRequest(jobId, status), ct);

    public async Task ReportItemsAsync(ItemStatusBatchRequest request, CancellationToken ct)
        => await _http.PostAsJsonAsync($"api/jobs/{request.JobId}/items/batch", request, ct);

    public async Task CompleteJobAsync(Guid jobId, CancellationToken ct)
        => await _http.PostAsync($"api/jobs/{jobId}/complete", content: null, ct);

    public async Task<HashSet<string>> GetCompletedSourceIdsAsync(Guid jobId, Guid mailboxId, CancellationToken ct)
    {
        var ids = await _http.GetFromJsonAsync<string[]>(
            $"api/jobs/{jobId}/mailboxes/{mailboxId}/completed", ct);
        return new HashSet<string>(ids ?? Array.Empty<string>(), StringComparer.Ordinal);
    }
}
