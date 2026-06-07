namespace MDaemonCalendarMigration.Models;

public class ImportItem
{
    public bool   Selected      { get; set; }
    public string IcsFile       { get; set; } = "";  // full path
    public string SourceEmail   { get; set; } = "";  // derived from filename
    public string TargetMailbox { get; set; } = "";
    public string MappingStatus { get; set; } = "";  // Mapped / Unmapped
    public int    TotalEvents   { get; set; }
    public int    Imported      { get; set; }
    public int    Failed        { get; set; }
    public string Status        { get; set; } = "Ready";
    public string Message       { get; set; } = "";
}
