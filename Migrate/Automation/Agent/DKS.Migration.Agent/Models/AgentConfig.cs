namespace DKS.Migration.Agent.Models;

public class AgentConfig
{
    public string ApiUrl { get; set; } = "http://localhost:5000/api/agent";
    public string CustomerCode { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string Mode { get; set; } = "EXPORT_RECONFIG_IMPORT";
    public string LogPath { get; set; } = @"C:\ProgramData\DKS\ProfileAgent\Logs";
    public bool AutoStart { get; set; } = true;
    public int CheckInIntervalSeconds { get; set; } = 60;
    public bool UsePrfForM365Profile { get; set; } = false;

    // Test mode: bỏ qua kiểm tra POP3, dùng email giả để test toàn bộ flow
    public bool TestMode { get; set; } = false;
    public string TestEmail { get; set; } = "test@example.com";

    public bool ShouldExport => Mode.Contains("EXPORT");
    public bool ShouldReconfig => Mode.Contains("RECONFIG");
    public bool ShouldImport => Mode.Contains("IMPORT");
}
