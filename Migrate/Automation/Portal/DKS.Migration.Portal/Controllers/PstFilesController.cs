using DKS.Migration.Portal.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class PstFilesController : Controller
{
    private readonly AppDbContext _db;

    public PstFilesController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? deviceId, int? batchId)
    {
        var q = _db.PstFiles.Include(p => p.Device).ThenInclude(d => d!.Batch).AsQueryable();
        if (deviceId.HasValue) q = q.Where(p => p.DeviceId == deviceId.Value);
        if (batchId.HasValue) q = q.Where(p => p.Device!.BatchId == batchId.Value);
        ViewBag.DeviceId = deviceId;
        ViewBag.BatchId = batchId;
        return View(await q.OrderBy(p => p.Device!.ComputerName).ThenBy(p => p.FileName).ToListAsync());
    }
}
