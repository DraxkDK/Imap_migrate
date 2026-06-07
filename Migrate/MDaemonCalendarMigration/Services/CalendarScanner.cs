using MDaemonCalendarMigration.Models;

namespace MDaemonCalendarMigration.Services;

public class CalendarScanner
{
    private static readonly string[] Exts = { ".mrk", ".msg", ".ics", ".txt" };

    public List<UserCalendarItem> Scan(string sourceFolder, Dictionary<string, string> userMap)
    {
        var results = new List<UserCalendarItem>();
        if (!Directory.Exists(sourceFolder)) return results;

        foreach (var domainDir in Directory.GetDirectories(sourceFolder))
        {
            var domain = Path.GetFileName(domainDir);
            foreach (var userDir in Directory.GetDirectories(domainDir))
            {
                var user = Path.GetFileName(userDir);
                var calPath = Path.Combine(userDir, "Calendar.IMAP");
                if (!Directory.Exists(calPath)) continue;

                var email = $"{user}@{domain}".ToLowerInvariant();
                CountFiles(calPath, out int cnt, out long bytes);

                userMap.TryGetValue(email, out var target);
                bool mapped = target != null;

                results.Add(new UserCalendarItem
                {
                    Selected       = mapped,
                    SourceEmail    = email,
                    TargetMailbox  = target ?? "",
                    CalendarPath   = calPath,
                    MappingStatus  = mapped ? "Mapped" : "Unmapped",
                    FileCount      = cnt,
                    EstimatedSizeMB = Math.Round(bytes / 1_048_576.0, 2),
                    Status         = "Ready"
                });
            }
        }
        return results;
    }

    private static void CountFiles(string folder, out int count, out long bytes)
    {
        count = 0; bytes = 0;
        foreach (var f in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            if (Array.IndexOf(Exts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
            count++;
            bytes += new FileInfo(f).Length;
        }
    }
}
