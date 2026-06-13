using System.Security.Cryptography;
using PstMigration.Application.Abstractions;
using PstMigration.Contracts;

namespace PstMigration.Agent.Services;

/// <summary>
/// Discovers PST files in the configured folders, computes their SHA-256, parses an
/// inventory (folders + counts) and reports metadata to the portal. The PST content
/// itself never leaves the machine.
/// </summary>
public sealed class PstScanner
{
    private readonly IPstParser _parser;
    private readonly PortalClient _portal;
    private readonly ILogger<PstScanner> _logger;

    public PstScanner(IPstParser parser, PortalClient portal, ILogger<PstScanner> logger)
    {
        _parser = parser;
        _portal = portal;
        _logger = logger;
    }

    public async Task ScanAsync(Guid agentId, IEnumerable<string> folders, CancellationToken ct)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                _logger.LogWarning("PST folder not found: {Folder}", folder);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.pst", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ScanFileAsync(agentId, path, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scan PST {Path}", path);
                }
            }
        }
    }

    private async Task ScanFileAsync(Guid agentId, string path, CancellationToken ct)
    {
        var sha = await ComputeSha256Async(path, ct);
        var info = await _parser.InspectAsync(path, ct);

        var folders = new List<PstFolderDto>();
        if (!info.IsCorrupted)
        {
            await foreach (var f in _parser.ReadFoldersAsync(path, ct))
            {
                folders.Add(new PstFolderDto(f.SourceFolderId, f.SourceFolderPath, f.DisplayName,
                    f.ParentSourceFolderId, f.ItemCount));
            }
        }

        var ok = await _portal.ReportInventoryAsync(new PstInventoryRequest(
            AgentId: agentId,
            Path: path,
            SizeBytes: info.FileSizeBytes,
            Sha256: sha,
            IsUnicode: info.IsUnicode,
            IsCorrupted: info.IsCorrupted,
            FolderCount: info.FolderCount,
            MailCount: info.MailCount,
            ContactCount: info.ContactCount,
            CalendarCount: info.CalendarCount,
            Folders: folders), ct);

        _logger.LogInformation("Scanned {Path}: {Folders} folders, {Mail} mail, reported={Ok}",
            path, info.FolderCount, info.MailCount, ok);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
