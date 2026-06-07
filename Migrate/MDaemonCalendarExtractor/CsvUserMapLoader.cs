namespace MDaemonCalendarExtractor;

public class CsvUserMapLoader
{
    /// <summary>
    /// Loads SourceEmail → TargetMailbox from a CSV with header row:
    /// SourceEmail,TargetMailbox
    /// </summary>
    public Dictionary<string, string> Load(string csvPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = File.ReadAllLines(csvPath, System.Text.Encoding.UTF8);
        bool firstLine = true;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (firstLine) { firstLine = false; continue; }   // skip header
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            var source = parts[0].Trim().ToLowerInvariant();
            var target = parts[1].Trim();
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target))
                map[source] = target;
        }

        return map;
    }
}
