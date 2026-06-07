using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public enum PstExportStatus { Pending, Exporting, Exported, Failed }
public enum PstImportStatus { Pending, Attaching, Attached, Importing, Imported, Failed }

public class PstFile
{
    [Key]
    public int PstId { get; set; }
    public int DeviceId { get; set; }
    public int? UserId { get; set; }

    [MaxLength(500)]
    public string? FileName { get; set; }

    [MaxLength(1000)]
    public string? SourcePath { get; set; }

    [MaxLength(1000)]
    public string? BackupPath { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string? Sha256 { get; set; }

    public PstExportStatus ExportStatus { get; set; } = PstExportStatus.Pending;
    public PstImportStatus ImportStatus { get; set; } = PstImportStatus.Pending;
    public DateTime? ExportedAt { get; set; }
    public DateTime? ImportedAt { get; set; }

    public Device? Device { get; set; }
}
