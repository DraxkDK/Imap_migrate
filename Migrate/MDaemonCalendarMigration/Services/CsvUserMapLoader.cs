using System.Text;

namespace MDaemonCalendarMigration.Services;

public class CsvUserMapLoader
{
    public Dictionary<string, string> Load(string csvPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(csvPath, new UTF8Encoding(false, false));
        bool first = true;
        foreach (var raw in lines)
        {
            if (first) { first = false; continue; }   // skip header
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            var src = parts[0].Trim().ToLowerInvariant();
            var tgt = parts[1].Trim();
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                map[src] = tgt;
        }
        return map;
    }
}
