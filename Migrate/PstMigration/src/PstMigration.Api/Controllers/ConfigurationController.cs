using Microsoft.AspNetCore.Mvc;
using PstMigration.Contracts;

namespace PstMigration.Api.Controllers;

[ApiController]
[Route("api/configuration")]
public class ConfigurationController : ControllerBase
{
    private readonly IConfiguration _config;

    public ConfigurationController(IConfiguration config) => _config = config;

    [HttpGet("agent")]
    public ActionResult<AgentConfigurationDto> GetAgentConfiguration()
    {
        var folders = _config.GetSection("Agent:DefaultPstFolders").Get<string[]>()
            ?? new[] { @"C:\ProgramData\DKS\PST" };

        return Ok(new AgentConfigurationDto(
            HeartbeatIntervalSeconds: _config.GetValue<int?>("Agent:HeartbeatIntervalSeconds") ?? 30,
            MaxConcurrentRequestsPerMailbox: _config.GetValue<int?>("Agent:MaxConcurrentRequestsPerMailbox") ?? 2,
            ScanIntervalMinutes: _config.GetValue<int?>("Agent:ScanIntervalMinutes") ?? 60,
            DefaultPstFolders: folders));
    }
}
