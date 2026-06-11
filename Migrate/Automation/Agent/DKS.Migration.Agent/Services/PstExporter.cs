using DKS.Migration.Agent.Models;
using System.Security.Cryptography;

namespace DKS.Migration.Agent.Services;

public class PstExporter
{
    private readonly ILogger<PstExporter> _logger;

    public PstExporter(ILogger<PstExporter> logger) => _logger = logger;

    public async Task<List<PstFileInfo>> ExportAllAsync(List<string> sourcePaths, string backupBasePath,
        string computerName, string? windowsUser, CancellationToken ct)
    {
        var results = new List<PstFileInfo>();
        var userFolder = Path.Combine(backupBasePath, computerName, windowsUser ?? "unknown");
        Directory.CreateDirectory(userFolder);

        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            var info = await ExportOneAsync(src, userFolder, ct);
            results.Add(info);
        }
        return results;
    }

    public async Task<PstFileInfo> ExportOneAsync(string sourcePath, string destFolder, CancellationToken ct)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destFolder, fileName);

        // Avoid overwriting — append timestamp if conflict
        if (File.Exists(destPath))
            destPath = Path.Combine(destFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMddHHmmss}.pst");

        _logger.LogInformation("Exporting PST: {Src} -> {Dest}", sourcePath, destPath);

        var info = new PstFileInfo { SourcePath = sourcePath, BackupPath = destPath };

        try
        {
            var fileInfo = new FileInfo(sourcePath);
            info.SizeBytes = fileInfo.Length;

            await CopyWithProgressAsync(sourcePath, destPath, fileInfo.Length, ct);

            info.Sha256 = await ComputeSha256Async(destPath, ct);
            info.IsExported = true;
            _logger.LogInformation("PST exported successfully. Size: {Size} MB, SHA256: {Hash}",
                info.SizeBytes / 1024 / 1024, info.Sha256?[..16]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export PST {Path}", sourcePath);
            info.IsExported = false;
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
        }

        return info;
    }

    private static async Task CopyWithProgressAsync(string src, string dest, long totalBytes, CancellationToken ct)
    {
        const int bufSize = 4 * 1024 * 1024; // 4MB buffer
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufSize, true);
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufSize, true);
        var buffer = new byte[bufSize];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
