using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What a session on Adaptive asks when it has the critics of its level review its change.</summary>
/// <param name="SessionId">The session whose change is reviewed.</param>
/// <param name="Base">
/// The commit the change starts from, when the folder also holds changes that are not the session's; null for the last
/// reviewed state of the request, else its start.
/// </param>
/// <param name="Notes">What the session says of its change, when the request of the user does not say it; null for nothing.</param>
public sealed record AdaptiveReviewRequest(string SessionId, string? Base = null, string? Notes = null);

/// <summary>How a request for a review by the critics of Adaptive was taken.</summary>
/// <param name="Status">
/// <c>started</c> when the critics review the change; else why not: <c>not_adaptive</c>, <c>no_critics</c>,
/// <c>no_change</c>, <c>running</c>, <c>invalid_base</c> or <c>unavailable</c>.
/// </param>
/// <param name="Message">What the session is told: what is reviewed and what comes next, or why nothing is reviewed and what to do instead.</param>
/// <param name="Level">The level of Adaptive of the session, or null when it is not on Adaptive.</param>
/// <param name="Critics">The models that review the change, as model references; empty when none does.</param>
/// <param name="Files">The files under review (the first ones of a long list); empty when nothing is reviewed.</param>
public sealed record AdaptiveReviewReceipt(string Status, string Message, string? Level, IReadOnlyList<string> Critics, IReadOnlyList<string> Files)
{
    /// <summary>The status of a review that started.</summary>
    public const string Started = "started";
}

/// <summary>Where a session stands with Adaptive and the reviews of its changes.</summary>
/// <param name="Adaptive">Whether the session is on Adaptive, with a level that is set.</param>
/// <param name="Level">Its level, or null.</param>
/// <param name="Critics">The critics of its level, as model references; empty for a level whose changes are rechecked.</param>
/// <param name="ReviewStartedAt">When the review that runs started, or null when none runs.</param>
/// <param name="UnreviewedFiles">The code files that changed since the last review of the request (the first ones of a long list).</param>
public sealed record AdaptiveSessionStatus(bool Adaptive, string? Level, IReadOnlyList<string> Critics, DateTimeOffset? ReviewStartedAt, IReadOnlyList<string> UnreviewedFiles);

// Adaptive, as the runtime sees it: what a session is told of it with its prompt, and the reviews it asks for.
public sealed partial class SessionRuntimeService
{
    // What starts a review and reads the state of one; the owner of the sends sets them (AdaptiveTurnCoordinator).
    internal Func<AdaptiveReviewRequest, CancellationToken, Task<AdaptiveReviewReceipt>>? AdaptiveReviewHandler { get; set; }

    internal Func<string, CancellationToken, Task<AdaptiveSessionStatus>>? AdaptiveStatusHandler { get; set; }

    /// <summary>
    /// Has the critics of the level of a session on Adaptive review the change of its request. It returns once the review
    /// started, or was refused; the verdict of the critics comes to the session later, as a prompt of the host.
    /// </summary>
    /// <param name="request">The session and what it says of its change.</param>
    /// <param name="cancellationToken">Stops the request, not a review that started.</param>
    /// <returns>How the request was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The session id is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    public Task<AdaptiveReviewReceipt> RequestAdaptiveReviewAsync(AdaptiveReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        return AdaptiveReviewHandler is { } handler
            ? handler(request, cancellationToken)
            : Task.FromResult(new AdaptiveReviewReceipt("unavailable", "This host runs no reviews of Adaptive.", null, [], []));
    }

    /// <summary>Reads where a session stands with Adaptive: its level, its critics, the review that runs, the code not reviewed yet.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The state; a session that is not on Adaptive has no level and no critics.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public Task<AdaptiveSessionStatus> GetAdaptiveStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdaptiveStatusHandler is { } handler
            ? handler(sessionId, cancellationToken)
            : Task.FromResult(new AdaptiveSessionStatus(false, null, [], null, []));
    }

    // Whether work a session started itself still goes on: a background job, or a run of a session it is the parent of.
    // Its result comes back to the session as a prompt, so a turn that ended is not the end of its request.
    internal bool HasOwnWorkPending(string sessionId)
    {
        if (Jobs.ListSessionsWithRunningJobs().Contains(sessionId, StringComparer.OrdinalIgnoreCase)) return true;
        foreach (var entry in _entries.Values)
        {
            if (!entry.IsTerminated && entry.HasActiveRun && string.Equals(entry.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // The note a run carries about Adaptive: the level and the critics of the session, when the agent was told something
    // else, or nothing yet while it is on Adaptive. Null when there is nothing to tell. The instructions stay the same:
    // their text is what the provider keeps in its cache, and a session goes on and off Adaptive during its life.
    private async Task<(string Text, string Noted)?> AdaptiveNoteForAsync(SessionViewDescriptor session, RuntimeSessionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var local = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
            AdaptiveLevelSettings? level = null;
            if (local?.Adaptive == true)
            {
                var scope = await LoadModelTierScopeAsync(entry.SpaceId, entry.ProjectId, cancellationToken).ConfigureAwait(false);
                level = scope.Adaptive?.Resolve(AdaptiveLevels.Parse(local.AdaptiveLevel));
            }

            var noted = level is null ? "off" : AdaptiveLevels.NameOf(level.Level) + ":" + string.Join(",", level.Critics.Select(static critic => critic.Format()));
            if (entry.AdaptiveNoted is { } told ? told == noted : noted == "off") return null;
            return (AdaptiveNoteText(level), noted);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // What cannot be read tells nothing now; the next run tries again.
            return null;
        }
    }

    /// <summary>The note that tells the agent of a session what Adaptive asks of it: null for a session that left Adaptive.</summary>
    internal static string AdaptiveNoteText(AdaptiveLevelSettings? level)
    {
        if (level is null)
            return "<codealta-context>Adaptive review: off. This session is no longer on Adaptive: no critics review its changes, and `alta adaptive review` no longer applies.</codealta-context>";
        var name = AdaptiveLevels.NameOf(level.Level);
        const string Before = "before you report it, and before any step that takes it out of this folder or is hard to undo: a push, a merge, a release, a deployment, the deletion of a branch";
        if (level.Critics.Count == 0)
            return $"<codealta-context>Adaptive recheck: on, level `{name}`, which has no critics. Recheck every change of code you make {Before}: "
                + "check it against each requirement of the request, one by one, with a test or a run for each rule it states, and fix what is missing or wrong. "
                + "When a turn ends with code that changed, CodeAlta asks you for that recheck once. Documentation alone, a question, or a commit need none.</codealta-context>";
        var critics = string.Join(", ", level.Critics.Select(static critic => "`" + critic.Format() + "`"));
        return $"<codealta-context>Adaptive review: on, level `{name}`. Critics of another model ({critics}) review the code you change. You start that review and you decide when: CodeAlta starts none by itself.\n"
            + $"- Have every change of code you make reviewed {Before}. Documentation alone, a question, or a commit need no review.\n"
            + "- When the change is complete and its checks pass, run `alta adaptive review`. The critics get the request of the user and the diff of your change since the last review; they read the code and run tests, read-only. The command returns at once. "
            + "Add `--message \"...\"` when the request alone does not say what your change is for, and `--base <commit>` when the folder also holds changes that are not yours (a branch you checked out, a pull): the review then covers what changed since that commit.\n"
            + "- Their verdict comes back to you as a prompt that starts with `[CodeAlta Adaptive]`, an approval or their findings. Until it comes, do not push, merge or publish the change, and do not edit the files under review. "
            + "When nothing else is left to do, end your turn saying that you wait for the review: that prompt starts you again.\n"
            + "- Check each finding yourself, fix the real ones and keep the tests passing. These fixes need no second review. Then do what waited for the review, and report.\n"
            + "- Sub-agents you start yourself are not the critics: their answer does not replace this review. A sub-agent cannot start this review: when one makes the change, tell it to leave it uncommitted and to report, then start the review yourself.\n"
            + "- When a turn of yours ends with code that was not reviewed, CodeAlta reminds you once.</codealta-context>";
    }

    // What the agent was told is kept with the session, so that a session that comes back is not told twice.
    private async Task RecordAdaptiveNotedAsync(SessionViewDescriptor session, RuntimeSessionEntry entry, string noted)
    {
        entry.AdaptiveNoted = noted;
        try
        {
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (localState is not null && localState.AdaptiveNoted != noted)
                {
                    localState.AdaptiveNoted = noted;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            // The next run of a session that comes back tells it again, which is harmless.
        }
    }
}
