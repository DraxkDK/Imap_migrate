using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DKS.Migration.Portal.Controllers;

public class AgentInstallerController : Controller
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public AgentInstallerController(AppDbContext db, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        _config = config;
        _env = env;
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
        ViewBag.ModeValue = selectedBatch != null ? GetModeValue(selectedBatch.Mode) : null;
        ViewBag.StandaloneExePath = ResolveStandaloneAgentExePath();

        ViewBag.BaseUrl = BuildBaseUrl();
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
        // Serve the real MSI if it has been built and dropped into wwwroot/agent.
        var msiPath = Path.Combine(_env.WebRootPath, "agent", "DKSProfileAgent.msi");
        if (System.IO.File.Exists(msiPath))
            return PhysicalFile(msiPath, "application/octet-stream", "DKSProfileAgent.msi");

        var placeholder = "MSI not built yet. Build it from "
            + "Migrate/Automation/Agent/Installer (see Installer/README.md), then copy "
            + "DKSProfileAgent.msi into wwwroot/agent/.";
        return File(System.Text.Encoding.UTF8.GetBytes(placeholder), "text/plain", "DKSProfileAgent.txt");
    }

    public async Task<IActionResult> DownloadExePackage(int batchId)
    {
        var batch = await GetBatchAsync(batchId);
        if (batch == null) return NotFound();

        var token = batch.Tokens.FirstOrDefault()?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Warning"] = "Generate an active token first, then download the EXE package.";
            return RedirectToAction(nameof(Index), new { batchId });
        }

        var exePath = ResolveStandaloneAgentExePath();
        if (exePath == null)
        {
            TempData["Error"] = "Standalone agent EXE not found. Copy DKSProfileAgent.exe into wwwroot/agent/ or set AgentPackage:StandaloneExePath.";
            return RedirectToAction(nameof(Index), new { batchId });
        }

        var customerCode = batch.Customer?.CustomerCode ?? $"Batch{batch.BatchId}";
        var downloadName = $"DKSProfileAgent_{SanitizeFileName(customerCode)}.zip";
        var apiUrl = $"{BuildBaseUrl()}/api/agent";

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{downloadName}\"";

        using (var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exeEntry = archive.CreateEntry("DKSProfileAgent.exe", CompressionLevel.NoCompression);
            await using (var entryStream = exeEntry.Open())
            await using (var exeStream = System.IO.File.OpenRead(exePath))
            {
                await exeStream.CopyToAsync(entryStream, HttpContext.RequestAborted);
            }

            var configEntry = archive.CreateEntry("appsettings.json", CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(configEntry.Open(), new UTF8Encoding(false)))
            {
                var configJson = JsonSerializer.Serialize(
                    BuildStandaloneAgentConfig(batch, token, apiUrl),
                    new JsonSerializerOptions { WriteIndented = true });
                await writer.WriteAsync(configJson);
            }

            var readmeEntry = archive.CreateEntry("README.txt", CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(readmeEntry.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(BuildStandaloneReadme(batch));
            }
        }

        await Response.Body.FlushAsync(HttpContext.RequestAborted);
        return new EmptyResult();
    }

    public async Task<IActionResult> DownloadGpoScript(int batchId)
    {
        var batch = await GetBatchAsync(batchId);
        if (batch == null) return NotFound();
        var token = batch.Tokens.FirstOrDefault()?.Token ?? "YOUR_TOKEN_HERE";
        var baseUrl = BuildBaseUrl();
        var mode = GetModeValue(batch.Mode);
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
    ""MODE={mode}"",
    ""/l*v"", $LogPath
) -Wait
";
        return File(System.Text.Encoding.UTF8.GetBytes(script), "text/plain",
            $"GPO_Install_{batch.Customer?.CustomerCode}.ps1");
    }

    public async Task<IActionResult> DownloadIntuneScript(int batchId)
    {
        var batch = await GetBatchAsync(batchId);
        if (batch == null) return NotFound();
        var token = batch.Tokens.FirstOrDefault()?.Token ?? "YOUR_TOKEN_HERE";
        var baseUrl = BuildBaseUrl();
        var mode = GetModeValue(batch.Mode);
        var script = $@"# Intune Win32 App Install Script
# Customer: {batch.Customer?.CustomerName}
# Generated: {DateTime.Now:yyyy-MM-dd HH:mm}

$params = '/i DKSProfileAgent.msi /qn APIURL=""{baseUrl}/api/agent"" CUSTOMERCODE=""{batch.Customer?.CustomerCode}"" AGENTTOKEN=""{token}"" MODE=""{mode}""'
Start-Process msiexec.exe -ArgumentList $params -Wait
";
        return File(System.Text.Encoding.UTF8.GetBytes(script), "text/plain",
            $"Intune_Install_{batch.Customer?.CustomerCode}.ps1");
    }

    private Task<MigrationBatch?> GetBatchAsync(int batchId)
    {
        return _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Tokens.Where(t => t.IsActive))
            .FirstOrDefaultAsync(b => b.BatchId == batchId);
    }

    private string BuildBaseUrl()
    {
        var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
        return $"{Request.Scheme}://{Request.Host}{pathBase}".TrimEnd('/');
    }

    private string? ResolveStandaloneAgentExePath()
    {
        var candidates = new List<string>();
        var configuredPath = _config["AgentPackage:StandaloneExePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(ResolveContentRootPath(configuredPath));
        }

        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        candidates.Add(Path.Combine(webRoot, "agent", "DKSProfileAgent.exe"));
        candidates.Add(Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "Agent", "agent-exe", "DKSProfileAgent.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "Agent", "Installer", "publish", "DKSProfileAgent.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "Agent", "DKS.Migration.Agent", "bin", "Release", "net8.0-windows", "win-x64", "DKSProfileAgent.exe")));

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(System.IO.File.Exists);
    }

    private string ResolveContentRootPath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, path));
    }

    private object BuildStandaloneAgentConfig(MigrationBatch batch, string token, string apiUrl)
    {
        var logPath = _config["AgentPackage:LogPath"] ?? @"C:\ProgramData\DKS\ProfileAgent\Logs";
        var checkInInterval = Math.Max(5, _config.GetValue<int?>("AgentPackage:CheckInIntervalSeconds") ?? 30);

        return new
        {
            Logging = new
            {
                LogLevel = new
                {
                    Default = "Information"
                }
            },
            Agent = new
            {
                ApiUrl = apiUrl,
                CustomerCode = batch.Customer?.CustomerCode ?? string.Empty,
                AgentToken = token,
                Mode = GetModeValue(batch.Mode),
                LogPath = logPath,
                AutoStart = true,
                CheckInIntervalSeconds = checkInInterval,
                TestMode = false
            }
        };
    }

    private string BuildStandaloneReadme(MigrationBatch batch)
    {
        var customerName = batch.Customer?.CustomerName ?? "Unknown customer";
        var customerCode = batch.Customer?.CustomerCode ?? "UNKNOWN";
        var logPath = _config["AgentPackage:LogPath"] ?? @"C:\ProgramData\DKS\ProfileAgent\Logs";

        return $@"DKS Profile Agent - Standalone EXE package

Customer: {customerName}
Customer code: {customerCode}
Batch: {batch.BatchName}
Mode: {GetModeValue(batch.Mode)}
Generated: {DateTime.Now:yyyy-MM-dd HH:mm}

1. Extract this ZIP into a normal folder on the target Windows PC.
2. Keep appsettings.json in the same folder as DKSProfileAgent.exe.
3. Double-click DKSProfileAgent.exe.
4. The agent runs quietly in the background and registers itself with the portal.
5. Check progress in Portal > Devices and Portal > Logs.

Notes
- This package does not require MSI installation.
- Local logs: {logPath}
- This copy-and-run package does not auto-start after reboot unless you add your own Startup shortcut or scheduled task.
";
    }

    private static string GetModeValue(AgentMode mode)
    {
        return mode switch
        {
            AgentMode.ExportOnly => "EXPORT_ONLY",
            AgentMode.ReconfigureOnly => "RECONFIG_ONLY",
            AgentMode.ImportOnly => "IMPORT_ONLY",
            AgentMode.ExportReconfigureImport => "EXPORT_RECONFIG_IMPORT",
            _ => "EXPORT_RECONFIG_IMPORT"
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}
