using System.Collections.Concurrent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>A run of the node API that the budgets of the machine admit before its prompt is sent.</summary>
/// <param name="RunId">The run: the session for a new one, the retry key of a send.</param>
/// <param name="Title">What the run is, for the owner.</param>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="Project">Its project; null for a chat.</param>
public sealed record FleetNodeBudgetRun(string RunId, string? Title, string? SpaceId, ProjectDescriptor? Project)
{
    /// <summary>Gets the nonce of the hold the run asks when a budget holds it; null for a new one. A send taken again after a restart asks the same hold.</summary>
    public string? Nonce { get; init; }

    /// <summary>Gets the person who started the session here through the organization; null for none (<see cref="Server.FleetBudgetRun.PersonId"/>).</summary>
    public string? PersonId { get; init; }

    /// <summary>Gets the organization of <see cref="PersonId"/>.</summary>
    public string? PersonOrganizationId { get; init; }
}

/// <summary>The statuses of a send that the budgets of the machine decided.</summary>
public static class FleetBudgetCommandStatuses
{
    /// <summary>The send waits for its owner to continue it anyway: it is sent then, or never when it is stopped.</summary>
    public const string Held = "held";

    /// <summary>A budget is reached and stops new runs: the prompt was not sent.</summary>
    public const string Reached = "budget_reached";
}

public sealed partial class FleetNodeService
{
    private const int MaxBudgetAdmitted = 4096;

    // The sends that wait for a person, by their retry key: a retry while one waits answers that it waits.
    private readonly ConcurrentDictionary<string, byte> _budgetHolds = new(StringComparer.Ordinal);

    // The sends the budgets admitted, by their retry key: a retry after a lost answer is the runtime's replay, not a new run.
    private readonly ConcurrentDictionary<string, byte> _budgetAdmitted = new(StringComparer.Ordinal);

    // The sends a budget holds, kept across restarts; created with the service.
    private FleetHeldSends HeldSends => LazyInitializer.EnsureInitialized(ref _heldSends, () => new FleetHeldSends(Path.Combine(_host.CatalogOptions.FleetRoot, "held-sends.json"),
        _options.HeldSendsProtection ?? new CodeAlta.Catalog.Secrets.LocalSecretStore(_host.CatalogOptions.GlobalRoot)));

    private FleetHeldSends? _heldSends;

    // A send of the node API as it is kept while a budget holds it.
    private static FleetHeldSend HeldSendOf(string runId, string sessionId, string text, string? title, OwnedSessionSelection? selection)
        => new(runId, sessionId, text, title, System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            selection?.ProviderKey, selection?.AgentPromptId, selection?.ModelId, selection?.ReasoningEffort?.ToString(), DateTimeOffset.UtcNow);

    // The send a held one makes once admitted: the owned command of its session, with its retry key. A send held is kept until
    // the runtime started its run (its prompt is in the journal then) or refused it: a process that ends before takes it again.
    private FleetCommandResult SendHeld(FleetHeldSend held)
    {
        OwnedSessionSelection? selection = held.ProviderKey is { } provider && held.AgentPromptId is { } prompt
            ? new OwnedSessionSelection(provider, prompt, held.ModelId,
                Enum.TryParse<CodeAlta.Agent.AgentReasoningEffort>(held.ReasoningEffort, ignoreCase: true, out var effort) ? effort : null)
            : null;
        var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest(held.RunId, held.SessionId, held.Text) { Selection = selection }, CancellationToken.None);
        if (admission is { Kind: OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay, Receipt: { } receipt })
            _ = ForgetOnceStartedAsync(held.HoldId, receipt.Completion);
        else HeldSends.Remove(held.HoldId);
        return Admit(admission);
    }

    // Forgets a held send once the runtime started its run, or ended it otherwise (a failure, a cancellation).
    private async Task ForgetOnceStartedAsync(string holdId, Task<OwnedSessionCommandResult> started)
    {
        try
        {
            await started.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            // Ended all the same: the runtime took it.
        }

        HeldSends.Remove(holdId);
    }

    // Whether the journal of a session holds the prompt of a send already (its run started before the node stopped): true when
    // found, false once the whole journal was read without it, null when the journal could not be read to its start. A send whose
    // search did not end is not sent: it stays held, and is looked for again at the next start.
    private async Task<bool?> PromptJournaledAsync(string sessionId, string retryKey)
    {
        // The pages of the longest journal read: one longer is not searched, and its held send waits for a person.
        const int MaxPages = 100_000;
        // A journal written while it is read is read again from its end, a few times.
        const int MaxRestarts = 8;
        CodeAlta.Agent.Runtime.AgentSessionHistoryCursor? cursor = null;
        var restarts = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            CodeAlta.Agent.Runtime.AgentSessionHistoryPage read;
            try
            {
                read = await _host.WorkspaceReads.ReadHistoryTailPageAsync(sessionId, cursor, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CodeAlta.Agent.Runtime.AgentSessionHistoryException exception) when (exception.Code == "history_changed" && restarts < MaxRestarts)
            {
                restarts++;
                cursor = null;
                page = -1;
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (exception is OwnedWorkspaceBusyException or CodeAlta.Agent.Runtime.AgentSessionHistoryException or IOException
                or UnauthorizedAccessException)
            {
                return null;
            }

            if (read.Entries.Any(entry => entry.Event is CodeAlta.Agent.AgentContentCompletedEvent { Kind: CodeAlta.Agent.AgentContentKind.User } prompt
                    && string.Equals(PromptImageHistory.ReadClientRequestId(prompt.Details), retryKey, StringComparison.Ordinal)))
                return true;
            // A journal whose end did not read (a record written while it was read) is not known to hold none.
            if (read.Next is not { } next) return read.TailOmitted ? null : false;
            cursor = next;
        }

        return null;
    }

    private int _resumed;

    /// <summary>
    /// Takes again the sends a budget held when the node stopped, once: each asks the same hold of the budgets, and is sent once
    /// admitted. Called by the host once what admits the runs (the budgets, the policies of the servers) is set, never before:
    /// a send held is not sent by a node that does not know its budgets yet. Their sessions wait for a person meanwhile.
    /// </summary>
    public void ResumeHeldSends()
    {
        if (_options.AdmitBudget is null || Interlocked.Exchange(ref _resumed, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(ResumeHeldSendsAsync);
    }

    // How often the sends this machine could not open (its protected store away) are tried again.
    internal TimeSpan BlockedHeldSendsRetry { get; set; } = TimeSpan.FromMinutes(1);

    // The held sends taken again by this process: one opened later is taken again once.
    private readonly ConcurrentDictionary<string, byte> _resumedHolds = new(StringComparer.Ordinal);

    // Takes again the sends a budget held when the node stopped: each asks the same hold, and is sent once admitted. Those this
    // machine cannot open now stay held, are reported, and are tried again until they open. Done once every send held at a reading
    // was taken again: one another reader opened meanwhile (the waiting sessions read the sends too) is still to be taken.
    private async Task ResumeHeldSendsAsync()
    {
        while (true)
        {
            FleetHeldSendsSnapshot held;
            try { held = HeldSends.Snapshot(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogInfo($"The held prompts were not taken again: {exception.Message}");
                return;
            }

            await ResumeOpenedHeldSendsAsync(held.Opened).ConfigureAwait(false);
            var waiting = held.HoldIds.Count(holdId => !_resumedHolds.ContainsKey(holdId));
            if (waiting == 0 || Volatile.Read(ref _disposed) != 0) return;
            LogWarn($"{waiting} held prompts cannot be opened now (the protected store of the machine is away): they stay held, and are tried again.");
            await Task.Delay(BlockedHeldSendsRetry).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0) return;
        }
    }

    private async Task ResumeOpenedHeldSendsAsync(IReadOnlyList<FleetHeldSend> opened)
    {
        foreach (var held in opened)
        {
            if (!_resumedHolds.TryAdd(held.HoldId, 0)) continue;
            try
            {
                if (await _host.RuntimeService.TryGetRecoverableSessionAsync(held.SessionId).ConfigureAwait(false) is not { } session)
                {
                    HeldSends.Remove(held.HoldId);
                    continue;
                }

                // Its run started before the node stopped, which had not forgotten it yet: it is not sent twice.
                switch (await PromptJournaledAsync(held.SessionId, held.RunId).ConfigureAwait(false))
                {
                    case true:
                        HeldSends.Remove(held.HoldId);
                        LogInfo($"The held prompt {held.HoldId} of the session {held.SessionId} was sent before the restart.");
                        continue;
                    case null:
                        // Not known whether it was sent: never sent twice, it stays held until the journal reads.
                        LogInfo($"The held prompt {held.HoldId} of the session {held.SessionId} is kept: its journal could not be searched to its start.");
                        continue;
                }

                var result = await WithinBudgetAsync(session, held.Title, held).ConfigureAwait(false);
                LogInfo($"The held prompt {held.HoldId} of the session {held.SessionId} was taken again after a restart ({result.Status}).");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException
                or FleetException or OwnedWorkspaceBusyException or CodeAlta.Agent.Runtime.AgentSessionHistoryException)
            {
                LogInfo($"The held prompt {held.HoldId} of the session {held.SessionId} was not taken again: {exception.Message}");
            }
        }
    }

    // Sends a prompt once the budgets of the machine admit it: at once when they do, never when one stops it, and later when one
    // holds it (the answer says it waits; the send is made once its owner continues it anyway). A send held is kept before the
    // answer says so: a restart takes it again, and its session waits for a person meanwhile. The run the budgets admit is the
    // one the hold is bound to (the session for the first prompt of a new one), asked again by the same after a restart.
    private async Task<FleetCommandResult> WithinBudgetAsync(SessionViewDescriptor session, string? title, FleetHeldSend held)
    {
        var runId = held.HoldId;
        Func<FleetCommandResult> send = () => SendHeld(held);
        if (_options.AdmitBudget is not { } admit || _budgetAdmitted.ContainsKey(runId)) return send();
        if (_budgetHolds.ContainsKey(runId)) return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
        var project = session.ProjectRef is { Length: > 0 } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId).ConfigureAwait(false) : null;
        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new FleetNodeBudgetRun(runId, title ?? session.Title, session.SpaceId, project)
        {
            Nonce = held.Nonce, PersonId = Principals.PersonOf(session.SessionId), PersonOrganizationId = Principals.OrganizationOf(session.SessionId),
        };
        var admission = admit(run, () => waits.TrySetResult(), CancellationToken.None);
        if (await Task.WhenAny(admission, waits.Task).ConfigureAwait(false) == admission)
        {
            var decided = await admission.ConfigureAwait(false);
            if (decided.Problem is { } problem)
            {
                HeldSends.Remove(runId);
                LogInfo($"The prompt of the session {session.SessionId} was not sent: {problem}");
                return new FleetCommandResult(FleetBudgetCommandStatuses.Reached, Code: FleetBudgetCommandStatuses.Reached);
            }

            // A stop that came while the budgets decided reaches it: checked again right before it is sent, and never sent then.
            if (Policy?.Halt is not null) HeldSends.Remove(runId);
            RequireNotHalted();
            // Forgotten once its run started (SendHeld), not before: a process that ends meanwhile takes it again.
            return Admitted(runId, send);
        }

        if (!_budgetHolds.TryAdd(runId, 0)) return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
        try
        {
            // Kept before the answer says it waits: a send accepted is never lost with the process.
            HeldSends.Add(held);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException)
        {
            _budgetHolds.TryRemove(runId, out _);
            LogInfo($"The prompt of the session {session.SessionId} that waits for its budget could not be kept: {exception.Message}");
            return new FleetCommandResult(FleetBudgetCommandStatuses.Reached, Code: FleetBudgetCommandStatuses.Reached);
        }
        catch (FleetHeldSendsFullException exception)
        {
            // Refused before the answer says it waits: the sends already held stay, none is evicted for this one.
            _budgetHolds.TryRemove(runId, out _);
            LogInfo($"The prompt of the session {session.SessionId} was not sent: {exception.Message}");
            return new FleetCommandResult("capacity", Code: "capacity");
        }

        LogInfo($"The prompt of the session {session.SessionId} waits for its budget.");
        _ = SendOnceAdmittedAsync(runId, session.SessionId, admission, send);
        return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
    }

    private async Task SendOnceAdmittedAsync(string runId, string sessionId, Task<Server.FleetBudgetAdmission> admission, Func<FleetCommandResult> send)
    {
        try
        {
            var decided = await admission.ConfigureAwait(false);
            // A node that stopped meanwhile leaves the send kept: its next start takes it again.
            if (Volatile.Read(ref _disposed) != 0) return;
            if (decided.Problem is { } problem)
            {
                HeldSends.Remove(runId);
                LogInfo($"The prompt of the session {sessionId} was not sent: {problem}");
                return;
            }

            // A stop of the kill switch that came while it waited reaches it: it is never sent.
            if (Policy?.Halt is { } halt)
            {
                HeldSends.Remove(runId);
                LogInfo($"The prompt of the session {sessionId} that waited for its budget was not sent: {halt.Describe(_options.Name)}");
                return;
            }

            // Forgotten once its run started (SendHeld), not before: a process that ends meanwhile takes it again, and finds
            // its prompt in the journal when its run had started.
            var result = Admitted(runId, send);
            LogInfo($"The prompt of the session {sessionId} was sent once its budget let it ({result.Status}).");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            LogInfo($"The prompt of the session {sessionId} that waited for its budget was not sent: {exception.Message}");
        }
        finally
        {
            _budgetHolds.TryRemove(runId, out _);
        }
    }

    // The gate this service gives the runtime, taken back when it stops.
    private readonly Func<CodeAlta.Orchestration.Runtime.QueuedRunAdmission, CancellationToken, Task<string?>>? _queuedRunGate;

    // A queued prompt about to start its turn: admitted as a send of its session, the queued item its run; it may wait. A steer
    // needs no admission: it goes into the turn that runs, which its own send or queued prompt was admitted for.
    private async Task<string?> AdmitQueuedAsync(CodeAlta.Orchestration.Runtime.QueuedRunAdmission queued, CancellationToken cancellationToken)
    {
        if (_options.AdmitBudget is not { } admit) return null;
        var session = await _host.RuntimeService.TryGetRecoverableSessionAsync(queued.SessionId, cancellationToken).ConfigureAwait(false);
        var project = session?.ProjectRef is { Length: > 0 } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
        var run = new FleetNodeBudgetRun(queued.QueueItemId, session?.Title, session?.SpaceId, project)
        {
            PersonId = Principals.PersonOf(queued.SessionId), PersonOrganizationId = Principals.OrganizationOf(queued.SessionId),
        };
        var decided = await admit(run, static () => { }, cancellationToken).ConfigureAwait(false);
        // The work of the fleet waits on a stop, checked once its admission is decided: a person's own work at the machine goes on.
        var problem = decided.Problem
            ?? (Policy?.Halt is { } halt && Work.List().Any(item => item.SessionId == queued.SessionId) ? halt.Describe(_options.Name) : null);
        if (problem is not null) LogInfo($"The queued prompt {queued.QueueItemId} of the session {queued.SessionId} was not sent: {problem}");
        return problem;
    }

    private FleetCommandResult Admitted(string runId, Func<FleetCommandResult> send)
    {
        // Bounded: the runtime keeps its own receipts for retries; this only spares them a second admission.
        if (_budgetAdmitted.Count >= MaxBudgetAdmitted) _budgetAdmitted.Clear();
        _budgetAdmitted[runId] = 0;
        return send();
    }
}
