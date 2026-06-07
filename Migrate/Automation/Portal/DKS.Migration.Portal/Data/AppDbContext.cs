using DKS.Migration.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace DKS.Migration.Portal.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<MigrationBatch> MigrationBatches => Set<MigrationBatch>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<MigrationUser> Users => Set<MigrationUser>();
    public DbSet<PstFile> PstFiles => Set<PstFile>();
    public DbSet<DeviceLog> Logs => Set<DeviceLog>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>()
            .HasIndex(c => c.CustomerCode).IsUnique();

        model.Entity<MigrationBatch>()
            .HasOne(b => b.Customer)
            .WithMany(c => c.Batches)
            .HasForeignKey(b => b.CustomerId);

        model.Entity<Device>()
            .HasOne(d => d.Batch)
            .WithMany(b => b.Devices)
            .HasForeignKey(d => d.BatchId);

        model.Entity<MigrationUser>()
            .HasOne(u => u.Batch)
            .WithMany(b => b.Users)
            .HasForeignKey(u => u.BatchId);

        model.Entity<PstFile>()
            .HasOne(p => p.Device)
            .WithMany(d => d.PstFiles)
            .HasForeignKey(p => p.DeviceId);

        model.Entity<DeviceLog>()
            .HasOne(l => l.Device)
            .WithMany(d => d.Logs)
            .HasForeignKey(l => l.DeviceId);

        model.Entity<AgentToken>()
            .HasOne(t => t.Batch)
            .WithMany(b => b.Tokens)
            .HasForeignKey(t => t.BatchId);

        model.Entity<AgentToken>()
            .HasIndex(t => t.Token).IsUnique();

        model.Entity<PortalUser>()
            .HasIndex(u => u.Username).IsUnique();
    }
}
