using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers.Api;

// Agents run on end-user PCs and cannot log into the portal; they authenticate
// per-request by token (see Register). Exempt this controller from the global
// login requirement.
[AllowAnonymous]
[ApiController]
[Route("api/agent")]
public class AgentApiController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DKS.Migration.Portal.Services.GraphTokenService _graphToken;

    public AgentApiController(AppDbContext db, DKS.Migration.Portal.Services.GraphTokenService graphToken)
    {
        _db = db;
        _graphToken = graphToken;
    }

    // POST /api/agent/graph-token — agent exchanges its token for a short-lived
    // Microsoft Graph token (portal holds the client secret/cert).
    [HttpPost("graph-token")]
    public async Task<IActionResult> GraphToken([FromBody] GraphTokenRequest req)
    {
        var token = await _db.AgentTokens
            .Include(t => t.Batch).ThenInclude(b => b!.Customer)
            .FirstOrDefaultAsync(t => t.Token == req.AgentToken && t.IsActive);
        if (token?.Batch?.Customer is null)
            return Unauthorized(new { error = "Invalid or inactive token." });

        try
        {
            var (accessToken, expiresOn) = await _graphToken.GetTokenAsync(token.Batch.Customer, HttpContext.RequestAborted);
            return Ok(new { accessToken, expiresOn });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // POST /api/agent/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var token = await _db.AgentTokens
            .Include(t => t.Batch).ThenInclude(b => b!.Customer)
            .FirstOrDefaultAsync(t => t.Token == req.AgentToken && t.IsActive);

        if (token == null) return Unauthorized(new { error = "Invalid or inactive token." });

        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.BatchId == token.BatchId && d.ComputerName == req.ComputerName);

        if (device == null)
        {
            device = new Device
            {
                BatchId = token.BatchId,
                ComputerName = req.ComputerName,
                WindowsUsername = req.WindowsUsername,
                AgentVersion = req.AgentVersion,
                OsVersion = req.OsVersion,
                CurrentStatus = DeviceStatus.AgentInstalled,
                LastCheckIn = DateTime.UtcNow
            };
            _db.Devices.Add(device);
        }
        else
        {
            device.AgentVersion = req.AgentVersion;
            device.OsVersion = req.OsVersion;
            device.LastCheckIn = DateTime.UtcNow;
            if (device.CurrentStatus == DeviceStatus.Pending)
                device.CurrentStatus = DeviceStatus.AgentInstalled;
        }

        // Look up user mapping by ComputerName + WindowsUsername
        // Match user mapping: ComputerName + WindowsUsername phải khớp cả hai
        // → tránh nhầm khi nhiều device có cùng WindowsUsername (nvana trên nhiều máy)
        var userMapping = await _db.Users.FirstOrDefaultAsync(u =>
            u.BatchId == token.BatchId &&
            u.ComputerName == req.ComputerName &&
            u.WindowsUsername == req.WindowsUsername);

        // Fallback: chỉ match ComputerName nếu mapping không có WindowsUsername
        if (userMapping == null)
            userMapping = await _db.Users.FirstOrDefaultAsync(u =>
                u.BatchId == token.BatchId &&
                u.ComputerName == req.ComputerName &&
                (u.WindowsUsername == null || u.WindowsUsername == ""));

        if (userMapping != null)
        {
            device.OldEmail = userMapping.OldEmail ?? device.OldEmail;
            device.NewMailbox = userMapping.TargetMailbox ?? userMapping.NewEmail ?? device.NewMailbox;
        }

        await _db.SaveChangesAsync();
        return Ok(new
        {
            deviceId = device.DeviceId,
            batchId = token.BatchId,
            customerCode = token.Batch?.Customer?.CustomerCode,
            mode = token.Batch?.Mode.ToString(),
            backupPstPath = token.Batch?.BackupPstPath,
            cutoverTime = token.Batch?.CutoverTime,
            closeOutlookAutomatically = token.Batch?.CloseOutlookAutomatically ?? true,
            importTargetFolder = token.Batch?.ImportTargetFolder,
            rollbackEnabled = token.Batch?.RollbackEnabled ?? true,
            // User mapping — agent dùng để cấu hình AutoDiscover
            oldEmail = userMapping?.OldEmail,
            newEmail = userMapping?.NewEmail,
            targetMailbox = userMapping?.TargetMailbox ?? userMapping?.NewEmail,
            fullName = userMapping?.FullName
        });
    }

    // POST /api/agent/checkin
    [HttpPost("checkin")]
    public async Task<IActionResult> CheckIn([FromBody] CheckInRequest req)
    {
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return NotFound();

        device.LastCheckIn = DateTime.UtcNow;
        device.CurrentStatus = req.Status;
        device.OutlookVersion = req.OutlookVersion ?? device.OutlookVersion;
        device.CurrentProfile = req.CurrentProfile ?? device.CurrentProfile;
        device.OldAccountType = req.OldAccountType ?? device.OldAccountType;
        device.OldEmail = req.OldEmail ?? device.OldEmail;
        device.NewMailbox = req.NewMailbox ?? device.NewMailbox;
        if (req.ErrorMessage != null) device.ErrorMessage = req.ErrorMessage;

        // Pop pending command — clear after returning so it runs once
        var command = device.PendingCommand;
        device.PendingCommand = null;

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, pendingCommand = command });
    }

    // POST /api/agent/log
    [HttpPost("log")]
    public async Task<IActionResult> Log([FromBody] LogRequest req)
    {
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return NotFound();

        var log = new DeviceLog
        {
            DeviceId = req.DeviceId,
            Step = req.Step,
            Level = req.Level,
            Message = req.Message,
            CreatedAt = DateTime.UtcNow
        };
        _db.Logs.Add(log);
        await _db.SaveChangesAsync();
        return Ok(new { logId = log.LogId });
    }

    // POST /api/agent/pst
    [HttpPost("pst")]
    public async Task<IActionResult> ReportPst([FromBody] PstReportRequest req)
    {
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return NotFound();

        var existing = await _db.PstFiles.FirstOrDefaultAsync(p =>
            p.DeviceId == req.DeviceId && p.SourcePath == req.SourcePath);

        if (existing == null)
        {
            existing = new PstFile
            {
                DeviceId = req.DeviceId,
                FileName = Path.GetFileName(req.SourcePath),
                SourcePath = req.SourcePath
            };
            _db.PstFiles.Add(existing);
        }

        existing.BackupPath = req.BackupPath ?? existing.BackupPath;
        existing.SizeBytes = req.SizeBytes;
        existing.Sha256 = req.Sha256 ?? existing.Sha256;
        existing.ExportStatus = req.ExportStatus;
        existing.ImportStatus = req.ImportStatus;
        if (req.ExportStatus == PstExportStatus.Exported) existing.ExportedAt = DateTime.UtcNow;
        if (req.ImportStatus == PstImportStatus.Imported) existing.ImportedAt = DateTime.UtcNow;

        device.PstCount = await _db.PstFiles.CountAsync(p => p.DeviceId == req.DeviceId) + (existing.PstId == 0 ? 1 : 0);
        device.TotalPstSizeBytes = await _db.PstFiles.Where(p => p.DeviceId == req.DeviceId).SumAsync(p => p.SizeBytes);

        await _db.SaveChangesAsync();
        return Ok(new { pstId = existing.PstId });
    }

    // GET /api/agent/config/{deviceId}
    [HttpGet("config/{deviceId}")]
    public async Task<IActionResult> GetConfig(int deviceId)
    {
        var device = await _db.Devices
            .Include(d => d.Batch)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return NotFound();
        var batch = device.Batch!;
        return Ok(new
        {
            batchId = batch.BatchId,
            mode = batch.Mode.ToString(),
            backupPstPath = batch.BackupPstPath,
            cutoverTime = batch.CutoverTime,
            closeOutlookAutomatically = batch.CloseOutlookAutomatically,
            importTargetFolder = batch.ImportTargetFolder,
            rollbackEnabled = batch.RollbackEnabled
        });
    }
}

public record GraphTokenRequest(string AgentToken);
public record RegisterRequest(string AgentToken, string ComputerName, string? WindowsUsername, string? AgentVersion, string? OsVersion);
public record CheckInRequest(int DeviceId, DeviceStatus Status, string? OutlookVersion, string? CurrentProfile, string? OldAccountType, string? OldEmail, string? NewMailbox, string? ErrorMessage);
public record LogRequest(int DeviceId, string? Step, DKS.Migration.Portal.Models.LogLevel Level, string Message);
public record PstReportRequest(int DeviceId, string SourcePath, string? BackupPath, long SizeBytes, string? Sha256, PstExportStatus ExportStatus, PstImportStatus ImportStatus);
