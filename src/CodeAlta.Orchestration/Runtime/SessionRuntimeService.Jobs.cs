using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Jobs;

namespace CodeAlta.Orchestration.Runtime;

// The background jobs of the sessions: commands a session starts without waiting for them. The runtime owns them
// because it is what gives a session the prompt that tells their end, and what says what a session is doing.
public sealed partial class SessionRuntimeService
{
    /// <summary>The kind a prompt that tells the end of a background job is queued and recorded with.</summary>
    public const string JobPromptKind = "job";

    /// <summary>
    /// Gets the background jobs of the sessions. They are ended when the runtime is disposed.
    /// </summary>
    public SessionJobService Jobs { get; }

    /// <summary>
    /// Gives a session a prompt that comes from the host, not from the user: a turn that runs is given it at
    /// once when its provider can take it, otherwise the prompt is queued and starts the next turn.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="kind">What the prompt is, as its record says it (for example <see cref="JobPromptKind"/>).</param>
    /// <param name="cancellationToken">Cancels the admission of the request, not a delivery that started.</param>
    /// <returns>
    /// <c>steered</c> when a running turn took the prompt, <c>queued</c> when it waits for the turn it starts,
    /// <c>missing_session</c> when the session is not known.
    /// </returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled before it was admitted.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    public Task<string> DeliverHostPromptAsync(string sessionId, string prompt, string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return AdmitAsync(() => DeliverHostPromptBodyAsync(sessionId, prompt, kind, [], whenIdle: true), cancellationToken);
    }

    /// <summary>
    /// Gives a session a prompt that comes from the host with images, as <see cref="DeliverHostPromptAsync(string, string, string, CancellationToken)"/>
    /// does: the images are saved with the attachments of the session, as those of a prompt of the user, and sent after its text.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="kind">What the prompt is, as its record says it (for example <see cref="AgentPromptProvenance.Workflow"/>).</param>
    /// <param name="images">The images, such as the pictures of the result of a script; empty for none.</param>
    /// <param name="cancellationToken">Cancels the admission of the request, not a delivery that started.</param>
    /// <returns>
    /// <c>steered</c> when a running turn took the prompt, <c>queued</c> when it waits for the turn it starts,
    /// <c>missing_session</c> when the session is not known.
    /// </returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled before it was admitted.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    public Task<string> DeliverHostPromptAsync(string sessionId, string prompt, string kind, IReadOnlyList<PromptImageAttachment> images, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(images);
        return AdmitAsync(() => DeliverHostPromptBodyAsync(sessionId, prompt, kind, images, whenIdle: true), cancellationToken);
    }

    /// <summary>
    /// Gives a session whose turn runs a prompt that comes from the host, as <see cref="DeliverHostPromptAsync"/>
    /// does, and leaves a session that is idle alone: nothing is queued for it, and the caller sends the prompt as
    /// a turn of its own (a reminder of the desktop, which is sent with the policy of the sends of its window).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="kind">What the prompt is, as its record says it (for example <c>reminder</c>).</param>
    /// <param name="cancellationToken">Cancels the admission of the request, not a delivery that started.</param>
    /// <returns>
    /// <c>steered</c> when the running turn took the prompt, <c>queued</c> when that turn could not take it and
    /// the prompt starts the next one, <c>idle</c> when no turn runs and nothing was done,
    /// <c>missing_session</c> when the session is not known.
    /// </returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled before it was admitted.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    public Task<string> DeliverHostPromptToRunningTurnAsync(string sessionId, string prompt, string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return AdmitAsync(() => DeliverHostPromptBodyAsync(sessionId, prompt, kind, [], whenIdle: false), cancellationToken);
    }

    private Task<string> DeliverHostPromptBodyAsync(string sessionId, string prompt, string kind, IReadOnlyList<PromptImageAttachment> images, bool whenIdle)
        => WithRetentionWorkAsync(() => DeliverHostPromptClaimedBodyAsync(sessionId, prompt, kind, images, whenIdle), sessionId);

    private async Task<string> DeliverHostPromptClaimedBodyAsync(string sessionId, string prompt, string kind, IReadOnlyList<PromptImageAttachment> images, bool whenIdle)
    {
        var session = await TryResolveSessionForParentDeliveryAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null) return "missing_session";
        // A session another process runs gets nothing written in its journal from here.
        using var lease = HoldForWrite(sessionId);
        return await WithRetentionWorkAsync(async () =>
        {
        var running = await HasActiveRunOwnedBodyAsync(session, CancellationToken.None).ConfigureAwait(false);
        if (!running && !whenIdle) return "idle";
        // The images are kept with the attachments of the session, where the timeline reads those of a prompt of the user.
        IReadOnlyList<PromptImageAttachmentReference> saved = [];
        try
        {
            if (images.Count > 0) saved = await new PromptImageAttachmentStore(_catalogOptions).SaveAsync(session, images, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The prompt still goes: its text names the images it could not keep.
        }

        var input = saved.Count == 0 ? AgentInput.Text(prompt)
            : new AgentInput([new AgentInputItem.Text(prompt), .. saved.Select(static image => new AgentInputItem.LocalImage(image.Path, image.Title, image.MediaType))]);
        var submittedBy = new AltaActorProvenance
        {
            Kind = kind,
            SourceSessionId = session.SessionId,
            SourceProjectId = session.ProjectRef,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        List<SessionViewQueuedPromptImage> QueuedImages()
            => [.. saved.Select(static image => new SessionViewQueuedPromptImage { Path = image.Path, Title = image.Title, MediaType = image.MediaType })];
        if (running)
        {
            try
            {
                var runId = await SteerOwnedBodyAsync(session, CreateParentDeliveryExecutionOptions(session),
                    new AgentSteerOptions
                    {
                        Input = input, Provenance = SessionPromptProvenance.Of(kind, submittedBy),
                        // A run that fails or is aborted before it takes the prompt hands it back: it waits its turn.
                        NotTaken = () => _ = QueueNotTakenSteerAsync(session, prompt, kind, submittedBy, QueuedImages(), session.SessionId),
                    },
                    CancellationToken.None).ConfigureAwait(false);
                await PersistPromptProvenanceAsync(session, runId.Value, queued: false, kind, prompt, submittedBy, CancellationToken.None).ConfigureAwait(false);
                return "steered";
            }
            catch (Exception failure) when (!OwnedProviderEventForwarding.HasRetention(failure))
            {
                // A provider that takes nothing in a turn that runs, or a turn that just ended: the prompt waits its turn.
            }
        }

        await QueuePromptOwnedBodyAsync(session, prompt, kind, submittedBy, QueuedImages(), CancellationToken.None).ConfigureAwait(false);
        await TryDrainNextQueuedPromptAsync(session.SessionId).ConfigureAwait(false);
        return "queued";
        }, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    // Called by the jobs when one ended and its session is to be told.
    private async Task<string> DeliverJobResultAsync(SessionJob job, string prompt)
    {
        try
        {
            var status = await DeliverHostPromptAsync(job.SessionId, prompt, JobPromptKind).ConfigureAwait(false);
            return status == "missing_session" ? "failed" : status;
        }
        catch (Exception failure) when (!OwnedProviderEventForwarding.HasRetention(failure)
            && failure is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException)
        {
            return "failed";
        }
    }

    /// <summary>
    /// Counts the sessions that are at work right now: a run in flight, or a background job that runs. Like
    /// <see cref="CountActiveRuns"/> it is a reading for the user (a question before the application exits, which
    /// ends the jobs), taken without waiting for any session.
    /// </summary>
    /// <returns>How many sessions would lose work if the host exited now.</returns>
    public int CountSessionsAtWork()
    {
        var working = new HashSet<string>(Jobs.ListSessionsWithRunningJobs(), StringComparer.OrdinalIgnoreCase);
        if (ScriptRuns is { } scripts) working.UnionWith(scripts.ListSessionsWithRunningScripts());
        foreach (var entry in _entries.Values)
        {
            if (!entry.IsTerminated && entry.HasActiveRun) working.Add(entry.SessionId);
        }

        return working.Count;
    }

    private async Task<bool> StopJobAsync(string sessionId, string jobId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Jobs.Get(jobId) is not { State: SessionJobState.Running } job || !string.Equals(job.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return false;
        return await Jobs.CancelAsync(jobId, byUser: true, cancellationToken).ConfigureAwait(false) is not null;
    }

    // The background tasks of a session with its jobs: what goes on first, then what ended.
    private IReadOnlyList<SessionRuntimeBackgroundTask> WithJobTasks(IReadOnlyList<SessionRuntimeBackgroundTask> tasks, string sessionId)
    {
        // The scripts of the session are background work as its jobs are.
        IReadOnlyList<SessionRuntimeBackgroundTask> jobs = ScriptRuns?.ListTasks(sessionId) is { Count: > 0 } scripts ? [.. JobTasks(sessionId), .. scripts] : JobTasks(sessionId);
        return jobs.Count == 0 ? tasks
            : [.. tasks.Where(static task => task.Outcome is null), .. jobs.Where(static task => task.Outcome is null),
                .. jobs.Where(static task => task.Outcome is not null), .. tasks.Where(static task => task.Outcome is not null)];
    }

    // The jobs of a session as its background tasks: the ones that run, then the last ones that ended.
    private IReadOnlyList<SessionRuntimeBackgroundTask> JobTasks(string sessionId)
        => [.. Jobs.List(sessionId).Take(MaximumJobTasks).Select(static job => new SessionRuntimeBackgroundTask(job.Id, "command", job.Label, null, job.StartedAt,
            job.State switch
            {
                SessionJobState.Running => null,
                SessionJobState.Succeeded => AgentBackgroundTaskOutcome.Completed,
                SessionJobState.Failed or SessionJobState.TimedOut => AgentBackgroundTaskOutcome.Failed,
                _ => AgentBackgroundTaskOutcome.Stopped,
            })
        { IsJob = true, ExitCode = job.ExitCode, EndedAt = job.EndedAt })];

    private const int MaximumJobTasks = 8;
}
