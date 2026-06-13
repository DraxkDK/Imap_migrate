using Microsoft.EntityFrameworkCore;
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
        // Defensive: add columns introduced after a DB was first created.
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Tenants\" ADD COLUMN \"RegistrationTokenHash\" TEXT"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"AppRegistrations\" ADD COLUMN \"ClientSecretEncrypted\" TEXT"); }
        catch { /* column already exists */ }
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

public partial class Program { }
