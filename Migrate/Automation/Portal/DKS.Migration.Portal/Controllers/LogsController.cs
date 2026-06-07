using DKS.Migration.Portal.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class LogsController : Controller
{
    private readonly AppDbContext _db;

    public LogsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? deviceId, int? batchId, string? level, int page = 1)
    {
        const int pageSize = 100;
        var q = _db.Logs.Include(l => l.Device).ThenInclude(d => d!.Batch).AsQueryable();
        if (deviceId.HasValue) q = q.Where(l => l.DeviceId == deviceId.Value);
        if (batchId.HasValue) q = q.Where(l => l.Device!.BatchId == batchId.Value);
        if (!string.IsNullOrEmpty(level) && Enum.TryParse<Models.LogLevel>(level, out var lv))
            q = q.Where(l => l.Level == lv);

        var total = await q.CountAsync();
        var logs = await q.OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.Total = total;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.DeviceId = deviceId;
        ViewBag.BatchId = batchId;
        ViewBag.Level = level;
        return View(logs);
    }
}
