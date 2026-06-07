using System.Text;
using MDaemonCalendarMigration.Models;

namespace MDaemonCalendarMigration.Services;

public class ReportWriter
{
    public string WriteExtractReport(string folder, List<ReportRow> rows)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "extract-report.csv");
        var sb = new StringBuilder();
        sb.AppendLine("SourceEmail,TargetMailbox,CalendarPath,OutputIcs,EventCount,FilesScanned,FilesWithCalendarBlocks,Status,Message");
        foreach (var r in rows)
            sb.AppendLine(Csv(r.SourceEmail, r.TargetMailbox, r.CalendarPath, r.OutputIcs,
                r.EventCount.ToString(), r.FilesScanned.ToString(),
                r.FilesWithCalendarBlocks.ToString(), r.Status, r.Message));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public string WriteImportReport(string folder, List<ImportReportRow> rows)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "import-report.csv");
        var sb = new StringBuilder();
        sb.AppendLine("SourceEmail,TargetMailbox,IcsFile,TotalEvents,Imported,Failed,Status,Message");
        foreach (var r in rows)
            sb.AppendLine(Csv(r.SourceEmail, r.TargetMailbox, r.IcsFile,
                r.TotalEvents.ToString(), r.Imported.ToString(),
                r.Failed.ToString(), r.Status, r.Message));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(Esc));

    private static string Esc(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}

public class ImportReportRow
{
    public string SourceEmail   { get; set; } = "";
    public string TargetMailbox { get; set; } = "";
    public string IcsFile       { get; set; } = "";
    public int    TotalEvents   { get; set; }
    public int    Imported      { get; set; }
    public int    Failed        { get; set; }
    public string Status        { get; set; } = "";
    public string Message       { get; set; } = "";
}
