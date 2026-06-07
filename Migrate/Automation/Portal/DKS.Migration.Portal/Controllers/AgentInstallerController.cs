using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class AgentInstallerController : Controller
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AgentInstallerController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<IActionResult> Index(int? batchId)
    {
        var batches = await _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Tokens.Where(t => t.IsActive))
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
        ViewBag.Batches = new SelectList(batches, "BatchId", "BatchName", batchId);

        MigrationBatch? selectedBatch = null;
        AgentToken? activeToken = null;
        if (batchId.HasValue)
        {
            selectedBatch = batches.FirstOrDefault(b => b.BatchId == batchId.Value);
            activeToken = selectedBatch?.Tokens.FirstOrDefault(t => t.IsActive);
        }
        ViewBag.SelectedBatch = selectedBatch;
        ViewBag.ActiveToken = activeToken;

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        ViewBag.BaseUrl = baseUrl;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> GenerateToken(int batchId)
    {
        var existing = await _db.AgentTokens.Where(t => t.BatchId == batchId && t.IsActive).ToListAsync();
        foreach (var t in existing) { t.IsActive = false; t.RevokedAt = DateTime.UtcNow; }

        var token = new AgentToken
        {
            BatchId = batchId,
            Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "").Replace("/", "_").Replace("+", "-")
        };
        _db.AgentTokens.Add(token);
        await _db.SaveChangesAsync();
        TempData["Success"] = "New token generated.";
        return RedirectToAction(nameof(Index), new { batchId });
    }

    public IActionResult DownloadMsi()
    {
        var placeholder = "MSI_PLACEHOLDER - Build DKSProfileAgent.msi using WiX Toolset";
        return File(System.Text.Encoding.UTF8.GetBytes(placeholder), "application/octet-stream", "DKSProfileAgent.msi");
    }

    public async Task<IActionResult> DownloadGpoScript(int batchId)
    {
        var batch = await _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Tokens.Where(t => t.IsActive))
            .FirstOrDefaultAsync(b => b.BatchId == batchId);
        if (batch == null) return NotFound();
        var token = batch.Tokens.FirstOrDefault()?.Token ?? "YOUR_TOKEN_HERE";
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var script = $@"# GPO Startup Script - DKS Profile Migration Agent
# Generated: {DateTime.Now:yyyy-MM-dd HH:mm}
# Customer: {batch.Customer?.CustomerName}

$InstallPath = ""\\\\FILESERVER\\DKS\\DKSProfileAgent.msi""
$LogPath = ""C:\\Windows\\Temp\\DKS_Install.log""

Start-Process msiexec.exe -ArgumentList @(
    ""/i"", $InstallPath, ""/qn"",
    ""APIURL={baseUrl}/api/agent"",
    ""CUSTOMERCODE={batch.Customer?.CustomerCode}"",
    ""AGENTTOKEN={token}"",
    ""MODE=EXPORT_RECONFIG_IMPORT"",
    ""/l*v"", $LogPath
) -Wait
";
        return File(System.Text.Encoding.UTF8.GetBytes(script), "text/plain",
            $"GPO_Install_{batch.Customer?.CustomerCode}.ps1");
    }

    public async Task<IActionResult> DownloadIntuneScript(int batchId)
    {
        var batch = await _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Tokens.Where(t => t.IsActive))
            .FirstOrDefaultAsync(b => b.BatchId == batchId);
        if (batch == null) return NotFound();
        var token = batch.Tokens.FirstOrDefault()?.Token ?? "YOUR_TOKEN_HERE";
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var script = $@"# Intune Win32 App Install Script
# Customer: {batch.Customer?.CustomerName}
# Generated: {DateTime.Now:yyyy-MM-dd HH:mm}

$params = '/i DKSProfileAgent.msi /qn APIURL=""{baseUrl}/api/agent"" CUSTOMERCODE=""{batch.Customer?.CustomerCode}"" AGENTTOKEN=""{token}"" MODE=""EXPORT_RECONFIG_IMPORT""'
Start-Process msiexec.exe -ArgumentList $params -Wait
";
        return File(System.Text.Encoding.UTF8.GetBytes(script), "text/plain",
            $"Intune_Install_{batch.Customer?.CustomerCode}.ps1");
    }
}
