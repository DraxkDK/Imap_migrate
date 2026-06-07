using System.Text;
using MDaemonCalendarExtractor.Models;

namespace MDaemonCalendarExtractor;

public class ReportWriter
{
    public string WriteReport(string reportFolder, List<ReportRow> rows)
    {
        Directory.CreateDirectory(reportFolder);
        var path = Path.Combine(reportFolder, "convert-report.csv");

        var sb = new StringBuilder();
        sb.AppendLine("SourceEmail,TargetMailbox,CalendarPath,OutputIcs,EventCount,FilesScanned,FilesWithCalendarBlocks,Status,Message");

        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                Esc(r.SourceEmail),
                Esc(r.TargetMailbox),
                Esc(r.CalendarPath),
                Esc(r.OutputIcs),
                r.EventCount,
                r.FilesScanned,
                r.FilesWithCalendarBlocks,
                Esc(r.Status),
                Esc(r.Message)));
        }

        // UTF-8 with BOM so Excel opens correctly without import wizard
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Esc(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return $"\"{v.Replace("\"", "\"\"")}\"";
        return v;
    }
}
