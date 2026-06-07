using System.Security.Claims;
using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _db;
    public AccountController(AppDbContext db) => _db = db;

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
    {
        username = (username ?? "").Trim();
        var user = await _db.PortalUsers.FirstOrDefaultAsync(u => u.Username == username);

        // Same generic error whether the user exists or not (no enumeration).
        if (user == null || !user.IsActive || !PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        user.LastLogin = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult Password() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(string currentPassword, string newPassword, string confirmPassword)
    {
        var user = await _db.PortalUsers.FirstOrDefaultAsync(u => u.Username == User.Identity!.Name);
        if (user == null) return RedirectToAction(nameof(Login));

        if (!PasswordHasher.Verify(currentPassword ?? "", user.PasswordHash))
            ModelState.AddModelError("", "Current password is incorrect.");
        else if (newPassword != confirmPassword)
            ModelState.AddModelError("", "New passwords do not match.");
        else
        {
            var err = PasswordHasher.ValidateStrength(newPassword);
            if (err != null) ModelState.AddModelError("", err);
        }

        if (!ModelState.IsValid) return View();

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Password updated.";
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    public IActionResult Denied() => View();
}
