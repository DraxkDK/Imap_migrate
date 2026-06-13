using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using DKS.Migration.Portal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class CustomersController : Controller
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _protector;

    public CustomersController(AppDbContext db, SecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

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
    public async Task<IActionResult> Create(Customer customer, string? clientSecret)
    {
        if (!ModelState.IsValid) return View(customer);
        if (!string.IsNullOrWhiteSpace(clientSecret))
            customer.ClientSecretEncrypted = _protector.Protect(clientSecret.Trim());
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
    public async Task<IActionResult> Edit(int id, Customer customer, string? clientSecret)
    {
        if (id != customer.CustomerId) return BadRequest();
        if (!ModelState.IsValid) return View(customer);

        var existing = await _db.Customers.FindAsync(id);
        if (existing == null) return NotFound();

        existing.CustomerName = customer.CustomerName;
        existing.CustomerCode = customer.CustomerCode;
        existing.DestinationDomain = customer.DestinationDomain;
        existing.EntraTenantId = customer.EntraTenantId;
        existing.GraphClientId = customer.GraphClientId;
        existing.CertThumbprint = customer.CertThumbprint;
        existing.CertLocation = customer.CertLocation;
        // Replace the secret only when a new one is typed (blank keeps the current one).
        if (!string.IsNullOrWhiteSpace(clientSecret))
            existing.ClientSecretEncrypted = _protector.Protect(clientSecret.Trim());

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
