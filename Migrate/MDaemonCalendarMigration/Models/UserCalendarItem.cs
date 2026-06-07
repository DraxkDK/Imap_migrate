namespace MDaemonCalendarMigration.Models;

public class UserCalendarItem
{
    public bool Selected { get; set; }
    public string SourceEmail    { get; set; } = "";
    public string TargetMailbox  { get; set; } = "";
    public string CalendarPath   { get; set; } = "";
    public string MappingStatus  { get; set; } = "";  // Mapped / Unmapped
    public int    FileCount       { get; set; }
    public double EstimatedSizeMB { get; set; }
    public string Status          { get; set; } = "Ready";  // Ready Exported NoData Skipped Failed
    public int    EventCount      { get; set; }
    public string OutputIcs       { get; set; } = "";
    public string Message         { get; set; } = "";
}
