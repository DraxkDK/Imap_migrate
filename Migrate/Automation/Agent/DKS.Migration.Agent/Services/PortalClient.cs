using DKS.Migration.Agent.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace DKS.Migration.Agent.Services;

public class PortalClient
{
    private readonly HttpClient _http;
    private readonly AgentConfig _config;
    private readonly ILogger<PortalClient> _logger;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public PortalClient(AgentConfig config, ILogger<PortalClient> logger)
    {
        _config = config;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/") };
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<DeviceState?> RegisterAsync(string computerName, string? windowsUser, string? osVersion)
    {
        try
        {
            var payload = new
            {
                agentToken = _config.AgentToken,
                computerName,
                windowsUsername = windowsUser,
                agentVersion = "1.0.0",
                osVersion
            };
            var resp = await _http.PostAsJsonAsync("register", payload);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Register failed: {Status}", resp.StatusCode);
                return null;
            }
            var result = await resp.Content.ReadFromJsonAsync<RegisterResponse>(_json);
            if (result == null) return null;

            return new DeviceState
            {
                DeviceId = result.DeviceId,
                BatchId = result.BatchId,
                BackupPstPath = result.BackupPstPath,
                CloseOutlookAutomatically = result.CloseOutlookAutomatically,
                ImportTargetFolder = result.ImportTargetFolder,
                RollbackEnabled = result.RollbackEnabled,
                CutoverTime = result.CutoverTime,
                OldEmail = result.OldEmail,
                NewEmail = result.NewEmail,
                TargetMailbox = result.TargetMailbox,
                FullName = result.FullName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration error");
            return null;
        }
    }

    public async Task<string?> CheckInAsync(int deviceId, string status, string? outlookVersion = null,
        string? currentProfile = null, string? oldAccountType = null,
        string? oldEmail = null, string? newMailbox = null, string? errorMessage = null)
    {
        try
        {
            var payload = new { deviceId, status, outlookVersion, currentProfile, oldAccountType, oldEmail, newMailbox, errorMessage };
            var resp = await _http.PostAsJsonAsync("checkin", payload);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<CheckInResponse>(_json);
                return result?.PendingCommand;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CheckIn failed (non-fatal)");
        }
        return null;
    }

    private record CheckInResponse(bool Ok, string? PendingCommand);

    public async Task<string?> GetGraphTokenAsync()
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("graph-token", new { agentToken = _config.AgentToken });
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Graph token request failed: {Status}", resp.StatusCode);
                return null;
            }
            var result = await resp.Content.ReadFromJsonAsync<GraphTokenResponse>(_json);
            return result?.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph token error");
            return null;
        }
    }

    private record GraphTokenResponse(string AccessToken, DateTimeOffset ExpiresOn);

    public async Task LogAsync(int deviceId, string step, string level, string message)
    {
        try
        {
            await _http.PostAsJsonAsync("log", new { deviceId, step, level, message });
        }
        catch { /* best effort */ }
    }

    public async Task ReportPstAsync(int deviceId, PstFileInfo pst, string exportStatus, string importStatus)
    {
        try
        {
            var payload = new
            {
                deviceId,
                sourcePath = pst.SourcePath,
                backupPath = pst.BackupPath,
                sizeBytes = pst.SizeBytes,
                sha256 = pst.Sha256,
                exportStatus,
                importStatus
            };
            await _http.PostAsJsonAsync("pst", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportPst failed (non-fatal)");
        }
    }

    private record RegisterResponse(int DeviceId, int BatchId, string? BackupPstPath,
        bool CloseOutlookAutomatically, string? ImportTargetFolder, bool RollbackEnabled, DateTime? CutoverTime,
        string? OldEmail, string? NewEmail, string? TargetMailbox, string? FullName);
}
