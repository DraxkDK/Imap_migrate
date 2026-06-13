using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PstMigration.Application.Abstractions;
using PstMigration.Application.Security;
using PstMigration.Contracts;
using PstMigration.Domain;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;

namespace PstMigration.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly MigrationDbContext _db;
    private readonly IConfiguration _config;
    private readonly IGraphTokenBroker _tokenBroker;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(MigrationDbContext db, IConfiguration config,
        IGraphTokenBroker tokenBroker, ILogger<AgentsController> logger)
    {
        _db = db;
        _config = config;
        _tokenBroker = tokenBroker;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AgentRegistrationResponse>> Register(
        [FromBody] AgentRegistrationRequest request, CancellationToken ct)
    {
        var expected = _config["Agent:RegistrationToken"];
        if (string.IsNullOrEmpty(expected) ||
            !TokenHasher.FixedTimeEquals(TokenHasher.Sha256Hex(request.RegistrationToken), TokenHasher.Sha256Hex(expected)))
        {
            _logger.LogWarning("Agent registration rejected for machine {Machine}", request.MachineName);
            return Unauthorized(new { error = "Invalid registration token." });
        }

        var tenant = await _db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (tenant is null) return Problem("No tenant configured.");

        var agent = await _db.Agents.FirstOrDefaultAsync(
            a => a.TenantId == tenant.Id && a.MachineName == request.MachineName, ct);
        if (agent is null)
        {
            agent = new Agent { TenantId = tenant.Id, MachineName = request.MachineName };
            _db.Agents.Add(agent);
        }
        agent.AgentVersion = request.AgentVersion;
        agent.OsVersion = request.OsVersion;
        agent.Status = AgentStatus.Registered;
        agent.RegistrationTokenHash = TokenHasher.Sha256Hex(request.RegistrationToken);
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var interval = _config.GetValue<int?>("Agent:HeartbeatIntervalSeconds") ?? 30;
        return Ok(new AgentRegistrationResponse(agent.Id, tenant.TenantDomain, interval));
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] AgentHeartbeatRequest request, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);
        if (agent is null) return NotFound();
        if (agent.Status == AgentStatus.Revoked) return StatusCode(StatusCodes.Status403Forbidden, new { error = "Agent revoked." });

        agent.Status = AgentStatus.Online;
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        _db.AgentHeartbeats.Add(new AgentHeartbeat
        {
            AgentId = agent.Id,
            CurrentJobId = request.CurrentJobId,
            ActiveItems = request.ActiveItems,
            CpuPercent = request.CpuPercent,
            FreeDiskBytes = request.FreeDiskBytes,
        });
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpGet("{agentId:guid}/jobs/next")]
    public async Task<IActionResult> NextJob(Guid agentId, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return NotFound();
        // Phase 1: job assignment is not implemented yet.
        return NoContent();
    }

    [HttpPost("graph-token")]
    public async Task<ActionResult<GraphTokenResponse>> GraphToken([FromBody] GraphTokenRequest request, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);
        if (agent is null) return NotFound();
        if (agent.Status == AgentStatus.Revoked) return StatusCode(StatusCodes.Status403Forbidden, new { error = "Agent revoked." });

        var token = await _tokenBroker.AcquireTokenAsync(agent.TenantId, ct);
        return Ok(new GraphTokenResponse(token.AccessToken, token.ExpiresOn, token.Resource));
    }
}
