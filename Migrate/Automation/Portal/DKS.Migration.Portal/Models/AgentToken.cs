using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public class AgentToken
{
    [Key]
    public int TokenId { get; set; }
    public int BatchId { get; set; }

    [Required, MaxLength(128)]
    public string Token { get; set; } = "";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public MigrationBatch? Batch { get; set; }
}
