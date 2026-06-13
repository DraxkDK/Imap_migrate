using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
using DKS.Migration.Portal.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Behind the shared Nginx reverse proxy (which terminates TLS and forwards over
// plain HTTP on loopback), trust X-Forwarded-* so request scheme/host/IP reflect
// the real client. Without this, UseHttpsRedirection() loops because Kestrel only
// ever sees http. Loopback proxies are trusted by default.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=dks_migration.db"));

// Microsoft 365 / Graph: encrypt client secrets at rest + broker app-only tokens.
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<GraphTokenService>();

// Cookie auth — every page requires login except those marked [AllowAnonymous]
// (the login page and the agent API, which authenticates by token).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/Denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.Name = "DksPortalAuth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    // Require an authenticated user on every endpoint by default; opt out with
    // [AllowAnonymous] on the login page and the agent API.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated does not alter an existing schema, so make sure the portal
    // users table exists even on databases created before auth was added.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""PortalUsers"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PortalUsers"" PRIMARY KEY AUTOINCREMENT,
            ""Username"" TEXT NOT NULL,
            ""PasswordHash"" TEXT NOT NULL,
            ""Role"" TEXT NOT NULL,
            ""IsActive"" INTEGER NOT NULL,
            ""CreatedAt"" TEXT NOT NULL,
            ""LastLogin"" TEXT NULL
        );");
    db.Database.ExecuteSqlRaw(
        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PortalUsers_Username"" ON ""PortalUsers"" (""Username"");");

    // Add the MFA (TOTP) columns to PortalUsers on pre-existing DBs.
    foreach (var col in new[] { ("TotpSecret", "TEXT"), ("TotpEnabled", "INTEGER NOT NULL DEFAULT 0") })
    {
        try { db.Database.ExecuteSqlRaw($"ALTER TABLE \"PortalUsers\" ADD COLUMN \"{col.Item1}\" {col.Item2}"); }
        catch { /* column already exists */ }
    }

    // Add the Graph app-registration columns to Customers on pre-existing DBs.
    foreach (var col in new[] { "EntraTenantId", "GraphClientId", "ClientSecretEncrypted", "CertThumbprint", "CertLocation" })
    {
        try { db.Database.ExecuteSqlRaw($"ALTER TABLE \"Customers\" ADD COLUMN \"{col}\" TEXT"); }
        catch { /* column already exists */ }
    }

    // Seed a first admin if none exist. Override via env PORTAL_ADMIN_USER /
    // PORTAL_ADMIN_PASSWORD. Change the password right after first login.
    if (!db.PortalUsers.Any())
    {
        var adminUser = Environment.GetEnvironmentVariable("PORTAL_ADMIN_USER") ?? "admin";
        var adminPass = Environment.GetEnvironmentVariable("PORTAL_ADMIN_PASSWORD") ?? "Admin@123456";
        db.PortalUsers.Add(new PortalUser
        {
            Username = adminUser,
            PasswordHash = PasswordHasher.Hash(adminPass),
            Role = PortalRoles.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Console.WriteLine($"[SEED] Portal admin '{adminUser}' created — CHANGE THIS PASSWORD after first login.");
    }

    // Run test-data seed when --seed flag passed: dotnet run -- --seed
    if (args.Contains("--seed"))
    {
        await SeedTestData(db);
        return;
    }
}

static async Task SeedTestData(AppDbContext db)
{
    // Customer
    var customer = await db.Customers.FirstOrDefaultAsync(c => c.CustomerCode == "TEST");
    if (customer == null)
    {
        customer = new Customer { CustomerName = "Test Company", CustomerCode = "TEST", DestinationDomain = "test.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
    }

    // Batch
    var batch = await db.MigrationBatches.FirstOrDefaultAsync(b => b.CustomerId == customer.CustomerId);
    if (batch == null)
    {
        batch = new MigrationBatch
        {
            CustomerId = customer.CustomerId,
            BatchName = "TEST Migration 2026-06",
            SourceType = SourceMailType.POP3,
            DestinationType = DestinationType.Microsoft365,
            Mode = AgentMode.ExportReconfigureImport,
            BackupPstPath = @"C:\ProgramData\DKS\PST-Migration\TEST",
            CutoverTime = DateTime.Now.AddDays(3),
            Status = BatchStatus.Active,
            CloseOutlookAutomatically = true,
            RollbackEnabled = true,
            ImportTargetFolder = "/Imported POP3"
        };
        db.MigrationBatches.Add(batch);
        await db.SaveChangesAsync();
    }

    // Revoke old tokens
    var old = db.AgentTokens.Where(t => t.BatchId == batch.BatchId && t.IsActive);
    foreach (var t in old) { t.IsActive = false; t.RevokedAt = DateTime.UtcNow; }

    // New token
    var token = new AgentToken
    {
        BatchId = batch.BatchId,
        Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                    .Replace("=", "").Replace("/", "_").Replace("+", "-")
    };
    db.AgentTokens.Add(token);
    await db.SaveChangesAsync();

    Console.WriteLine();
    Console.WriteLine("=== TEST SEED COMPLETED ===");
    Console.WriteLine($"Customer : TEST Company (Code: TEST)");
    Console.WriteLine($"Batch    : {batch.BatchName} (ID: {batch.BatchId})");
    Console.WriteLine($"Token    : {token.Token}");
    Console.WriteLine();
    Console.WriteLine("--- MSI Install Command ---");
    Console.WriteLine($"msiexec /i DKSProfileAgent.msi /qn APIURL=\"http://localhost:5000/api/agent\" CUSTOMERCODE=\"TEST\" AGENTTOKEN=\"{token.Token}\" MODE=\"EXPORT_RECONFIG_IMPORT\"");
    Console.WriteLine();
    Console.WriteLine("--- Agent appsettings override ---");
    Console.WriteLine($"  ApiUrl    : http://localhost:5000/api/agent");
    Console.WriteLine($"  AgentToken: {token.Token}");
    Console.WriteLine($"  Mode      : EXPORT_RECONFIG_IMPORT");
    Console.WriteLine("===========================");
}

// Must run before anything that inspects scheme/host/IP (HTTPS redirect, auth).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Serve the agent MSI (.msi is not in the default static content-type map, so
// without this the /agent/DKSProfileAgent.msi download returns a 404/error page).
var msiContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
msiContentTypes.Mappings[".msi"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = msiContentTypes });

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Viewer role is read-only: block state-changing verbs for viewers, except the
// agent API and their own account actions (logout / change password).
app.Use(async (ctx, next) =>
{
    var u = ctx.User;
    if (u?.Identity?.IsAuthenticated == true && u.IsInRole(PortalRoles.Viewer)
        && !HttpMethods.IsGet(ctx.Request.Method)
        && !HttpMethods.IsHead(ctx.Request.Method)
        && !ctx.Request.Path.StartsWithSegments("/Account")
        && !ctx.Request.Path.StartsWithSegments("/api/agent"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("Viewer role is read-only.");
        return;
    }
    await next();
});

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
