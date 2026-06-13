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

    // Microsoft 365 / Graph app registration (per customer = per tenant).
    // Used by the Graph-based PST import (app-only auth). Secret encrypted at rest.
    [MaxLength(100)]
    public string? EntraTenantId { get; set; }
    [MaxLength(100)]
    public string? GraphClientId { get; set; }
    public string? ClientSecretEncrypted { get; set; }
    [MaxLength(100)]
    public string? CertThumbprint { get; set; }
    [MaxLength(300)]
    public string? CertLocation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MigrationBatch> Batches { get; set; } = new List<MigrationBatch>();
}
