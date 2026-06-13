using Microsoft.EntityFrameworkCore;
using PstMigration.Domain.Entities;

namespace PstMigration.Infrastructure.Persistence;

public class MigrationDbContext : DbContext
{
    public MigrationDbContext(DbContextOptions<MigrationDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppRegistration> AppRegistrations => Set<AppRegistration>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentHeartbeat> AgentHeartbeats => Set<AgentHeartbeat>();
    public DbSet<PstFile> PstFiles => Set<PstFile>();
    public DbSet<PstFolder> PstFolders => Set<PstFolder>();
    public DbSet<MailboxMapping> MailboxMappings => Set<MailboxMapping>();
    public DbSet<MigrationJob> MigrationJobs => Set<MigrationJob>();
    public DbSet<MigrationJobMailbox> MigrationJobMailboxes => Set<MigrationJobMailbox>();
    public DbSet<MigrationItem> MigrationItems => Set<MigrationItem>();
    public DbSet<MigrationAttachment> MigrationAttachments => Set<MigrationAttachment>();
    public DbSet<MigrationError> MigrationErrors => Set<MigrationError>();
    public DbSet<MigrationAuditLog> AuditLogs => Set<MigrationAuditLog>();
    public DbSet<GraphRequestLog> GraphRequestLogs => Set<GraphRequestLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.TenantDomain).IsUnique();
            e.HasOne(x => x.AppRegistration).WithOne(x => x.Tenant!)
                .HasForeignKey<AppRegistration>(x => x.TenantId);
        });

        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => x.RegistrationTokenHash);
            e.HasOne(x => x.Tenant).WithMany(t => t.Agents).HasForeignKey(x => x.TenantId);
        });

        b.Entity<AgentHeartbeat>(e =>
            e.HasOne(x => x.Agent).WithMany(a => a.Heartbeats).HasForeignKey(x => x.AgentId));

        b.Entity<PstFile>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.Sha256 });
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<PstFolder>(e =>
            e.HasOne(x => x.PstFile).WithMany(f => f.Folders).HasForeignKey(x => x.PstFileId));

        b.Entity<MigrationJobMailbox>(e =>
        {
            e.HasOne(x => x.Job).WithMany(j => j.Mailboxes).HasForeignKey(x => x.JobId);
            e.HasOne(x => x.MailboxMapping).WithMany().HasForeignKey(x => x.MailboxMappingId);
        });

        // Idempotency: one row per source item per (job, mailbox, type).
        b.Entity<MigrationItem>(e =>
        {
            e.HasIndex(x => new { x.JobId, x.MailboxId, x.SourceItemId, x.ItemType }).IsUnique();
            e.HasMany(x => x.Attachments).WithOne(a => a.MigrationItem!).HasForeignKey(a => a.MigrationItemId);
        });

        b.Entity<MigrationError>(e => e.HasIndex(x => x.JobId));
        b.Entity<GraphRequestLog>(e => e.HasIndex(x => x.CorrelationId));
    }
}
