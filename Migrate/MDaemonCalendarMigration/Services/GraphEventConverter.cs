using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MDaemonCalendarMigration.Models;

namespace MDaemonCalendarMigration.Services;

public class GraphEventConverter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Converts a ParsedEvent to a Microsoft Graph Event JSON body.
    /// Returns null if DTSTART cannot be parsed (skip this event).
    /// </summary>
    public string? ToGraphJson(ParsedEvent ev, string defaultTimeZone)
    {
        var startDt = ParseDt(ev.DtStart, ev.DtStartTzId, defaultTimeZone, ev.IsAllDay);
        if (startDt == null) return null;

        var endDt = ParseDt(ev.DtEnd, ev.DtEndTzId, defaultTimeZone, ev.IsAllDay)
                    ?? DeriveEnd(startDt.Value, ev.IsAllDay);

        var showAs = ev.Transp?.Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase) == true
            ? "free" : "busy";

        var sensitivity = ev.Class?.ToUpperInvariant() switch
        {
            "PRIVATE"       => "private",
            "CONFIDENTIAL"  => "confidential",
            _               => "normal"
        };

        object graphEvent = new
        {
            subject = string.IsNullOrEmpty(ev.Summary) ? "(No Title)" : ev.Summary,
            body    = new { contentType = "text", content = ev.Description ?? "" },
            start   = new { dateTime = Format(startDt.Value.dt), timeZone = startDt.Value.tz },
            end     = new { dateTime = Format(endDt.dt),         timeZone = endDt.tz },
            location    = string.IsNullOrEmpty(ev.Location) ? null : new { displayName = ev.Location },
            isAllDay    = ev.IsAllDay,
            showAs      = showAs,
            sensitivity = sensitivity
        };

        return JsonSerializer.Serialize(graphEvent, JsonOpts);
    }

    // ── date-time helpers ────────────────────────────────────────────────────

    private static (DateTime dt, string tz)? ParseDt(
        string? raw, string? tzId, string defaultTz, bool isAllDay)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        // All-day: YYYYMMDD
        if (isAllDay && raw.Length == 8)
        {
            return DateTime.TryParseExact(raw, "yyyyMMdd", null, DateTimeStyles.None, out var d)
                ? (d, "UTC") : null;
        }

        // UTC: ends with Z
        if (raw.EndsWith('Z'))
        {
            var s = raw.TrimEnd('Z');
            return DateTime.TryParseExact(s, "yyyyMMddTHHmmss", null, DateTimeStyles.None, out var d)
                ? (d, "UTC") : null;
        }

        // With TZID parameter
        if (!string.IsNullOrEmpty(tzId))
        {
            return DateTime.TryParseExact(raw, "yyyyMMddTHHmmss", null, DateTimeStyles.None, out var d)
                ? (d, tzId) : null;
        }

        // Floating — apply default timezone
        return DateTime.TryParseExact(raw, "yyyyMMddTHHmmss", null, DateTimeStyles.None, out var df)
            ? (df, defaultTz) : null;
    }

    private static (DateTime dt, string tz) DeriveEnd((DateTime dt, string tz) start, bool allDay) =>
        allDay
            ? (start.dt.AddDays(1),  start.tz)
            : (start.dt.AddHours(1), start.tz);

    private static string Format(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss");
}
