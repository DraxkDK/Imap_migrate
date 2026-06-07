using System.Text;

namespace MDaemonCalendarExtractor;

public class IcsBuilder
{
    // Windows path chars that are invalid in filenames: \ / : * ? " < > |
    // @ and . are intentionally kept intact.
    private static readonly char[] WindowsInvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public string Build(List<string> events, List<string> timezones)
    {
        var sb = new StringBuilder(1024 * events.Count);
        sb.Append("BEGIN:VCALENDAR\r\n");
        sb.Append("VERSION:2.0\r\n");
        sb.Append("PRODID:-//DKS//MDaemon Calendar Extractor//EN\r\n");
        sb.Append("CALSCALE:GREGORIAN\r\n");
        sb.Append("METHOD:PUBLISH\r\n");

        foreach (var tz in timezones)
            AppendBlock(sb, tz);

        foreach (var ev in events)
            AppendBlock(sb, ev);

        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, string block)
    {
        // Ensure block uses CRLF and ends with CRLF
        var normalized = block.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        sb.Append(normalized);
        if (!normalized.EndsWith("\r\n"))
            sb.Append("\r\n");
    }

    /// <summary>
    /// Returns output file path for a given sourceEmail.
    /// index=0  → user@domain.ics
    /// index=1  → user@domain_2.ics
    /// </summary>
    public string GetOutputPath(string outputFolder, string sourceEmail, int index)
    {
        var safe = SanitizeFileName(sourceEmail);
        var name = index == 0 ? $"{safe}.ics" : $"{safe}_{index + 1}.ics";
        return Path.Combine(outputFolder, name);
    }

    public void WriteIcs(string path, string content) =>
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));  // UTF-8 no BOM

    private static string SanitizeFileName(string name)
    {
        foreach (var c in WindowsInvalidChars)
            name = name.Replace(c, '_');
        return name;
    }
}
