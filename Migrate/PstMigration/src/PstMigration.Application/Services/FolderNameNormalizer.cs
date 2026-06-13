using System.Text;

namespace PstMigration.Application.Services;

/// <summary>Normalizes raw PST folder names into Graph-safe mail folder names.</summary>
public interface IFolderNameNormalizer
{
    string Normalize(string? rawName);
}

public sealed class FolderNameNormalizer : IFolderNameNormalizer
{
    // Characters Exchange/Graph reject or that cause issues in folder names.
    private static readonly char[] Invalid = { '/', '\\', '\0' };
    private const int MaxLength = 250;

    public string Normalize(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "Unnamed Folder";

        var sb = new StringBuilder(rawName.Length);
        foreach (var ch in rawName)
        {
            if (Array.IndexOf(Invalid, ch) >= 0 || char.IsControl(ch))
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim().Trim('.');
        if (cleaned.Length == 0)
            cleaned = "Unnamed Folder";
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].Trim();

        return cleaned;
    }
}
