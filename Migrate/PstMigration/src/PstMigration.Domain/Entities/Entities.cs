namespace PstMigration.Domain.Entities;

/// <summary>A target Microsoft 365 tenant being migrated into.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TenantDomain { get; set; } = "";
    public string EntraTenantId { get; set; } = "";
    /// <summary>SHA-256 of the per-tenant agent registration token (raw token never stored).</summary>
    public string? RegistrationTokenHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AppRegistration? AppRegistration { get; set; }
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();
}

/// <summary>Entra ID app registration used for app-only Graph auth (cert lives on the portal).</summary>
public class AppRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ClientId { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    /// <summary>Where the portal loads the cert from: "store:CurrentUser/My" or "file:/path/cert.pfx".</summary>
    public string CertificateLocation { get; set; } = "";
    /// <summary>DPAPI/secret-manager protected reference for the PFX password (never plaintext).</summary>
    public string? ProtectedCertPasswordRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Tenant? Tenant { get; set; }
}

/// <summary>A migration agent installed on a customer endpoint / migration server.</summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string MachineName { get; set; } = "";
    public string? AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Registered;
    /// <summary>Hash of the registration token (never store the raw token).</summary>
    public string RegistrationTokenHash { get; set; } = "";
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Tenant? Tenant { get; set; }
    public ICollection<AgentHeartbeat> Heartbeats { get; set; } = new List<AgentHeartbeat>();
}

public class AgentHeartbeat
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? CurrentJobId { get; set; }
    public int ActiveItems { get; set; }
    public double? CpuPercent { get; set; }
    public long? FreeDiskBytes { get; set; }

    public Agent? Agent { get; set; }
}

/// <summary>A PST file discovered by an agent. Content never leaves the endpoint.</summary>
public class PstFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public bool IsUnicode { get; set; }
    public bool IsCorrupted { get; set; }
    public int FolderCount { get; set; }
    public int MailCount { get; set; }
    public int ContactCount { get; set; }
    public int CalendarCount { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastScannedAt { get; set; }

    public Agent? Agent { get; set; }
    public ICollection<PstFolder> Folders { get; set; } = new List<PstFolder>();
}

public class PstFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PstFileId { get; set; }
    public string SourceFolderId { get; set; } = "";
    public string SourceFolderPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ParentSourceFolderId { get; set; }
    public int ItemCount { get; set; }
    public string? DestinationFolderId { get; set; }

    public PstFile? PstFile { get; set; }
}

/// <summary>Maps one PST to one target mailbox plus per-type modes.</summary>
public class MailboxMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PstFileId { get; set; }
    public string TargetMailbox { get; set; } = "";
    public string? DisplayName { get; set; }
    public EmailMigrationMode EmailMode { get; set; } = EmailMigrationMode.MetadataOnly;
    public CalendarMigrationMode CalendarMode { get; set; } = CalendarMigrationMode.Safe;
    public ContactMigrationMode ContactMode { get; set; } = ContactMigrationMode.Enabled;
    public string RootFolderName { get; set; } = "Imported PST";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public PstFile? PstFile { get; set; }
}

public class MigrationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public MigrationJobStatus Status { get; set; } = MigrationJobStatus.Draft;
    public Guid? AssignedAgentId { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MigrationJobMailbox> Mailboxes { get; set; } = new List<MigrationJobMailbox>();
}

public class MigrationJobMailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid MailboxMappingId { get; set; }
    public MigrationJobStatus Status { get; set; } = MigrationJobStatus.Draft;
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int SkippedItems { get; set; }
    public int WarningItems { get; set; }

    public MigrationJob? Job { get; set; }
    public MailboxMapping? MailboxMapping { get; set; }
}

/// <summary>One migrated item. Unique on (JobId, MailboxId, SourceItemId, ItemType) for idempotency.</summary>
public class MigrationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid MailboxId { get; set; }
    public Guid PstFileId { get; set; }
    public string SourceItemId { get; set; } = "";
    public string SourceFolderPath { get; set; } = "";
    public MigrationItemType ItemType { get; set; }
    public string SourceHash { get; set; } = "";
    public string? DestinationFolderId { get; set; }
    public string? DestinationItemId { get; set; }
    public MigrationItemStatus Status { get; set; } = MigrationItemStatus.Discovered;
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public string? FidelityWarning { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MigrationAttachment> Attachments { get; set; } = new List<MigrationAttachment>();
}

public class MigrationAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MigrationItemId { get; set; }
    public string SourceAttachmentId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsInline { get; set; }
    public MigrationItemStatus Status { get; set; } = MigrationItemStatus.Discovered;
    public string? LastErrorMessage { get; set; }

    public MigrationItem? MigrationItem { get; set; }
}

public class MigrationError
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? MailboxId { get; set; }
    public string? SourceItemId { get; set; }
    public MigrationItemType? ItemType { get; set; }
    public string ErrorCode { get; set; } = "";
    public string Message { get; set; } = "";
    public int HttpStatus { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MigrationAuditLog
{
    public long Id { get; set; }
    public string Action { get; set; } = "";
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Diagnostic log of Graph calls — never contains message/attachment bodies.</summary>
public class GraphRequestLog
{
    public long Id { get; set; }
    public Guid? JobId { get; set; }
    public string CorrelationId { get; set; } = "";
    public string Method { get; set; } = "";
    public string ResourcePath { get; set; } = "";
    public int StatusCode { get; set; }
    public int AttemptNumber { get; set; }
    public int DurationMs { get; set; }
    public string? RetryAfter { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
