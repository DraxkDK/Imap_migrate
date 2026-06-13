using System.Security.Claims;
using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using DKS.Migration.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace DKS.Migration.Portal.Controllers;

public class AccountController : Controller
{
    private const string Issuer = "DKS Migration";
    private const string MfaCookie = "DksMfaPending";

    private readonly AppDbContext _db;
    private readonly IDataProtectionProvider _dp;
    public AccountController(AppDbContext db, IDataProtectionProvider dp) { _db = db; _dp = dp; }

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

        // Password OK. If MFA is enabled, defer sign-in until the TOTP code is verified.
        if (user.TotpEnabled)
        {
            SetPending(user.Id);
            return RedirectToAction(nameof(Verify), new { returnUrl });
        }

        await SignInAsync(user);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    // --- Second factor at login -------------------------------------------------

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Verify(string? returnUrl = null)
    {
        if (ReadPending() == null) return RedirectToAction(nameof(Login));
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string code, string? returnUrl = null)
    {
        var userId = ReadPending();
        if (userId == null) return RedirectToAction(nameof(Login));

        var user = await _db.PortalUsers.FindAsync(userId.Value);
        if (user == null || !user.IsActive || !user.TotpEnabled)
            return RedirectToAction(nameof(Login));

        if (!TotpService.Verify(user.TotpSecret, code))
        {
            ModelState.AddModelError("", "Mã xác thực không đúng hoặc đã hết hạn.");
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        Response.Cookies.Delete(MfaCookie);
        await SignInAsync(user);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    // --- MFA enrolment (for the logged-in user) ---------------------------------

    [HttpGet]
    public async Task<IActionResult> Mfa()
    {
        var user = await CurrentUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        if (!user.TotpEnabled)
        {
            // Generate (and persist) a secret to enrol against if there isn't a pending one.
            if (string.IsNullOrEmpty(user.TotpSecret))
            {
                user.TotpSecret = TotpService.GenerateSecret();
                await _db.SaveChangesAsync();
            }
            ViewBag.Secret = user.TotpSecret;
            ViewBag.QrDataUri = QrDataUri(TotpService.GetOtpAuthUri(Issuer, user.Username, user.TotpSecret!));
        }
        ViewBag.Enabled = user.TotpEnabled;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Mfa(string code)
    {
        var user = await CurrentUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        if (!user.TotpEnabled)
        {
            if (!TotpService.Verify(user.TotpSecret, code))
            {
                ModelState.AddModelError("", "Mã không đúng — quét lại QR và thử mã mới.");
                ViewBag.Secret = user.TotpSecret;
                ViewBag.QrDataUri = QrDataUri(TotpService.GetOtpAuthUri(Issuer, user.Username, user.TotpSecret ?? ""));
                ViewBag.Enabled = false;
                return View();
            }
            user.TotpEnabled = true;
            await _db.SaveChangesAsync();
            // Refresh the cookie so the "mfa" claim becomes "enabled" and the RequireMfa
            // middleware lets the user into the rest of the portal in this same session.
            await SignInAsync(user);
            TempData["Success"] = "Đã bật xác thực 2 lớp (MFA). Lần đăng nhập sau sẽ cần mã từ app.";
            return RedirectToAction("Index", "Home");
        }
        return RedirectToAction(nameof(Mfa));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableMfa(string code)
    {
        var user = await CurrentUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        if (user.TotpEnabled)
        {
            if (!TotpService.Verify(user.TotpSecret, code))
            {
                TempData["Error"] = "Mã không đúng — chưa tắt MFA.";
                return RedirectToAction(nameof(Mfa));
            }
            user.TotpEnabled = false;
            user.TotpSecret = null;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Đã tắt MFA.";
        }
        return RedirectToAction(nameof(Mfa));
    }

    // --- Existing account actions ----------------------------------------------

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

    // --- Helpers ----------------------------------------------------------------

    private async Task SignInAsync(PortalUser user)
    {
        user.LastLogin = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            // Lets the RequireMfa middleware allow the user through without a per-request DB hit.
            new("mfa", user.TotpEnabled ? "enabled" : "none"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private Task<PortalUser?> CurrentUserAsync()
        => _db.PortalUsers.FirstOrDefaultAsync(u => u.Username == User.Identity!.Name);

    private IDataProtector Protector() => _dp.CreateProtector("DksPortal.MfaPending.v1");

    /// <summary>Stores the user id awaiting a second factor in a short-lived, signed cookie.</summary>
    private void SetPending(int userId)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var token = Protector().Protect($"{userId}|{exp}");
        Response.Cookies.Append(MfaCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(5),
        });
    }

    private int? ReadPending()
    {
        if (!Request.Cookies.TryGetValue(MfaCookie, out var tok) || string.IsNullOrEmpty(tok)) return null;
        try
        {
            var parts = Protector().Unprotect(tok).Split('|');
            if (parts.Length == 2 && int.TryParse(parts[0], out var id)
                && long.TryParse(parts[1], out var exp)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= exp)
                return id;
        }
        catch { /* tampered / expired key — treat as no pending session */ }
        return null;
    }

    private static string QrDataUri(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
