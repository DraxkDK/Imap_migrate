using System.Text.RegularExpressions;
using MDaemonCalendarMigration.Models;

namespace MDaemonCalendarMigration.Services;

public class IcsEventParser
{
    public ParsedEvent Parse(string veventBlock)
    {
        var ev = new ParsedEvent { RawBlock = veventBlock };

        // RFC 5545 §3.1: unfold CRLF + whitespace soft-wraps
        var unfolded = Regex.Replace(
            veventBlock.Replace("\r\n", "\n").Replace("\r", "\n"),
            @"\n[ \t]", "");

        foreach (var line in unfolded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("END:",   StringComparison.OrdinalIgnoreCase))
                continue;

            var (propName, parameters, value) = SplitLine(line);
            if (propName == null) continue;

            switch (propName.ToUpperInvariant())
            {
                case "UID":         ev.Uid         = Unescape(value); break;
                case "SUMMARY":     ev.Summary     = Unescape(value); break;
                case "DESCRIPTION": ev.Description = Unescape(value); break;
                case "LOCATION":    ev.Location    = Unescape(value); break;
                case "TRANSP":      ev.Transp      = value;           break;
                case "CLASS":       ev.Class       = value;           break;
                case "STATUS":      ev.Status      = value;           break;
                case "RRULE":       ev.RRule       = value;           break;

                case "DTSTART":
                    ev.DtStart     = value;
                    ev.DtStartTzId = parameters.TryGetValue("TZID", out var tz1) ? tz1 : null;
                    ev.IsAllDay    = IsAllDay(value, parameters);
                    break;

                case "DTEND":
                case "DUE":
                    ev.DtEnd     = value;
                    ev.DtEndTzId = parameters.TryGetValue("TZID", out var tz2) ? tz2 : null;
                    break;
            }
        }
        return ev;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (string? name, Dictionary<string, string> parameters, string value) SplitLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return (null, new(), "");

        var nameAndParams = line[..colon];
        var value         = line[(colon + 1)..];
        var parts         = nameAndParams.Split(';');
        var name          = parts[0].Trim();

        var prms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq > 0)
                prms[parts[i][..eq].Trim()] = parts[i][(eq + 1)..].Trim().Trim('"');
        }
        return (name, prms, value);
    }

    private static string Unescape(string v) =>
        v.Replace("\\n", "\n").Replace("\\N", "\n")
         .Replace("\\,", ",").Replace("\\;", ";")
         .Replace("\\\\", "\\");

    private static bool IsAllDay(string v, Dictionary<string, string> prms)
    {
        // Explicit VALUE=DATE
        if (prms.TryGetValue("VALUE", out var val) &&
            val.Equals("DATE", StringComparison.OrdinalIgnoreCase)) return true;
        // Implicit: no 'T' part
        return !v.Contains('T');
    }
}
