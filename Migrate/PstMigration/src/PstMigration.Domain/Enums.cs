namespace PstMigration.Domain;

/// <summary>High-level type of a migrated item.</summary>
public enum MigrationItemType
{
    Folder = 0,
    Mail = 1,
    Contact = 2,
    CalendarEvent = 3,
    Attachment = 4,
}

/// <summary>Lifecycle status of a single migration item (idempotent state machine).</summary>
public enum MigrationItemStatus
{
    Discovered = 0,
    Queued = 1,
    Processing = 2,
    Completed = 3,
    CompletedWithWarning = 4,
    Skipped = 5,
    RetryPending = 6,
    Failed = 7,
    Cancelled = 8,
}

/// <summary>Status of a migration job.</summary>
public enum MigrationJobStatus
{
    Draft = 0,
    Ready = 1,
    Running = 2,
    Paused = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    Failed = 6,
    Cancelled = 7,
    Queued = 8,
}

/// <summary>Email migration mode (see spec section 12).</summary>
public enum EmailMigrationMode
{
    Disabled = 0,
    MetadataOnly = 1,
    GraphBestEffort = 2,
}

/// <summary>Calendar migration mode (see spec section 11).</summary>
public enum CalendarMigrationMode
{
    Disabled = 0,
    /// <summary>No invitations/updates are ever sent; attendees preserved as metadata.</summary>
    Safe = 1,
    /// <summary>Advanced: includes attendees (may notify) — requires explicit opt-in.</summary>
    Advanced = 2,
}

/// <summary>Contact migration mode.</summary>
public enum ContactMigrationMode
{
    Disabled = 0,
    Enabled = 1,
}

/// <summary>Agent connectivity/health state.</summary>
public enum AgentStatus
{
    Unknown = 0,
    Registered = 1,
    Online = 2,
    Offline = 3,
    Disabled = 4,
    Revoked = 5,
}

/// <summary>Outcome level of the pre-migration readiness report.</summary>
public enum ReadinessLevel
{
    Ready = 0,
    ReadyWithWarnings = 1,
    Blocked = 2,
}

/// <summary>Portal RBAC roles (see spec section 4.2).</summary>
public enum PortalRole
{
    ReadOnlyAuditor = 0,
    MigrationOperator = 1,
    MigrationAdministrator = 2,
    GlobalAdministrator = 3,
}
