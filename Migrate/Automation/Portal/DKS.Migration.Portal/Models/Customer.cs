using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public class Customer
{
    [Key]
    public int CustomerId { get; set; }

    [Required, MaxLength(200)]
    public string CustomerName { get; set; } = "";

    [Required, MaxLength(50)]
    public string CustomerCode { get; set; } = "";

    [MaxLength(200)]
    public string? DestinationDomain { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MigrationBatch> Batches { get; set; } = new List<MigrationBatch>();
}
