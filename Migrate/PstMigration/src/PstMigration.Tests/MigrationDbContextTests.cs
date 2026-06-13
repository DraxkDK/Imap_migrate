using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PstMigration.Domain;
using PstMigration.Domain.Entities;
using PstMigration.Infrastructure.Persistence;
using Xunit;

namespace PstMigration.Tests;

public class MigrationDbContextTests
{
    private static MigrationDbContext NewDb(out SqliteConnection conn)
    {
        conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<MigrationDbContext>().UseSqlite(conn).Options;
        var db = new MigrationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Duplicate_item_with_same_key_is_rejected()
    {
        using var db = NewDb(out var conn);
        try
        {
            var jobId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();

            db.MigrationItems.Add(new MigrationItem
            {
                JobId = jobId, MailboxId = mailboxId, SourceItemId = "item-1",
                ItemType = MigrationItemType.Mail, Status = MigrationItemStatus.Completed,
            });
            await db.SaveChangesAsync();

            db.MigrationItems.Add(new MigrationItem
            {
                JobId = jobId, MailboxId = mailboxId, SourceItemId = "item-1",
                ItemType = MigrationItemType.Mail, Status = MigrationItemStatus.Completed,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Same_source_id_different_type_is_allowed()
    {
        using var db = NewDb(out var conn);
        try
        {
            var jobId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();

            db.MigrationItems.Add(new MigrationItem { JobId = jobId, MailboxId = mailboxId, SourceItemId = "x", ItemType = MigrationItemType.Mail });
            db.MigrationItems.Add(new MigrationItem { JobId = jobId, MailboxId = mailboxId, SourceItemId = "x", ItemType = MigrationItemType.Contact });

            var saved = await db.SaveChangesAsync();
            Assert.Equal(2, saved);
        }
        finally { conn.Dispose(); }
    }
}
