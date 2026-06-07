using MDaemonCalendarExtractor.Models;

namespace MDaemonCalendarExtractor;

public class CalendarScanner
{
    private static readonly string[] ScannedExtensions = { ".mrk", ".msg", ".ics", ".txt" };

    /// <summary>
    /// Walks sourceFolder\{domain}\{user}\Calendar.IMAP and returns one item per IMAP folder found.
    /// Expected path: sourceFolder\old-domain.com\user1\Calendar.IMAP
    /// Derived email:  user1@old-domain.com
    /// </summary>
    public List<UserCalendarItem> Scan(string sourceFolder, Dictionary<string, string> userMap)
    {
        var results = new List<UserCalendarItem>();

        if (!Directory.Exists(sourceFolder))
            return results;

        foreach (var domainDir in Directory.GetDirectories(sourceFolder))
        {
            var domain = Path.GetFileName(domainDir);

            foreach (var userDir in Directory.GetDirectories(domainDir))
            {
                var user = Path.GetFileName(userDir);
                var calendarPath = Path.Combine(userDir, "Calendar.IMAP");

                if (!Directory.Exists(calendarPath))
                    continue;

                var sourceEmail = $"{user}@{domain}".ToLowerInvariant();

                CountFiles(calendarPath, out int fileCount, out long totalBytes);

                var isMapped = userMap.TryGetValue(sourceEmail, out var target);

                results.Add(new UserCalendarItem
                {
                    Selected = isMapped,
                    SourceEmail = sourceEmail,
                    TargetMailbox = target ?? "",
                    CalendarPath = calendarPath,
                    MappingStatus = isMapped ? "Mapped" : "Unmapped",
                    FileCount = fileCount,
                    EstimatedSizeMB = Math.Round(totalBytes / 1_048_576.0, 2),
                    Status = "Ready",
                    EventCount = 0,
                    OutputIcs = "",
                    Message = ""
                });
            }
        }

        return results;
    }

    private static void CountFiles(string folder, out int count, out long bytes)
    {
        count = 0;
        bytes = 0;
        foreach (var file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (Array.IndexOf(ScannedExtensions, ext) < 0) continue;
            count++;
            bytes += new FileInfo(file).Length;
        }
    }
}
