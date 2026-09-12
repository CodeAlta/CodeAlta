using System.Collections.Immutable;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Bounded renderer text, keyed by session plus run/content/channel. Not an agent event.</summary>
/// <param name="RunId">Original bounded run identifier, when supplied.</param>
/// <param name="ContentId">Original bounded content identifier.</param>
/// <param name="Kind">Text channel.</param>
/// <param name="Text">Retained prefix, at most <see cref="RuntimeDisplayProjection.MaxTextCharacters"/> UTF-16 code units.</param>
/// <param name="IsComplete">Whether finalized content was published.</param>
/// <param name="IsTruncated">Whether content exceeded the retained prefix bound.</param>
/// <param name="StartedWithDelta">No final content has established a complete baseline; earlier content may be absent.</param>
public readonly record struct RuntimeDisplayText(string? RunId, string ContentId, AgentContentKind Kind,
    string Text, bool IsComplete, bool IsTruncated, bool StartedWithDelta);

/// <summary>Latest reported plain ToolCall values only; not permission, process-start, run-completion or effect acknowledgment.</summary>
/// <param name="ProviderId">Exact provider.Value identity, at most 256 well-formed UTF-16 code units.</param>
/// <param name="RunId">Exact supplied run identity, at most 256 well-formed UTF-16 code units; null remains unknown.</param>
/// <param name="ActivityId">Exact activity identity, at most 256 well-formed UTF-16 code units.</param>
/// <param name="Phase">Latest supported published phase, which may regress; no lifecycle reconstruction.</param>
/// <param name="Name">Optional well-formed name prefix, at most 128 UTF-16 code units. A malformed name omits the entire report.</param>
/// <param name="IsNameTruncated">Whether a valid name exceeded the retained prefix bound.</param>
public readonly record struct RuntimeDisplayToolActivity(string ProviderId, string? RunId, string ActivityId,
    AgentActivityPhase Phase, string? Name, bool IsNameTruncated);

/// <summary>Latest published lifecycle values; not a recoverable execution/interaction authority.</summary>
/// <param name="Kind">Published lifecycle kind.</param>
/// <param name="RunId">Bounded display run identifier, possibly truncated.</param>
/// <param name="Message">Bounded diagnostic text, not an exception graph.</param>
public readonly record struct RuntimeDisplayLifecycle(SessionLifecycleEventKind Kind, string? RunId, string? Message);

/// <summary>Latest published configuration labels only; not editable settings or credentials.</summary>
/// <param name="ProviderId">Bounded provider label.</param>
/// <param name="ProviderKey">Bounded provider configuration key label.</param>
/// <param name="ModelId">Bounded model label.</param>
/// <param name="ReasoningEffort">Selected reasoning effort, when known.</param>
/// <param name="AgentPromptId">Bounded prompt label.</param>
public readonly record struct RuntimeDisplayConfiguration(string? ProviderId, string? ProviderKey, string? ModelId,
    AgentReasoningEffort? ReasoningEffort, string? AgentPromptId);

/// <summary>Immutable bounded live session window. Null status fields mean not yet observed, not idle/empty.</summary>
/// <param name="SessionId">Stable original identifier.</param>
/// <param name="Revision">Last publication revision for this session.</param>
/// <param name="Lifecycle">Latest published lifecycle, retained independently of text.</param>
/// <param name="QueuedPromptCount">Latest published queue count; queue payloads are intentionally omitted.</param>
/// <param name="Configuration">Latest published configuration labels.</param>
/// <param name="StatusKind">Latest host status kind, if any.</param>
/// <param name="StatusMessage">Latest bounded host status message.</param>
/// <param name="Text">Immutable bounded text items in least-to-most recently updated order.</param>
/// <param name="MetadataTruncated">At least one status/configuration/lifecycle label was truncated during this retained session window.</param>
/// <param name="EvictedTextItems">Text items evicted during this retained session window.</param>
/// <param name="UnsupportedEvents">Publications omitted from this vertical during this retained session window.</param>
public readonly record struct RuntimeDisplaySession(string SessionId, long Revision, RuntimeDisplayLifecycle? Lifecycle,
    int? QueuedPromptCount, RuntimeDisplayConfiguration? Configuration, AgentSessionUpdateKind? StatusKind,
    string? StatusMessage, ImmutableArray<RuntimeDisplayText> Text, bool MetadataTruncated,
    long EvictedTextItems, long UnsupportedEvents)
{
    /// <summary>At most two reported plain ToolCalls, least-to-most recently updated. Missing/evicted means unknown, not idle or complete.</summary>
    public ImmutableArray<RuntimeDisplayToolActivity> ToolActivities { get; init; } = [];

    /// <summary>Tool identities evicted during this retained session window; not a history/effect count.</summary>
    public long EvictedToolActivities { get; init; }
}

/// <summary>A complete replacement of the retained live display window, never a complete journal or M4 snapshot.</summary>
/// <param name="Epoch">Projection-owner identity; revisions from another epoch are unrelated.</param>
/// <param name="Revision">Atomic global publication revision, including closure.</param>
/// <param name="IsClosed">No further publications will be accepted.</param>
/// <param name="Sessions">Immutable retained sessions, ordered by last publication revision.</param>
/// <param name="EvictedSessions">Number of session windows evicted since this epoch began.</param>
/// <param name="OmittedSessionEvents">Publications with missing/oversized session identifiers, not retained.</param>
public readonly record struct RuntimeDisplaySnapshot(Guid Epoch, long Revision, bool IsClosed,
    ImmutableArray<RuntimeDisplaySession> Sessions, long EvictedSessions, long OmittedSessionEvents)
{
    /// <summary>Always true: bounded live status/text/reported ToolCalls only; history, tool payloads/effects, interactions, plugins and arbitrary details are not projected.</summary>
    public bool IsPartial => true;
}

/// <summary>Replace the entire local display window with Snapshot, even when HasGap is true. Never replay as plugin effects.</summary>
/// <param name="Snapshot">Committed replacement state.</param>
/// <param name="IsInitial">Atomic subscription baseline (also used for a closed owner).</param>
/// <param name="PreviousRevision">Last revision delivered on this observation; null for the baseline.</param>
/// <param name="HasGap">Intermediate revisions were coalesced; the replacement recovers retained state, not omitted history.</param>
public readonly record struct RuntimeDisplayReplacement(RuntimeDisplaySnapshot Snapshot, bool IsInitial,
    long? PreviousRevision, bool HasGap);
