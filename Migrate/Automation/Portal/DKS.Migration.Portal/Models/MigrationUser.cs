using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public enum UserMigrationStatus { Pending, InProgress, Completed, Failed }

public class MigrationUser
{
    [Key]
    public int UserId { get; set; }
    public int BatchId { get; set; }

    [MaxLength(200)]
    public string? FullName { get; set; }

    [Required, MaxLength(100)]
    public string WindowsUsername { get; set; } = "";

    [MaxLength(200)]
    public string? ComputerName { get; set; }

    [MaxLength(200)]
    public string? OldEmail { get; set; }

    [MaxLength(200)]
    public string? NewEmail { get; set; }

    [MaxLength(200)]
    public string? TargetMailbox { get; set; }

    public UserMigrationStatus Status { get; set; } = UserMigrationStatus.Pending;

    public MigrationBatch? Batch { get; set; }
}
