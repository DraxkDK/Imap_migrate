using PstMigration.Agent;
using PstMigration.Agent.Services;
using PstMigration.Application.Abstractions;
using PstMigration.Graph;
using PstMigration.PstParser;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configuration written by the MSI to HKLM\SOFTWARE\DKS\PstMigrationAgent
// (overrides appsettings.json) — so the service picks up Portal URL + token.
builder.Configuration.AddInMemoryCollection(ReadRegistryConfig());

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

// Graph: agent gets short-lived tokens from the portal (cert stays on portal).
builder.Services.AddSingleton<GraphTokenProvider>();
builder.Services.AddSingleton<IGraphTokenProvider>(sp => sp.GetRequiredService<GraphTokenProvider>());
builder.Services.AddGraphMailboxService();
builder.Services.AddSingleton<MigrationRunner>();

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();

static Dictionary<string, string?> ReadRegistryConfig()
{
    var map = new Dictionary<string, string?>();
    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DKS\PstMigrationAgent");
        if (key is null) return map;
        if (key.GetValue("PortalUrl") is string url && url.Length > 0) map["Portal:Url"] = url;
        if (key.GetValue("RegistrationToken") is string tok && tok.Length > 0) map["Portal:RegistrationToken"] = tok;
        if (key.GetValue("LogPath") is string log && log.Length > 0) map["Agent:LogPath"] = log;
    }
    catch { /* no registry config (dev/console) */ }
    return map;
}
