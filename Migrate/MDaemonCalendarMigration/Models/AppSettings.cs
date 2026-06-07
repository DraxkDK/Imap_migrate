namespace MDaemonCalendarMigration.Models;

public class AppSettings
{
    // Tab 1 — Extract
    public string LastSourceFolder { get; set; } = "";
    public string LastMappingCsv   { get; set; } = "";
    public string LastOutputFolder { get; set; } = "";
    public string LastReportFolder { get; set; } = "";

    // Tab 2 — Import to M365
    public string TenantId              { get; set; } = "";
    public string ClientId              { get; set; } = "";
    public string ClientSecretEncrypted { get; set; } = "";   // DPAPI base64 or empty
    public string TimeZone              { get; set; } = "SE Asia Standard Time";
    public string LastIcsFolder         { get; set; } = "";
    public string LastImportMappingCsv  { get; set; } = "";
}
