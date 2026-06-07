using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public enum SourceMailType { POP3, IMAP, Exchange }
public enum DestinationType { Microsoft365, GoogleWorkspace, iRedMail }
public enum AgentMode { ExportOnly, ReconfigureOnly, ImportOnly, ExportReconfigureImport }
public enum BatchStatus { Draft, Active, Paused, Completed, Failed }

public class MigrationBatch
{
    [Key]
    public int BatchId { get; set; }
    public int CustomerId { get; set; }

    [Required, MaxLength(200)]
    public string BatchName { get; set; } = "";

    public SourceMailType SourceType { get; set; } = SourceMailType.POP3;
    public DestinationType DestinationType { get; set; } = DestinationType.Microsoft365;

    [MaxLength(500)]
    public string? BackupPstPath { get; set; }

    public AgentMode Mode { get; set; } = AgentMode.ExportReconfigureImport;
    public DateTime? CutoverTime { get; set; }
    public bool RequireUserLogin { get; set; } = true;
    public bool CloseOutlookAutomatically { get; set; } = true;

    [MaxLength(200)]
    public string? ImportTargetFolder { get; set; }

    public bool RollbackEnabled { get; set; } = true;
    public BatchStatus Status { get; set; } = BatchStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Customer? Customer { get; set; }
    public ICollection<Device> Devices { get; set; } = new List<Device>();
    public ICollection<MigrationUser> Users { get; set; } = new List<MigrationUser>();
    public ICollection<AgentToken> Tokens { get; set; } = new List<AgentToken>();
}
