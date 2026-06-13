namespace PstMigration.Domain.Models;

/// <summary>An email address + display name pair.</summary>
public sealed class MailAddressModel
{
    public string? Name { get; init; }
    public required string Address { get; init; }
}

/// <summary>Summary of a PST returned by <c>IPstParser.InspectAsync</c>.</summary>
public sealed class PstMailboxInfo
{
    public required string PstPath { get; init; }
    public long FileSizeBytes { get; init; }
    public string? DisplayName { get; init; }
    public bool IsUnicode { get; init; }
    public bool IsEncrypted { get; init; }
    public int FolderCount { get; init; }
    public int MailCount { get; init; }
    public int ContactCount { get; init; }
    public int CalendarCount { get; init; }
    /// <summary>True if the parser detected corruption while inspecting.</summary>
    public bool IsCorrupted { get; init; }
    public string? InspectionWarning { get; init; }
}

/// <summary>A normalized folder node from a PST.</summary>
public sealed class MigrationFolder
{
    public required string SourceFolderId { get; init; }
    public required string SourceFolderPath { get; init; }
    public required string DisplayName { get; init; }
    public string? ParentSourceFolderId { get; init; }
    public MigrationItemType ContainerClass { get; init; } = MigrationItemType.Mail;
    public int ItemCount { get; init; }
}

/// <summary>A normalized attachment. Content is streamed lazily to avoid loading large blobs into memory.</summary>
public sealed class MigrationAttachment
{
    public required string SourceAttachmentId { get; init; }
    public required string FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }
    public bool IsInline { get; init; }
    public string? ContentId { get; init; }
    /// <summary>Opens a fresh read stream over the attachment content (local only).</summary>
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
}

/// <summary>A normalized mail item (see spec section 7).</summary>
public sealed class MigrationMailItem
{
    public required string SourceItemId { get; init; }
    public required string SourceFolderPath { get; init; }
    public string? InternetMessageId { get; init; }
    public string? Subject { get; init; }
    public MailAddressModel? From { get; init; }
    public IReadOnlyList<MailAddressModel> ToRecipients { get; init; } = Array.Empty<MailAddressModel>();
    public IReadOnlyList<MailAddressModel> CcRecipients { get; init; } = Array.Empty<MailAddressModel>();
    public IReadOnlyList<MailAddressModel> BccRecipients { get; init; } = Array.Empty<MailAddressModel>();
    public DateTimeOffset? OriginalSentDateTime { get; init; }
    public DateTimeOffset? OriginalReceivedDateTime { get; init; }
    public string? HtmlBody { get; init; }
    public string? TextBody { get; init; }
    public bool IsRead { get; init; }
    public string Importance { get; init; } = "normal";
    public bool HasAttachments { get; init; }
    public IReadOnlyList<MigrationAttachment> Attachments { get; init; } = Array.Empty<MigrationAttachment>();
    public IDictionary<string, string> SourceProperties { get; init; } = new Dictionary<string, string>();
}

/// <summary>A normalized contact.</summary>
public sealed class MigrationContactItem
{
    public required string SourceItemId { get; init; }
    public string? DisplayName { get; init; }
    public string? GivenName { get; init; }
    public string? MiddleName { get; init; }
    public string? Surname { get; init; }
    public IReadOnlyList<string> EmailAddresses { get; init; } = Array.Empty<string>();
    public string? BusinessPhone { get; init; }
    public string? MobilePhone { get; init; }
    public string? HomePhone { get; init; }
    public string? CompanyName { get; init; }
    public string? Department { get; init; }
    public string? JobTitle { get; init; }
    public string? BusinessAddress { get; init; }
    public string? HomeAddress { get; init; }
    public string? PersonalNotes { get; init; }
    public DateTimeOffset? Birthday { get; init; }
}

/// <summary>Recurrence pattern for a calendar event (simplified normalized form).</summary>
public sealed class RecurrenceModel
{
    public required string Pattern { get; init; }          // e.g. "daily", "weekly", "monthly", "yearly"
    public int Interval { get; init; } = 1;
    public IReadOnlyList<string> DaysOfWeek { get; init; } = Array.Empty<string>();
    public DateTimeOffset? RangeStart { get; init; }
    public DateTimeOffset? RangeEnd { get; init; }
    public int? NumberOfOccurrences { get; init; }
}

/// <summary>A normalized calendar event.</summary>
public sealed class MigrationCalendarItem
{
    public required string SourceItemId { get; init; }
    public string? Subject { get; init; }
    public string? Location { get; init; }
    public string? HtmlBody { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string TimeZone { get; init; } = "UTC";
    public bool IsAllDay { get; init; }
    public string ShowAs { get; init; } = "busy";
    public int? ReminderMinutesBeforeStart { get; init; }
    public RecurrenceModel? Recurrence { get; init; }
    public IReadOnlyList<MailAddressModel> Attendees { get; init; } = Array.Empty<MailAddressModel>();
    public MailAddressModel? Organizer { get; init; }
}

/// <summary>Result of attempting to migrate a single item to Graph.</summary>
public sealed class MigrationResult
{
    public required string SourceItemId { get; init; }
    public required MigrationItemType ItemType { get; init; }
    public required MigrationItemStatus Status { get; init; }
    public string? DestinationItemId { get; init; }
    public string? DestinationFolderId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FidelityWarning { get; init; }
    public TimeSpan Duration { get; init; }

    public static MigrationResult Ok(string sourceId, MigrationItemType type, string destId,
        string? destFolderId = null, string? fidelityWarning = null) => new()
    {
        SourceItemId = sourceId,
        ItemType = type,
        Status = fidelityWarning is null ? MigrationItemStatus.Completed : MigrationItemStatus.CompletedWithWarning,
        DestinationItemId = destId,
        DestinationFolderId = destFolderId,
        FidelityWarning = fidelityWarning,
    };

    public static MigrationResult Fail(string sourceId, MigrationItemType type, string code, string message) => new()
    {
        SourceItemId = sourceId,
        ItemType = type,
        Status = MigrationItemStatus.Failed,
        ErrorCode = code,
        ErrorMessage = message,
    };
}
