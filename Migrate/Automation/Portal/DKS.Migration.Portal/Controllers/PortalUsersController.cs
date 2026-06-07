using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using DKS.Migration.Portal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

[Authorize(Roles = PortalRoles.Admin)]
public class PortalUsersController : Controller
{
    private readonly AppDbContext _db;
    public PortalUsersController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewBag.Roles = PortalRoles.All;
        return View(await _db.PortalUsers.OrderBy(u => u.Username).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string username, string password, string role)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrEmpty(username))
            TempData["Error"] = "Username is required.";
        else if (!PortalRoles.IsValid(role))
            TempData["Error"] = "Invalid role.";
        else if (await _db.PortalUsers.AnyAsync(u => u.Username == username))
            TempData["Error"] = "That username already exists.";
        else if (PasswordHasher.ValidateStrength(password) is { } err)
            TempData["Error"] = err;
        else
        {
            _db.PortalUsers.Add(new PortalUser
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                Role = role,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            TempData["Success"] = $"User '{username}' created.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, string role)
    {
        var user = await _db.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        if (!PortalRoles.IsValid(role))
            TempData["Error"] = "Invalid role.";
        else if (user.Username == User.Identity!.Name && role != PortalRoles.Admin)
            TempData["Error"] = "You cannot remove your own Admin role.";
        else
        {
            user.Role = role;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Role updated.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var user = await _db.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        if (user.Username == User.Identity!.Name)
            TempData["Error"] = "You cannot disable your own account.";
        else
        {
            user.IsActive = !user.IsActive;
            await _db.SaveChangesAsync();
            TempData["Success"] = "User updated.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, string newPassword)
    {
        var user = await _db.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        if (PasswordHasher.ValidateStrength(newPassword) is { } err)
            TempData["Error"] = err;
        else
        {
            user.PasswordHash = PasswordHasher.Hash(newPassword);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Password reset for '{user.Username}'.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _db.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        if (user.Username == User.Identity!.Name)
            TempData["Error"] = "You cannot delete your own account.";
        else
        {
            _db.PortalUsers.Remove(user);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"User '{user.Username}' deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
