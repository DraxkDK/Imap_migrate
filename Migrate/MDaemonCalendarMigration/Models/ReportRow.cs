namespace MDaemonCalendarMigration.Models;

public class ReportRow
{
    public string SourceEmail             { get; set; } = "";
    public string TargetMailbox           { get; set; } = "";
    public string CalendarPath            { get; set; } = "";
    public string OutputIcs               { get; set; } = "";
    public int    EventCount              { get; set; }
    public int    FilesScanned            { get; set; }
    public int    FilesWithCalendarBlocks { get; set; }
    public string Status                  { get; set; } = "";
    public string Message                 { get; set; } = "";
}
