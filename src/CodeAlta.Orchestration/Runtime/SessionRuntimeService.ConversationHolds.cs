namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    private readonly bool _askWhenConversationHeld;

    /// <summary>
    /// Gets the questions of runs whose conversation another process holds (a Claude Code conversation open in VS Code
    /// or a terminal), until the user answers them.
    /// </summary>
    public SessionConversationHeldService ConversationHolds { get; } = new();

    /// <summary>
    /// Gets or initializes whether the runs of the sessions this runtime attaches ask the user, through
    /// <see cref="ConversationHolds"/>, what to do when their conversation is held by another process. False, the
    /// default, for a host that does not show these questions: such a run fails with a message that says how to go on.
    /// </summary>
    public bool AskWhenConversationHeld
    {
        get => _askWhenConversationHeld;
        init
        {
            _askWhenConversationHeld = value;
            // A question is asked on the live attachment of its session, and ends with it.
            ConversationHolds.ResolveTarget = value
                ? sessionId => _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated && !entry.Attachment.IsRetiring
                    ? new SessionConversationHeldService.Target(_runtimeInstanceId, entry.Attachment, entry.ProviderId)
                    : null
                : null;
        }
    }
}
