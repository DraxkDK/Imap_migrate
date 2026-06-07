using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class BatchesController : Controller
{
    private readonly AppDbContext _db;

    public BatchesController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var batches = await _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Devices)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
        return View(batches);
    }

    public async Task<IActionResult> Create()
    {
        await PopulateCustomers();
        return View(new MigrationBatch { CutoverTime = DateTime.Now.AddDays(3) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MigrationBatch batch)
    {
        if (!ModelState.IsValid) { await PopulateCustomers(); return View(batch); }
        batch.Status = BatchStatus.Active;
        _db.MigrationBatches.Add(batch);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Batch '{batch.BatchName}' created.";
        return RedirectToAction(nameof(Details), new { id = batch.BatchId });
    }

    public async Task<IActionResult> Details(int id)
    {
        var batch = await _db.MigrationBatches
            .Include(b => b.Customer)
            .Include(b => b.Devices)
            .Include(b => b.Users)
            .Include(b => b.Tokens)
            .FirstOrDefaultAsync(b => b.BatchId == id);
        if (batch == null) return NotFound();
        return View(batch);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var batch = await _db.MigrationBatches.FindAsync(id);
        if (batch == null) return NotFound();
        await PopulateCustomers(batch.CustomerId);
        return View(batch);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, MigrationBatch batch)
    {
        if (id != batch.BatchId) return BadRequest();
        if (!ModelState.IsValid) { await PopulateCustomers(batch.CustomerId); return View(batch); }
        _db.MigrationBatches.Update(batch);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Batch updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task PopulateCustomers(int? selectedId = null)
    {
        var customers = await _db.Customers.OrderBy(c => c.CustomerName).ToListAsync();
        ViewBag.Customers = new SelectList(customers, "CustomerId", "CustomerName", selectedId);
    }
}
