namespace PstMigration.Contracts;

// Agent -> Portal registration.
public sealed record AgentRegistrationRequest(
    string RegistrationToken,
    string MachineName,
    string? AgentVersion,
    string? OsVersion);

public sealed record AgentRegistrationResponse(
    Guid AgentId,
    string TenantDomain,
    int HeartbeatIntervalSeconds);

public sealed record AgentHeartbeatRequest(
    Guid AgentId,
    string? CurrentJobId,
    int ActiveItems,
    double? CpuPercent,
    long? FreeDiskBytes);

// Portal -> Agent: short-lived Graph access token (portal holds the certificate).
public sealed record GraphTokenRequest(Guid AgentId, string TargetMailbox);

public sealed record GraphTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresOn,
    string Resource);

// Portal -> Agent: agent runtime configuration.
public sealed record AgentConfigurationDto(
    int HeartbeatIntervalSeconds,
    int MaxConcurrentRequestsPerMailbox,
    int ScanIntervalMinutes,
    string[] DefaultPstFolders);

// Portal -> Agent: a job to process.
public sealed record JobAssignmentDto(
    Guid JobId,
    string JobName,
    IReadOnlyList<JobMailboxDto> Mailboxes);

public sealed record JobMailboxDto(
    Guid MailboxId,
    string PstPath,
    string TargetMailbox,
    string RootFolderName,
    string EmailMode,
    string CalendarMode,
    string ContactMode);

// Agent -> Portal: batched item status (metadata only — never bodies/attachment content).
public sealed record ItemStatusDto(
    string SourceItemId,
    string SourceFolderPath,
    string ItemType,
    long ItemSizeBytes,
    string? DestinationItemId,
    string Status,
    string? ErrorCode,
    string? WarningCode,
    int ProcessingDurationMs);

public sealed record ItemStatusBatchRequest(
    Guid JobId,
    Guid MailboxId,
    IReadOnlyList<ItemStatusDto> Items);

public sealed record JobStatusUpdateRequest(Guid JobId, string Status);

public sealed record ErrorReportDto(
    string ErrorCode,
    string Message,
    int HttpStatus,
    string? SourceItemId,
    string? ItemType);

public sealed record ErrorReportRequest(Guid JobId, Guid? MailboxId, IReadOnlyList<ErrorReportDto> Errors);

// Agent -> Portal: PST discovery inventory (metadata only).
public sealed record PstFolderDto(
    string SourceFolderId,
    string SourceFolderPath,
    string DisplayName,
    string? ParentSourceFolderId,
    int ItemCount);

public sealed record PstInventoryRequest(
    Guid AgentId,
    string Path,
    long SizeBytes,
    string Sha256,
    bool IsUnicode,
    bool IsCorrupted,
    int FolderCount,
    int MailCount,
    int ContactCount,
    int CalendarCount,
    IReadOnlyList<PstFolderDto> Folders);
