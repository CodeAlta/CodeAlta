namespace CodeAlta.LiveTool;

/// <summary>Identifies the committed reminder change whose observers were notified.</summary>
public enum AltaReminderChangeKind
{
    /// <summary>A reminder was created.</summary>
    Created,
    /// <summary>A reminder's content was updated.</summary>
    ContentUpdated,
    /// <summary>A reminder was deleted.</summary>
    Deleted,
    /// <summary>A delivery result and firing count were recorded.</summary>
    Fired,
    /// <summary>An exceptional runner failure was recorded.</summary>
    Failed,
}

/// <summary>
/// Immutable, bounded observer-failure feedback for a committed reminder change.
/// This is not a delivery failure or proof of runtime admission.
/// </summary>
public sealed class AltaReminderNotificationFailure
{
    internal AltaReminderNotificationFailure(string reminderId, string targetSessionId, AltaReminderChangeKind changeKind,
        int firedCount, int failureCount, string[] messages, bool messagesTruncated)
    {
        ReminderId = reminderId;
        TargetSessionId = targetSessionId;
        ChangeKind = changeKind;
        FiredCount = firedCount;
        FailureCount = failureCount;
        Messages = Array.AsReadOnly((string[])messages.Clone());
        MessagesTruncated = messagesTruncated;
    }

    /// <summary>Gets the affected reminder id, even if the reminder was subsequently deleted.</summary>
    public string ReminderId { get; }

    /// <summary>Gets the captured target session id.</summary>
    public string TargetSessionId { get; }

    /// <summary>Gets the committed change being notified, not necessarily the reminder's current state.</summary>
    public AltaReminderChangeKind ChangeKind { get; }

    /// <summary>Gets the fired count captured with the committed change.</summary>
    public int FiredCount { get; }

    /// <summary>Gets the total number of observers that threw, including omitted messages.</summary>
    public int FailureCount { get; }

    /// <summary>Gets an owned read-only collection of at most eight messages, each at most 512 characters.</summary>
    public IReadOnlyList<string> Messages { get; }

    /// <summary>Gets whether any messages were omitted or shortened.</summary>
    public bool MessagesTruncated { get; }
}
