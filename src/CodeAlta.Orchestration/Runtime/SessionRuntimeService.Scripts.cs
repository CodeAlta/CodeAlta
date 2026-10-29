using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The scripts of Code Mode that run for sessions, as the runtime sees them: background work, as jobs are.</summary>
public interface ISessionScriptRuns
{
    /// <summary>Lists the sessions that have a script running.</summary>
    /// <returns>Their ids.</returns>
    IReadOnlyCollection<string> ListSessionsWithRunningScripts();

    /// <summary>Lists the scripts of a session as its background tasks (kind <c>workflow</c>): those that run, then those that ended a short while ago.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The tasks.</returns>
    IReadOnlyList<SessionRuntimeBackgroundTask> ListTasks(string sessionId);

    /// <summary>Stops the scripts of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>A task that completes when they ended.</returns>
    Task StopAsync(string sessionId);

    /// <summary>Stops one script of a session that runs, saying who stops it; its result still comes back to the session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="runId">The run.</param>
    /// <param name="by">Who stops it, such as <c>the user</c>; null to say nothing.</param>
    /// <returns>Whether a run of that session was running and is stopped.</returns>
    bool StopRun(string sessionId, string runId, string? by);

    /// <summary>
    /// Tells that a turn of a session ended: the results of the scripts that ended during the turn that started them, and
    /// that no wait took, come back to the session as prompts.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>A task that completes when the results are given.</returns>
    Task TurnEndedAsync(string sessionId);

    /// <summary>
    /// Gets the mode of Code Mode a session has now, on a provider and a model: <c>off</c>, <c>on</c> (it has <c>exec</c>)
    /// or <c>only</c> (<c>exec</c> takes over its tools).
    /// </summary>
    /// <param name="sessionId">The session, or null.</param>
    /// <param name="providerKey">The key of its provider, or null.</param>
    /// <param name="modelId">Its model, or null.</param>
    /// <returns>The mode.</returns>
    string CodeModeOf(string? sessionId, string? providerKey, string? modelId);
}

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Gets or sets the scripts of Code Mode of the host: an abort of a tree stops those of its sessions, and the sessions
    /// running one are at work when CodeAlta asks before exiting. Null for a host without them.
    /// </summary>
    public ISessionScriptRuns? ScriptRuns { get; set; }

    /// <summary>Writes a record of a run of a script in the journal of its session; a session that is not known gets nothing.</summary>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <param name="record">The record; its provider is the session's.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the record is written.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public async Task AppendScriptRecordAsync(string sessionId, AgentWorkflowEvent record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(record);
        if (await TryGetResumableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return;
        }

        await AppendSessionEventAsync(session, record with { ProviderId = new ModelProviderId(session.ResolvedProviderKey) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the turn a session runs now, for the scripts of Code Mode: the id of its run in progress, or null when it runs
    /// none (<see cref="Scripts.CodeModeRunRegistry.ActiveTurnOf"/>).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The id of the run, or null.</returns>
    public Task<string?> ReadActiveTurnAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadActiveRunIdAsync(sessionId);
    }

    /// <summary>
    /// Holds a session for a script that runs for it: until the hold is released, the session is not settled
    /// (<see cref="WhenSettledAsync"/> waits, so a headless run waits for its scripts) and is not retired.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The hold, or null when the session is retired or being retired.</returns>
    public IDisposable? TryHoldForScript(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        try
        {
            return ClaimRetentionWork(sessionId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
