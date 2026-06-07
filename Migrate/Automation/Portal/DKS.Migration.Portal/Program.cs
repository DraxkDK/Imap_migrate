using DKS.Migration.Portal.Data;
using DKS.Migration.Portal.Models;
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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Run seed when --seed flag passed: dotnet run -- --seed
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
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
