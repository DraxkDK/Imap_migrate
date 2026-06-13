using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class DevicesController : Controller
{
    private readonly AppDbContext _db;

    public DevicesController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? batchId, string? status)
    {
        var q = _db.Devices.Include(d => d.Batch).ThenInclude(b => b!.Customer).AsQueryable();
        if (batchId.HasValue) q = q.Where(d => d.BatchId == batchId.Value);
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DeviceStatus>(status, out var s))
            q = q.Where(d => d.CurrentStatus == s);
        ViewBag.BatchId = batchId;
        ViewBag.Status = status;
        return View(await q.OrderByDescending(d => d.LastCheckIn).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var device = await _db.Devices
            .Include(d => d.Batch).ThenInclude(b => b!.Customer)
            .Include(d => d.PstFiles)
            .Include(d => d.Logs.OrderByDescending(l => l.CreatedAt).Take(50))
            .FirstOrDefaultAsync(d => d.DeviceId == id);
        if (device == null) return NotFound();
        return View(device);
    }

    [HttpPost]
    public async Task<IActionResult> SendCommand(int id, string command)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();

        device.PendingCommand = command;
        _db.Logs.Add(new DeviceLog
        {
            DeviceId = id,
            Step = "ManualCommand",
            Level = Models.LogLevel.Info,
            Message = $"Command '{command}' queued — waiting for agent check-in."
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Command '{command}' queued. Agent will execute on next check-in (~30s).";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> MarkManualCompleted(int id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();
        device.CurrentStatus = DeviceStatus.Completed;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Device marked as manually completed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Queue the Graph-based "Import to M365" command for one or many devices.</summary>
    [HttpPost]
    public async Task<IActionResult> ImportToM365(int[] ids)
    {
        if (ids is null || ids.Length == 0)
        {
            TempData["Warning"] = "No devices selected.";
            return RedirectToAction(nameof(Index));
        }
        var devices = await _db.Devices.Where(d => ids.Contains(d.DeviceId)).ToListAsync();
        foreach (var d in devices)
        {
            d.PendingCommand = "ImportToM365";
            _db.Logs.Add(new DeviceLog
            {
                DeviceId = d.DeviceId,
                Step = "ManualCommand",
                Level = Models.LogLevel.Info,
                Message = "Import to M365 (Graph) queued — waiting for agent check-in.",
            });
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Import to M365 queued for {devices.Count} device(s). Agents run it on next check-in (~30s).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Rollback(int id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();
        device.CurrentStatus = DeviceStatus.NeedManualAction;
        device.ErrorMessage = "Rollback requested from portal.";
        _db.Logs.Add(new DeviceLog
        {
            DeviceId = id,
            Step = "Rollback",
            Level = Models.LogLevel.Warning,
            Message = "Rollback triggered manually from portal."
        });
        await _db.SaveChangesAsync();
        TempData["Warning"] = "Rollback flagged. Agent will execute on next check-in.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> DownloadLog(int id)
    {
        var logs = await _db.Logs
            .Where(l => l.DeviceId == id)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
        var device = await _db.Devices.FindAsync(id);
        var lines = logs.Select(l => $"[{l.CreatedAt:yyyy-MM-dd HH:mm:ss}] [{l.Level}] [{l.Step}] {l.Message}");
        var content = string.Join("\n", lines);
        var filename = $"log_{device?.ComputerName ?? id.ToString()}_{DateTime.Now:yyyyMMdd}.txt";
        return File(System.Text.Encoding.UTF8.GetBytes(content), "text/plain", filename);
    }
}
