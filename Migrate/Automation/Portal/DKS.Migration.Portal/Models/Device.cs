using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public enum DeviceStatus
{
    Pending,
    AgentInstalled,
    AgentOnline,
    ScanningOutlook,
    PstFound,
    ExportingPst,
    PstExported,
    ReconfiguringProfile,
    ProfileReconfigured,
    ImportingPst,
    Completed,
    Failed,
    NeedManualAction
}

public class Device
{
    [Key]
    public int DeviceId { get; set; }
    public int BatchId { get; set; }

    [Required, MaxLength(200)]
    public string ComputerName { get; set; } = "";

    [MaxLength(100)]
    public string? WindowsUsername { get; set; }

    [MaxLength(50)]
    public string? AgentVersion { get; set; }

    [MaxLength(100)]
    public string? OsVersion { get; set; }

    [MaxLength(100)]
    public string? OutlookVersion { get; set; }

    [MaxLength(200)]
    public string? CurrentProfile { get; set; }

    [MaxLength(20)]
    public string? OldAccountType { get; set; }

    [MaxLength(200)]
    public string? OldEmail { get; set; }

    [MaxLength(200)]
    public string? NewMailbox { get; set; }

    public int PstCount { get; set; }
    public long TotalPstSizeBytes { get; set; }
    public DeviceStatus CurrentStatus { get; set; } = DeviceStatus.Pending;
    public DateTime? LastCheckIn { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    [MaxLength(100)]
    public string? PendingCommand { get; set; }

    /// <summary>Live import progress (0–100) reported by the agent during a Graph import; null when idle.</summary>
    public int? ImportPercent { get; set; }
    /// <summary>One-line import status (items/s, MB/s, ETA) shown under the progress bar.</summary>
    [MaxLength(300)]
    public string? ImportStatusText { get; set; }

    public MigrationBatch? Batch { get; set; }
    public ICollection<PstFile> PstFiles { get; set; } = new List<PstFile>();
    public ICollection<DeviceLog> Logs { get; set; } = new List<DeviceLog>();
}
