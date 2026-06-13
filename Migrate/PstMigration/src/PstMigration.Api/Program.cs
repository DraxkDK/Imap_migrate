using PstMigration.Domain.Entities;
using PstMigration.Infrastructure;
using PstMigration.Infrastructure.Persistence;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

    builder.Services.AddControllers();
    builder.Services.AddPstMigrationInfrastructure(builder.Configuration);

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MigrationDbContext>();
        db.Database.EnsureCreated();
        SeedDefaults(db, app.Configuration);
    }

    app.UseSerilogRequestLogging();
    app.MapControllers();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PstMigration.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Seed a default tenant + app-registration placeholder so the system is runnable
// out of the box. Real tenants/app registrations are configured via the portal.
static void SeedDefaults(MigrationDbContext db, IConfiguration config)
{
    if (db.Tenants.Any()) return;

    var tenant = new Tenant
    {
        Name = config["DefaultTenant:Name"] ?? "Default Tenant",
        TenantDomain = config["DefaultTenant:Domain"] ?? "contoso.onmicrosoft.com",
        EntraTenantId = config["DefaultTenant:EntraTenantId"] ?? "00000000-0000-0000-0000-000000000000",
    };
    db.Tenants.Add(tenant);
    db.AppRegistrations.Add(new AppRegistration
    {
        TenantId = tenant.Id,
        ClientId = config["DefaultTenant:ClientId"] ?? "00000000-0000-0000-0000-000000000000",
        CertificateThumbprint = config["DefaultTenant:CertThumbprint"] ?? "",
        CertificateLocation = config["DefaultTenant:CertLocation"] ?? "store:CurrentUser/My",
    });
    db.SaveChanges();
    Log.Information("Seeded default tenant {Domain}", tenant.TenantDomain);
}

public partial class Program { }
