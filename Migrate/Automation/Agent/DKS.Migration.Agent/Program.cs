using DKS.Migration.Agent.Models;
using DKS.Migration.Agent.Services;

// Content root = the exe's own folder, so appsettings.json next to the exe is
// always read — even when the user double-clicks or runs it from another path
// ("copy & run" deployment).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Load config from appsettings + registry + environment.
// Precedence (low → high): appsettings.json → HKLM registry (written by MSI) → env vars.
var config = new AgentConfig();
builder.Configuration.GetSection("Agent").Bind(config);

// Registry config written by the MSI at HKLM\SOFTWARE\DKS\ProfileAgent.
try
{
    using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DKS\ProfileAgent");
    if (reg != null)
    {
        if (reg.GetValue("ApiUrl") is string a && a.Length > 0) config.ApiUrl = a;
        if (reg.GetValue("CustomerCode") is string c && c.Length > 0) config.CustomerCode = c;
        if (reg.GetValue("AgentToken") is string t && t.Length > 0) config.AgentToken = t;
        if (reg.GetValue("Mode") is string m && m.Length > 0) config.Mode = m;
        if (reg.GetValue("LogPath") is string l && l.Length > 0) config.LogPath = l;
    }
}
catch { /* no registry config (e.g. running standalone) — ignore */ }

// Allow MSI command-line properties to override via environment variables
config.ApiUrl = Environment.GetEnvironmentVariable("APIURL") ?? config.ApiUrl;
config.CustomerCode = Environment.GetEnvironmentVariable("CUSTOMERCODE") ?? config.CustomerCode;
config.AgentToken = Environment.GetEnvironmentVariable("AGENTTOKEN") ?? config.AgentToken;
config.Mode = Environment.GetEnvironmentVariable("MODE") ?? config.Mode;
config.LogPath = Environment.GetEnvironmentVariable("LOGPATH") ?? config.LogPath;

// Register services
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<PortalClient>();
builder.Services.AddSingleton<OutlookDetector>();
builder.Services.AddSingleton<PstExporter>();
builder.Services.AddSingleton<ProfileReconfigurer>();
builder.Services.AddSingleton<PstImporter>();
builder.Services.AddHostedService<AgentWorker>();

// Log to file + console
builder.Logging.AddConsole();
Directory.CreateDirectory(config.LogPath);
builder.Logging.AddProvider(new FileLoggerProvider(config.LogPath));

var host = builder.Build();
host.Run();

// Simple file logger implementation
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    public FileLoggerProvider(string logPath) => _logPath = logPath;
    public ILogger CreateLogger(string categoryName) => new FileLogger(_logPath, categoryName);
    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly string _logFile;
    private readonly string _category;
    private static readonly object _lock = new();

    public FileLogger(string logPath, string category)
    {
        _logFile = Path.Combine(logPath, $"agent_{DateTime.Now:yyyyMMdd}.log");
        _category = category.Split('.').Last();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel,-11}] [{_category}] {formatter(state, exception)}";
        if (exception != null) line += $"\n  Exception: {exception.Message}";
        lock (_lock)
        {
            try { File.AppendAllText(_logFile, line + "\n"); } catch { }
        }
        Console.WriteLine(line);
    }
}
