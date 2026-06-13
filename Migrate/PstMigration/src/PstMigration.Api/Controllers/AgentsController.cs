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
        // Match the registration token to a tenant (multi-tenant SaaS).
        var hash = TokenHasher.Sha256Hex(request.RegistrationToken ?? "");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(
            t => t.IsActive && t.RegistrationTokenHash == hash, ct);
        if (tenant is null)
        {
            _logger.LogWarning("Agent registration rejected for machine {Machine}", request.MachineName);
            return Unauthorized(new { error = "Invalid registration token." });
        }

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

        var job = await _db.MigrationJobs
            .Where(j => j.TenantId == agent.TenantId
                        && (j.Status == MigrationJobStatus.Ready || j.Status == MigrationJobStatus.Queued))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job is null) return NoContent();

        var mailboxes = await _db.MigrationJobMailboxes
            .Where(m => m.JobId == job.Id)
            .Join(_db.MailboxMappings, jm => jm.MailboxMappingId, mm => mm.Id, (jm, mm) => new { jm, mm })
            .Join(_db.PstFiles, x => x.mm.PstFileId, p => p.Id, (x, p) => new JobMailboxDto(
                x.jm.Id, p.Path, x.mm.TargetMailbox, x.mm.RootFolderName,
                x.mm.EmailMode.ToString(), x.mm.CalendarMode.ToString(), x.mm.ContactMode.ToString()))
            .ToListAsync(ct);

        job.Status = MigrationJobStatus.Running;
        job.AssignedAgentId = agentId;
        job.StartedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new JobAssignmentDto(job.Id, job.Name, mailboxes));
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

    /// <summary>Receives PST discovery inventory (metadata only — never PST content).</summary>
    [HttpPost("pst-inventory")]
    public async Task<IActionResult> PstInventory([FromBody] PstInventoryRequest req, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == req.AgentId, ct);
        if (agent is null) return NotFound();

        var pst = await _db.PstFiles
            .Include(p => p.Folders)
            .FirstOrDefaultAsync(p => p.AgentId == req.AgentId && p.Sha256 == req.Sha256, ct);

        if (pst is null)
        {
            pst = new PstFile { AgentId = req.AgentId, Path = req.Path, Sha256 = req.Sha256 };
            _db.PstFiles.Add(pst);
        }

        pst.Path = req.Path;
        pst.SizeBytes = req.SizeBytes;
        pst.IsUnicode = req.IsUnicode;
        pst.IsCorrupted = req.IsCorrupted;
        pst.FolderCount = req.FolderCount;
        pst.MailCount = req.MailCount;
        pst.ContactCount = req.ContactCount;
        pst.CalendarCount = req.CalendarCount;
        pst.LastScannedAt = DateTimeOffset.UtcNow;

        // Replace the folder inventory snapshot.
        if (pst.Folders.Count > 0)
            _db.PstFolders.RemoveRange(pst.Folders);
        foreach (var f in req.Folders)
        {
            pst.Folders.Add(new PstFolder
            {
                SourceFolderId = f.SourceFolderId,
                SourceFolderPath = f.SourceFolderPath,
                DisplayName = f.DisplayName,
                ParentSourceFolderId = f.ParentSourceFolderId,
                ItemCount = f.ItemCount,
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("PST inventory stored: {Path} ({Folders} folders, {Mail} mail)", req.Path, req.FolderCount, req.MailCount);
        return Ok(new { pstFileId = pst.Id });
    }
}
