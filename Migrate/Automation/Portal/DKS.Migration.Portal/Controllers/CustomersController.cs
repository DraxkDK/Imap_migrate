using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class CustomersController : Controller
{
    private readonly AppDbContext _db;

    public CustomersController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var customers = await _db.Customers
            .Include(c => c.Batches)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return View(customers);
    }

    public IActionResult Create() => View(new Customer());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Customer customer)
    {
        if (!ModelState.IsValid) return View(customer);
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Customer '{customer.CustomerName}' created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await _db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Customer customer)
    {
        if (id != customer.CustomerId) return BadRequest();
        if (!ModelState.IsValid) return View(customer);
        _db.Customers.Update(customer);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Customer updated.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        _db.Customers.Remove(c);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Customer deleted.";
        return RedirectToAction(nameof(Index));
    }
}
