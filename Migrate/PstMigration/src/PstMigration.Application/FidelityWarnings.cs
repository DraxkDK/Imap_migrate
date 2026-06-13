namespace PstMigration.Application;

/// <summary>
/// Documented fidelity limitations of Graph best-effort migration (spec section 2).
/// These are surfaced in reports and the portal — we never claim full-fidelity PST import.
/// </summary>
public static class FidelityWarnings
{
    public const string TimestampsNotPreserved = "ORIGINAL_TIMESTAMPS_NOT_PRESERVED";
    public const string DraftLikeSemantics = "DRAFT_LIKE_SEMANTICS";
    public const string ConversationNotRecreated = "CONVERSATION_NOT_RECREATED";
    public const string ExchangeInternalPropsLost = "EXCHANGE_INTERNAL_PROPERTIES_LOST";
    public const string SentItemsBehaviorDiffers = "SENT_ITEMS_BEHAVIOR_DIFFERS";
    public const string AttendeesPreservedAsMetadata = "ATTENDEES_PRESERVED_AS_METADATA";

    public const string EmailModeBanner =
        "Email migration through Microsoft Graph is a best-effort reconstruction. " +
        "It is not equivalent to Microsoft Purview PST Import and may not preserve " +
        "all original Exchange properties, timestamps, conversation information, " +
        "or historical received/sent semantics.";
}

/// <summary>Abstraction over the system clock for testability.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
