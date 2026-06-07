using System.Text;

namespace MDaemonCalendarMigration.Services;

public class IcsBuilder
{
    private static readonly char[] WinInvalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public string Build(List<string> events, List<string> timezones)
    {
        var sb = new StringBuilder(1024 * events.Count);
        sb.Append("BEGIN:VCALENDAR\r\n");
        sb.Append("VERSION:2.0\r\n");
        sb.Append("PRODID:-//DKS//MDaemon Calendar Extractor//EN\r\n");
        sb.Append("CALSCALE:GREGORIAN\r\n");
        sb.Append("METHOD:PUBLISH\r\n");
        foreach (var tz in timezones) AppendBlock(sb, tz);
        foreach (var ev in events)    AppendBlock(sb, ev);
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, string block)
    {
        var s = block.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        sb.Append(s);
        if (!s.EndsWith("\r\n")) sb.Append("\r\n");
    }

    public string GetOutputPath(string folder, string sourceEmail, int idx)
    {
        var name = Sanitize(sourceEmail);
        var file = idx == 0 ? $"{name}.ics" : $"{name}_{idx + 1}.ics";
        return Path.Combine(folder, file);
    }

    public void WriteIcs(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(false));

    private static string Sanitize(string s)
    {
        foreach (var c in WinInvalid) s = s.Replace(c, '_');
        return s;
    }
}
