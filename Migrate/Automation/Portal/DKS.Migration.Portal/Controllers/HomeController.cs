using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var vm = new DashboardViewModel
        {
            TotalCustomers = await _db.Customers.CountAsync(),
            TotalBatches = await _db.MigrationBatches.CountAsync(),
            TotalDevices = await _db.Devices.CountAsync(),
            TotalUsers = await _db.Users.CountAsync(),
            CompletedDevices = await _db.Devices.CountAsync(d => d.CurrentStatus == DeviceStatus.Completed),
            FailedDevices = await _db.Devices.CountAsync(d => d.CurrentStatus == DeviceStatus.Failed),
            ActiveBatches = await _db.MigrationBatches
                .Include(b => b.Customer)
                .Where(b => b.Status == BatchStatus.Active)
                .OrderByDescending(b => b.CreatedAt)
                .Take(5)
                .ToListAsync(),
            RecentLogs = await _db.Logs
                .Include(l => l.Device)
                .OrderByDescending(l => l.CreatedAt)
                .Take(10)
                .ToListAsync()
        };
        return View(vm);
    }

    public IActionResult Error() => View();
}

public class DashboardViewModel
{
    public int TotalCustomers { get; set; }
    public int TotalBatches { get; set; }
    public int TotalDevices { get; set; }
    public int TotalUsers { get; set; }
    public int CompletedDevices { get; set; }
    public int FailedDevices { get; set; }
    public List<MigrationBatch> ActiveBatches { get; set; } = new();
    public List<DeviceLog> RecentLogs { get; set; } = new();
}
