using CsvHelper;
using CsvHelper.Configuration;
using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace DKS.Migration.Portal.Controllers;

public class UsersController : Controller
{
    private readonly AppDbContext _db;

    public UsersController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? batchId)
    {
        var q = _db.Users.Include(u => u.Batch).ThenInclude(b => b!.Customer).AsQueryable();
        if (batchId.HasValue) q = q.Where(u => u.BatchId == batchId.Value);
        ViewBag.BatchId = batchId;
        return View(await q.OrderBy(u => u.WindowsUsername).ToListAsync());
    }

    public async Task<IActionResult> ImportCsv(int batchId)
    {
        var batch = await _db.MigrationBatches.Include(b => b.Customer).FirstOrDefaultAsync(b => b.BatchId == batchId);
        if (batch == null) return NotFound();
        ViewBag.Batch = batch;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsv(int batchId, IFormFile file)
    {
        var batch = await _db.MigrationBatches.Include(b => b.Customer).FirstOrDefaultAsync(b => b.BatchId == batchId);
        if (batch == null) return NotFound();

        if (file == null || file.Length == 0)
        {
            ModelState.AddModelError("", "Please select a CSV file.");
            ViewBag.Batch = batch;
            return View();
        }

        var imported = 0;
        using var reader = new StreamReader(file.OpenReadStream());
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HeaderValidated = null, MissingFieldFound = null });

        var records = csv.GetRecords<CsvUserRecord>().ToList();
        foreach (var r in records)
        {
            var windowsUsername = NormalizeField(r.WindowsUsername);
            if (string.IsNullOrWhiteSpace(windowsUsername)) continue;

            var existing = await _db.Users.FirstOrDefaultAsync(u => u.BatchId == batchId && u.WindowsUsername == windowsUsername);
            if (existing != null)
            {
                existing.FullName = NormalizeField(r.FullName);
                existing.ComputerName = NormalizeField(r.ComputerName);
                existing.OldEmail = NormalizeField(r.OldEmail);
                existing.NewEmail = NormalizeField(r.NewEmail);
                existing.TargetMailbox = NormalizeField(r.TargetMailbox);
            }
            else
            {
                _db.Users.Add(new MigrationUser
                {
                    BatchId = batchId,
                    FullName = NormalizeField(r.FullName),
                    WindowsUsername = windowsUsername,
                    ComputerName = NormalizeField(r.ComputerName),
                    OldEmail = NormalizeField(r.OldEmail),
                    NewEmail = NormalizeField(r.NewEmail),
                    TargetMailbox = NormalizeField(r.TargetMailbox),
                    Status = UserMigrationStatus.Pending
                });
            }
            imported++;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Imported/updated {imported} users.";
        return RedirectToAction(nameof(Index), new { batchId });
    }

    public IActionResult DownloadTemplate()
    {
        var csv = "FullName,WindowsUsername,ComputerName,OldEmail,NewEmail,TargetMailbox,Status\n" +
                  "Nguyen Van A,nvana,PC-KETOAN01,nvana@oldmail.com,nvana@abc.com,nvana@abc.com,Pending\n" +
                  "Tran Thi B,ttb,PC-SALE02,ttb@oldmail.com,ttb@abc.com,ttb@abc.com,Pending";
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "user_mapping_template.csv");
    }

    private static string? NormalizeField(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public class CsvUserRecord
{
    public string? FullName { get; set; }
    public string WindowsUsername { get; set; } = "";
    public string? ComputerName { get; set; }
    public string? OldEmail { get; set; }
    public string? NewEmail { get; set; }
    public string? TargetMailbox { get; set; }
    public string? Status { get; set; }
}
