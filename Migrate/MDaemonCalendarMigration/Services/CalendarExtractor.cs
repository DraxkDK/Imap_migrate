using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MDaemonCalendarMigration.Services;

public class ExtractionResult
{
    public List<string> Events  { get; init; } = new();
    public List<string> Timezones { get; init; } = new();
    public int FilesScanned           { get; set; }
    public int FilesWithCalendarBlocks { get; set; }
}

public class CalendarExtractor
{
    private static readonly string[] Exts = { ".mrk", ".msg", ".ics", ".txt" };

    public ExtractionResult Extract(string calendarPath, CancellationToken ct = default)
    {
        var result = new ExtractionResult();
        var seenEvents = new HashSet<string>();
        var seenTzIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(calendarPath)) return result;

        foreach (var file in Directory.GetFiles(calendarPath, "*.*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Array.IndexOf(Exts, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;

            result.FilesScanned++;
            var content = ReadSafe(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (HasQP(content)) content = DecodeQP(content);
            content = NormCrlf(content);

            bool hadBlock = false;
            foreach (var tz in ExtractBlocks(content, "VTIMEZONE"))
            {
                hadBlock = true;
                var id = GetProp(tz, "TZID");
                if (id != null && seenTzIds.Add(id)) result.Timezones.Add(tz);
            }
            foreach (var ev in ExtractBlocks(content, "VEVENT"))
            {
                hadBlock = true;
                if (seenEvents.Add(EventKey(ev))) result.Events.Add(ev);
            }
            if (hadBlock) result.FilesWithCalendarBlocks++;
        }
        return result;
    }

    private static string ReadSafe(string path)
    {
        try { return File.ReadAllText(path, new UTF8Encoding(false, false)); }
        catch
        {
            try { return File.ReadAllText(path, Encoding.GetEncoding(1252)); }
            catch { return ""; }
        }
    }

    private static bool HasQP(string s) =>
        s.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQP(string input)
    {
        input = Regex.Replace(input.Replace("\r\n", "\n").Replace("\r", "\n"), @"=\n", "");
        return Regex.Replace(input, @"=([0-9A-Fa-f]{2})",
            m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
    }

    private static string NormCrlf(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private static List<string> ExtractBlocks(string content, string type)
    {
        var list = new List<string>();
        var begin = $"BEGIN:{type}";
        var end   = $"END:{type}";
        int from  = 0;
        while (true)
        {
            int s = content.IndexOf(begin, from, StringComparison.OrdinalIgnoreCase);
            if (s < 0) break;
            int e = content.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) break;
            e += end.Length;
            if (e < content.Length && content[e] == '\r') e++;
            if (e < content.Length && content[e] == '\n') e++;
            list.Add(content[s..e]);
            from = e;
        }
        return list;
    }

    private static string? GetProp(string block, string name)
    {
        var m = Regex.Match(block, $@"^{Regex.Escape(name)}[;:]([^\r\n]+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string EventKey(string ev)
    {
        var uid     = GetProp(ev, "UID");
        var dtstart = GetProp(ev, "DTSTART");
        if (!string.IsNullOrEmpty(uid)) return $"U:{uid}|D:{dtstart ?? ""}";
        return "H:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ev)));
    }
}
