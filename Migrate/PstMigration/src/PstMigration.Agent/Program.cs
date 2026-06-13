using PstMigration.Agent;
using PstMigration.Agent.Services;
using PstMigration.PstParser;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service when installed via MSI; runs as console in dev.
builder.Services.AddWindowsService(o => o.ServiceName = "PstMigrationAgent");

var logDir = builder.Configuration["Agent:LogPath"] ?? @"C:\ProgramData\DKS\PstMigrationAgent\Logs";
Directory.CreateDirectory(logDir);
builder.Services.AddSerilog(cfg => cfg
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "agent-.log"), rollingInterval: RollingInterval.Day));

var portalUrl = builder.Configuration["Portal:Url"] ?? "https://localhost:5001";
builder.Services.AddHttpClient<PortalClient>(c => c.BaseAddress = new Uri(portalUrl));

builder.Services.AddXstPstParser();
builder.Services.AddSingleton<PstScanner>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
