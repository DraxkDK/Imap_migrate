using PstMigration.Agent.Services;
using PstMigration.Contracts;

namespace PstMigration.Agent;

/// <summary>
/// Phase 1 worker: registers the agent with the portal then sends periodic heartbeats.
/// PST scanning and migration are added in later phases. The PST never leaves the host.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly PortalClient _portal;
    private readonly PstScanner _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentWorker> _logger;

    private Guid _agentId;
    private int _heartbeatSeconds = 30;

    public AgentWorker(PortalClient portal, PstScanner scanner, IConfiguration config, ILogger<AgentWorker> logger)
    {
        _portal = portal;
        _scanner = scanner;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registrationToken = _config["Portal:RegistrationToken"] ?? "";
        var machineName = Environment.MachineName;

        // Register (retry until success or shutdown).
        while (!stoppingToken.IsCancellationRequested && _agentId == Guid.Empty)
        {
            try
            {
                var resp = await _portal.RegisterAsync(
                    new AgentRegistrationRequest(registrationToken, machineName,
                        AgentVersion: GetVersion(), OsVersion: Environment.OSVersion.VersionString),
                    stoppingToken);

                if (resp is not null)
                {
                    _agentId = resp.AgentId;
                    _heartbeatSeconds = Math.Max(5, resp.HeartbeatIntervalSeconds);
                    _logger.LogInformation("Registered with portal as agent {AgentId} (tenant {Tenant})", _agentId, resp.TenantDomain);
                }
                else
                {
                    _logger.LogWarning("Registration rejected (check Portal:RegistrationToken). Retrying in 30s.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed; retrying in 30s.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // One-time PST discovery scan (metadata reported to portal; PST stays local).
        try
        {
            var cfg = await _portal.GetConfigurationAsync(stoppingToken);
            var folders = cfg?.DefaultPstFolders
                ?? _config.GetSection("Agent:PstFolders").Get<string[]>()
                ?? Array.Empty<string>();
            if (folders.Length > 0)
            {
                _logger.LogInformation("Scanning {Count} PST folder(s)...", folders.Length);
                await _scanner.ScanAsync(_agentId, folders, stoppingToken);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PST scan failed.");
        }

        // Heartbeat loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _portal.HeartbeatAsync(
                    new AgentHeartbeatRequest(_agentId, CurrentJobId: null, ActiveItems: 0,
                        CpuPercent: null, FreeDiskBytes: GetFreeDisk()),
                    stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), stoppingToken);
        }
    }

    private static string GetVersion()
        => typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private static long? GetFreeDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }
}
