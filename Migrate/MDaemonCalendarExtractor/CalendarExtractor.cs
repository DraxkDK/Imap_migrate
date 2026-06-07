using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MDaemonCalendarExtractor;

public class ExtractionResult
{
    public List<string> Events { get; init; } = new();
    public List<string> Timezones { get; init; } = new();
    public int FilesScanned { get; set; }
    public int FilesWithCalendarBlocks { get; set; }
}

public class CalendarExtractor
{
    private static readonly string[] TargetExtensions = { ".mrk", ".msg", ".ics", ".txt" };

    public ExtractionResult Extract(string calendarPath, CancellationToken ct = default)
    {
        var result = new ExtractionResult();
        var seenEventKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenTzIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(calendarPath))
            return result;

        foreach (var file in Directory.GetFiles(calendarPath, "*.*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (Array.IndexOf(TargetExtensions, Path.GetExtension(file).ToLowerInvariant()) < 0)
                continue;

            result.FilesScanned++;

            var content = ReadFileSafe(file);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (HasQuotedPrintable(content))
                content = DecodeQuotedPrintable(content);

            content = NormalizeCrlf(content);

            bool hadBlock = false;

            foreach (var tz in ExtractBlocks(content, "VTIMEZONE"))
            {
                hadBlock = true;
                var tzId = GetFirstPropertyValue(tz, "TZID");
                if (tzId != null && seenTzIds.Add(tzId))
                    result.Timezones.Add(tz);
            }

            foreach (var ev in ExtractBlocks(content, "VEVENT"))
            {
                hadBlock = true;
                var key = BuildEventKey(ev);
                if (seenEventKeys.Add(key))
                    result.Events.Add(ev);
            }

            if (hadBlock)
                result.FilesWithCalendarBlocks++;
        }

        return result;
    }

    // ── file reading ────────────────────────────────────────────────────────

    private static string ReadFileSafe(string path)
    {
        try
        {
            return File.ReadAllText(path, new UTF8Encoding(false, false));
        }
        catch
        {
            try
            {
                return File.ReadAllText(path, Encoding.GetEncoding(1252));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    // ── quoted-printable ────────────────────────────────────────────────────

    private static bool HasQuotedPrintable(string content) =>
        content.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQuotedPrintable(string input)
    {
        // Unfold soft-wrapped lines (trailing =)
        input = Regex.Replace(input.Replace("\r\n", "\n").Replace("\r", "\n"), @"=\n", "");

        // Decode =XX sequences
        return Regex.Replace(input, @"=([0-9A-Fa-f]{2})", m =>
            ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
    }

    // ── normalisation ───────────────────────────────────────────────────────

    private static string NormalizeCrlf(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    // ── block extraction ────────────────────────────────────────────────────

    private static List<string> ExtractBlocks(string content, string blockType)
    {
        var blocks = new List<string>();
        var begin = $"BEGIN:{blockType}";
        var end   = $"END:{blockType}";
        int from = 0;

        while (true)
        {
            int s = content.IndexOf(begin, from, StringComparison.OrdinalIgnoreCase);
            if (s < 0) break;

            int e = content.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) break;

            e += end.Length;
            // consume trailing CRLF so block is self-contained
            if (e < content.Length && content[e] == '\r') e++;
            if (e < content.Length && content[e] == '\n') e++;

            blocks.Add(content[s..e]);
            from = e;
        }

        return blocks;
    }

    // ── property helpers ────────────────────────────────────────────────────

    private static string? GetFirstPropertyValue(string block, string name)
    {
        var m = Regex.Match(block, $@"^{Regex.Escape(name)}[;:]([^\r\n]+)",
                            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ── deduplication ───────────────────────────────────────────────────────

    private static string BuildEventKey(string eventBlock)
    {
        var uid     = GetFirstPropertyValue(eventBlock, "UID");
        var dtstart = GetFirstPropertyValue(eventBlock, "DTSTART");

        if (!string.IsNullOrEmpty(uid))
            return $"U:{uid}|D:{dtstart ?? ""}";

        // No UID → hash entire block
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(eventBlock));
        return $"H:{Convert.ToHexString(hash)}";
    }
}
