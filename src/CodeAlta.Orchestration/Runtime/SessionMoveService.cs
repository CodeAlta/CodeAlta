using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The machine that decides whether a move happened: the server in a fleet, the target in paired mode. Its decision is
/// made once and stands: a move is committed or aborted, never both.
/// </summary>
public interface ISessionMoveArbiter
{
    /// <summary>Commits a move, unless it was aborted before.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="epoch">The ownership epoch the move gives the session.</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns>The decision that stands.</returns>
    /// <exception cref="IOException">The arbiter cannot be reached or cannot record its decision.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    Task<SessionMoveDecision> CommitAsync(string moveId, long epoch, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the outcome of a move after a crash or a lost connection. A move that was not committed is aborted then, so
    /// that a commit that comes later is refused.
    /// </summary>
    /// <param name="moveId">The move.</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns>The decision that stands; <see langword="null"/> when the arbiter cannot be reached (nothing is undone then).</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    Task<SessionMoveDecision?> ResolveAsync(string moveId, CancellationToken cancellationToken);
}

/// <summary>The arbiter of the moves this machine decides, from its own records: the target, in paired mode.</summary>
/// <param name="store">The records of this machine.</param>
public sealed class LocalSessionMoveArbiter(SessionMoveTransactionStore store) : ISessionMoveArbiter
{
    private readonly SessionMoveTransactionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task<SessionMoveDecision> CommitAsync(string moveId, long epoch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.Decide(moveId, SessionMoveState.Committed, epoch));
    }

    /// <inheritdoc />
    public Task<SessionMoveDecision?> ResolveAsync(string moveId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<SessionMoveDecision?>(_store.ReadDecision(moveId) ?? _store.Decide(moveId, SessionMoveState.Aborted, 0));
    }
}

/// <summary>What a step of a move did.</summary>
/// <param name="Status">
/// The state the move is in on this machine (<see cref="SessionMoveState"/>), or why the step was refused (a status of
/// the export or of the preparation).
/// </param>
/// <param name="Transaction">The record of the move on this machine, when there is one.</param>
/// <param name="Warnings">What went wrong without stopping the step, in words.</param>
public sealed record SessionMoveStepResult(string Status, SessionMoveTransaction? Transaction, IReadOnlyList<string> Warnings);

/// <summary>
/// The moves of the sessions of one host to and from other machines, as a recoverable transaction recorded durably on
/// both sides: <c>exporting</c> (the source records that it freezes the session, before it does), <c>offered</c> (the
/// source froze the session and made its bundle), <c>prepared</c> (the target holds a
/// verified copy and a worktree), <c>committed</c> (the arbiter recorded the new owner), <c>finished</c> (the target
/// installed the session, or the source relinquished it), or <c>aborted</c>.
/// </summary>
/// <remarks>
/// <para>The network part drives it: it carries the bundle and calls the steps of each side. This service moves no byte
/// between machines.</para>
/// <para>Recovery (<see cref="RecoverAsync"/>) asks the arbiter before it does anything: a lost acknowledgement never
/// makes the source take the session back, and only a move the arbiter never committed is undone (the source unfreezes
/// the session, the target removes what it prepared). A target that crashed after the commit installs the session.</para>
/// </remarks>
public sealed class SessionMoveService
{
    private readonly SessionRuntimeService _runtime;
    private readonly OwnedSessionCommandService _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes the moves of a host.</summary>
    /// <param name="host">The host.</param>
    /// <param name="machineName">The name of this machine, as the other machines know it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public SessionMoveService(CodeAltaHost host, string? machineName)
    {
        ArgumentNullException.ThrowIfNull(host);
        _runtime = host.RuntimeService;
        _commands = host.Commands;
        Store = new SessionMoveTransactionStore(host.CatalogOptions);
        MachineName = machineName;
    }

    /// <summary>Gets the records of the moves of this machine.</summary>
    public SessionMoveTransactionStore Store { get; }

    /// <summary>Gets the name of this machine.</summary>
    public string? MachineName { get; }

    // Runs once the session is frozen and its bundle written, before the move is recorded as offered: a test stops the
    // source there as a crash would.
    internal Func<string, Task>? AfterExportForTesting { get; init; }

    /// <summary>
    /// Undoes, when the source starts, the exports a crash interrupted before the move was offered to any machine: the
    /// moves recorded as <see cref="SessionMoveState.Exporting"/>, and the sessions frozen by a move this machine has no
    /// record of. Their bundle goes, the session is written here again and the move is recorded as aborted. A move
    /// recorded as offered is left to its arbiter (<see cref="RecoverAsync(ISessionMoveArbiter, string?, string?, CancellationToken)"/>).
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the other moves of this machine.</param>
    /// <returns>What each move is now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<IReadOnlyList<SessionMoveStepResult>> RecoverExportsAsync(CancellationToken cancellationToken)
    {
        var results = new List<SessionMoveStepResult>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in Store.ListUnfinished())
            {
                if (record.Role != SessionMoveRole.Source || record.State != SessionMoveState.Exporting) continue;
                results.Add(Recover(record, () => AbandonExport(record, "The source stopped before it offered the session.")));
            }

            foreach (var (sessionId, moveId) in _runtime.Relinquishments.ListFrozen())
            {
                // A freeze is recorded after its move (as exporting), and a move is offered once recorded as offered:
                // with no record, no machine was offered the session.
                if (!IsValidMoveId(moveId) || Store.Read(moveId, SessionMoveRole.Source) is not null || Store.Read(moveId, SessionMoveRole.Target) is not null) continue;
                var orphan = new SessionMoveTransaction
                {
                    MoveId = moveId, Role = SessionMoveRole.Source, State = SessionMoveState.Exporting, SessionId = sessionId, FromMachine = MachineName,
                    BundleFolder = GetBundleFolder(moveId, SessionMoveRole.Source),
                };
                results.Add(Recover(orphan, () => AbandonExport(orphan, "The session was frozen by a move this machine has no record of.")));
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;

        static SessionMoveStepResult Recover(SessionMoveTransaction record, Func<SessionMoveStepResult> abandon)
        {
            try
            {
                return abandon();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(record.State, record, [$"The move {record.MoveId} of the session {record.SessionId} could not be undone: {exception.Message}"]);
            }
        }

        static bool IsValidMoveId(string moveId)
        {
            try
            {
                SessionMoveTransactionStore.ValidateMoveId(moveId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    // An export that did not happen leaves no record; one that cannot be removed now is undone when the source starts.
    private void TryAbandonExport(SessionMoveTransaction record)
    {
        try
        {
            AbandonExport(record, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // RecoverExportsAsync ends it.
        }
    }

    // A source's move that was never offered: its bundle goes, the session is written here again, and the move is
    // recorded as aborted for that reason, or leaves no record without one (a refusal of the export).
    private SessionMoveStepResult AbandonExport(SessionMoveTransaction record, string? reason)
    {
        if (record.BundleFolder is { } bundle && Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        _runtime.UnfreezeMovedSession(record.SessionId, record.MoveId);
        if (reason is null)
        {
            Store.Delete(record.MoveId, SessionMoveRole.Source);
            return new(SessionMoveState.Aborted, null, []);
        }

        var aborted = record with { State = SessionMoveState.Aborted, Reason = reason };
        Store.Save(aborted);
        return new(SessionMoveState.Aborted, aborted, []);
    }

    /// <summary>Gets the folder of the bundle this machine makes or receives for a move.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="role">The side (<see cref="SessionMoveRole"/>).</param>
    /// <returns>The folder.</returns>
    /// <exception cref="ArgumentException">The move id is not valid.</exception>
    public string GetBundleFolder(string moveId, string role)
    {
        SessionMoveTransactionStore.ValidateMoveId(moveId);
        return Path.Combine(Store.BundlesDirectory, moveId, role);
    }

    /// <summary>
    /// The source offers a session: it is frozen, its bundle made in <see cref="GetBundleFolder"/>, and the move recorded
    /// as offered.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move.</param>
    /// <param name="toMachine">The machine the session goes to.</param>
    /// <param name="epoch">The ownership epoch of the session on this machine.</param>
    /// <param name="maximumPayloadBytes">The largest git payload the bundle may carry.</param>
    /// <returns><c>offered</c>, or why the session was not offered.</returns>
    /// <exception cref="ArgumentException">An argument is not valid.</exception>
    public Task<SessionMoveStepResult> OfferAsync(string sessionId, string moveId, string? toMachine, long epoch = 0,
        long maximumPayloadBytes = GitSessionPayload.DefaultMaximumBytes)
        => OfferAsync(sessionId, moveId, toMachine, null, epoch, maximumPayloadBytes);

    /// <summary>
    /// The source offers a session to a target it reaches by a route: it is frozen, its bundle made in
    /// <see cref="GetBundleFolder"/>, and the move recorded as offered with that route, the only one it goes on with.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move.</param>
    /// <param name="toMachine">The name of the machine the session goes to, as the session says it moved there.</param>
    /// <param name="route">The devices the move is bound to; null records none (a move driven on this machine alone).</param>
    /// <param name="epoch">The ownership epoch of the session on this machine.</param>
    /// <param name="maximumPayloadBytes">The largest git payload the bundle may carry.</param>
    /// <returns><c>offered</c>, or why the session was not offered.</returns>
    /// <exception cref="ArgumentException">An argument is not valid.</exception>
    public async Task<SessionMoveStepResult> OfferAsync(string sessionId, string moveId, string? toMachine, SessionMoveRoute? route, long epoch,
        long maximumPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var bundle = GetBundleFolder(moveId, SessionMoveRole.Source);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Store.Read(moveId, SessionMoveRole.Source) is { } existing)
            {
                if (existing.State != SessionMoveState.Exporting) return new(existing.State, existing, ["The move was already offered."]);
                // An export a crash interrupted: nothing was offered, it begins again.
                TryAbandonExport(existing);
            }

            // Recorded before the session is frozen: a crash from here on leaves a record the source recovers alone
            // (RecoverExportsAsync), since no machine was offered anything before the move is recorded as offered.
            var intent = new SessionMoveTransaction
            {
                MoveId = moveId, Role = SessionMoveRole.Source, State = SessionMoveState.Exporting, SessionId = sessionId,
                FromMachine = MachineName, ToMachine = toMachine, ToDeviceId = route?.DeviceId, Via = route?.Via, Epoch = epoch + 1, BundleFolder = bundle,
            };
            Store.Save(intent);
            SessionBundleExportResult export;
            try
            {
                export = await _commands.ExportSessionBundleAsync(sessionId, new SessionBundleExportRequest(moveId, bundle, MachineName, toMachine)
                {
                    Epoch = epoch,
                    MaximumPayloadBytes = maximumPayloadBytes,
                }).ConfigureAwait(false);
            }
            catch
            {
                TryAbandonExport(intent);
                throw;
            }

            if (export.Status != "ok")
            {
                // The export undid itself: the move leaves no record, as if it was never asked.
                TryAbandonExport(intent);
                return new(export.Status, null, export.Warnings);
            }

            if (AfterExportForTesting is { } hook) await hook(moveId).ConfigureAwait(false);
            var record = intent with { State = SessionMoveState.Offered };
            Store.Save(record);
            return new(SessionMoveState.Offered, record, export.Warnings);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The target prepares a received bundle: it is checked, its project found, its worktree made, and the move recorded
    /// as prepared. Nothing of the session runs here before the commit.
    /// </summary>
    /// <param name="bundleFolder">The folder the bundle was received in.</param>
    /// <param name="cancellationToken">Stops the checks and git.</param>
    /// <returns><c>prepared</c>, or why the target refuses the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="bundleFolder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<SessionMoveStepResult> PrepareAsync(string bundleFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preparation = await _runtime.PrepareMovedSessionAsync(bundleFolder, cancellationToken).ConfigureAwait(false);
            if (preparation.Prepared is not { } prepared) return new(preparation.Status, null, preparation.Warnings);
            var manifest = prepared.Manifest;
            if (Store.Read(manifest.MoveId, SessionMoveRole.Target) is { } existing)
            {
                await _runtime.DiscardPreparedMoveAsync(prepared).ConfigureAwait(false);
                return new(existing.State, existing, ["The move was already prepared."]);
            }

            var record = new SessionMoveTransaction
            {
                MoveId = manifest.MoveId, Role = SessionMoveRole.Target, State = SessionMoveState.Prepared, SessionId = manifest.SessionId,
                FromMachine = manifest.FromMachine, ToMachine = MachineName, Epoch = manifest.Epoch + 1, BundleFolder = prepared.BundleFolder,
                ProjectId = prepared.ProjectId, ProjectPath = prepared.ProjectPath, WorktreeRoot = prepared.WorktreeRoot, WorkFolder = prepared.WorkFolder, Branch = prepared.Branch,
                CreatedBranch = prepared.CreatedBranch,
            };
            Store.Save(record);
            return new(SessionMoveState.Prepared, record, preparation.Warnings);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The target asks the arbiter to commit a prepared move, then installs the session: it runs here from then on. A
    /// move the arbiter aborted is undone instead.
    /// </summary>
    /// <param name="moveId">The move.</param>
    /// <param name="arbiter">The arbiter: in paired mode, this machine (<see cref="LocalSessionMoveArbiter"/>).</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns><c>finished</c> once installed; <c>aborted</c>; or <c>missing</c> for a move this machine did not prepare.</returns>
    /// <exception cref="IOException">The arbiter cannot be reached, or a file cannot be written: the move stays prepared, or committed.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<SessionMoveStepResult> CommitAsync(string moveId, ISessionMoveArbiter arbiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Store.Read(moveId, SessionMoveRole.Target) is not { } record) return new("missing", null, []);
            if (record.State != SessionMoveState.Prepared) return await AdvanceTargetAsync(record, null).ConfigureAwait(false);
            var decision = await arbiter.CommitAsync(moveId, record.Epoch, cancellationToken).ConfigureAwait(false);
            return await AdvanceTargetAsync(record, decision).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The source learns the outcome of its move and ends it: it relinquishes the session, or unfreezes it.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="arbiter">The arbiter.</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns><c>finished</c>, <c>aborted</c>, <c>offered</c> while the arbiter cannot be reached, or <c>missing</c>.</returns>
    /// <exception cref="IOException">A record cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<SessionMoveStepResult> CompleteSourceAsync(string moveId, ISessionMoveArbiter arbiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Store.Read(moveId, SessionMoveRole.Source) is not { } record) return new("missing", null, []);
            return await AdvanceSourceAsync(record, arbiter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the moves a crash or a lost connection left unfinished, on both sides, by the decision of the arbiter: a
    /// committed move goes on (the target installs, the source relinquishes); one never committed is undone; one whose
    /// arbiter cannot be reached waits.
    /// </summary>
    /// <param name="arbiter">The arbiter of the moves.</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns>What each move is now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<IReadOnlyList<SessionMoveStepResult>> RecoverAsync(ISessionMoveArbiter arbiter, CancellationToken cancellationToken)
        => RecoverAsync(arbiter, null, null, cancellationToken);

    /// <summary>
    /// Ends the unfinished moves of one side, or of one side and one other machine, by the decision of their arbiter:
    /// the moves of a target in paired mode with its own records, those of a source with the arbiter of the machine it
    /// gave the session to.
    /// </summary>
    /// <param name="arbiter">The arbiter of those moves.</param>
    /// <param name="role">The side (<see cref="SessionMoveRole"/>); null for both.</param>
    /// <param name="otherMachine">
    /// The machine at the other end, as the records name it (<see cref="SessionMoveTransaction.ToMachine"/> of a source,
    /// <see cref="SessionMoveTransaction.FromMachine"/> of a target); null for any.
    /// </param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns>What each move is now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<IReadOnlyList<SessionMoveStepResult>> RecoverAsync(ISessionMoveArbiter arbiter, string? role, string? otherMachine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        var results = new List<SessionMoveStepResult>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in Store.ListUnfinished())
            {
                if (role is not null && record.Role != role) continue;
                if (otherMachine is not null
                    && !string.Equals(record.Role == SessionMoveRole.Source ? record.ToMachine : record.FromMachine, otherMachine, StringComparison.Ordinal))
                    continue;
                try
                {
                    if (record.Role == SessionMoveRole.Source && record.State == SessionMoveState.Exporting)
                    {
                        // Never offered: no arbiter has anything to say of it.
                        results.Add(AbandonExport(record, "The source stopped before it offered the session."));
                    }
                    else if (record.Role == SessionMoveRole.Source)
                    {
                        results.Add(await AdvanceSourceAsync(record, arbiter, cancellationToken).ConfigureAwait(false));
                    }
                    else if (record.State == SessionMoveState.Committed)
                    {
                        results.Add(await AdvanceTargetAsync(record, null).ConfigureAwait(false));
                    }
                    else
                    {
                        var decision = await arbiter.ResolveAsync(record.MoveId, cancellationToken).ConfigureAwait(false);
                        results.Add(decision is null ? new(record.State, record, ["The arbiter of the move cannot be reached: the move waits."])
                            : await AdvanceTargetAsync(record, decision).ConfigureAwait(false));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    results.Add(new(record.State, record, [$"The move {record.MoveId} of the session {record.SessionId} could not go on: {exception.Message}"]));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    /// <summary>
    /// The target of one move ends it by the decision of the arbiter, without asking for a commit: it installs a move the
    /// arbiter committed, and removes what it prepared for one the arbiter aborted (or aborts now, since it never
    /// committed it). Used when the source asked the arbiter for the outcome, after a lost connection.
    /// </summary>
    /// <param name="moveId">The move.</param>
    /// <param name="arbiter">The arbiter.</param>
    /// <param name="cancellationToken">Stops waiting for the arbiter.</param>
    /// <returns>What the move is now on this machine, or <c>missing</c> when this machine has no record of it.</returns>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<SessionMoveStepResult> SettleTargetAsync(string moveId, ISessionMoveArbiter arbiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Store.Read(moveId, SessionMoveRole.Target) is not { } record) return new("missing", null, []);
            if (SessionMoveState.IsFinal(record.State) || record.State == SessionMoveState.Committed)
                return await AdvanceTargetAsync(record, null).ConfigureAwait(false);
            var decision = await arbiter.ResolveAsync(moveId, cancellationToken).ConfigureAwait(false);
            return decision is null ? new(record.State, record, ["The arbiter of the move cannot be reached: the move waits."])
                : await AdvanceTargetAsync(record, decision).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The source of a move, once the arbiter is asked: it relinquishes a committed move, unfreezes an aborted one.
    private async Task<SessionMoveStepResult> AdvanceSourceAsync(SessionMoveTransaction record, ISessionMoveArbiter arbiter, CancellationToken cancellationToken)
    {
        if (SessionMoveState.IsFinal(record.State)) return new(record.State, record, []);
        var decision = await arbiter.ResolveAsync(record.MoveId, cancellationToken).ConfigureAwait(false);
        if (decision is null) return new(record.State, record, ["The arbiter of the move cannot be reached: the session stays frozen."]);
        if (decision.Outcome == SessionMoveState.Committed)
        {
            _runtime.RelinquishMovedSession(record.SessionId, record.MoveId, record.ToMachine, decision.Epoch);
            var finished = record with { State = SessionMoveState.Finished, Epoch = decision.Epoch };
            Store.Save(finished);
            return new(SessionMoveState.Finished, finished, []);
        }

        _runtime.UnfreezeMovedSession(record.SessionId, record.MoveId);
        var aborted = record with { State = SessionMoveState.Aborted, Reason = "The arbiter did not commit the move." };
        Store.Save(aborted);
        return new(SessionMoveState.Aborted, aborted, []);
    }

    // The target of a move, with the decision of the arbiter (null once it recorded the commit): it installs a committed
    // move, removes what it prepared for an aborted one.
    private async Task<SessionMoveStepResult> AdvanceTargetAsync(SessionMoveTransaction record, SessionMoveDecision? decision)
    {
        if (SessionMoveState.IsFinal(record.State)) return new(record.State, record, []);
        var prepared = await ReadPreparedAsync(record).ConfigureAwait(false);
        if (decision is { Outcome: SessionMoveState.Aborted })
        {
            if (prepared is not null) await _runtime.DiscardPreparedMoveAsync(prepared).ConfigureAwait(false);
            var aborted = record with { State = SessionMoveState.Aborted, Reason = "The arbiter did not commit the move." };
            Store.Save(aborted);
            return new(SessionMoveState.Aborted, aborted, []);
        }

        var committed = record;
        if (decision is not null)
        {
            committed = record with { State = SessionMoveState.Committed, Epoch = decision.Epoch };
            Store.Save(committed);
        }

        if (prepared is null) throw new InvalidOperationException($"The bundle of the move {record.MoveId} is gone: the session cannot be installed.");
        var warnings = await _runtime.InstallMovedSessionAsync(prepared, MachineName, committed.Epoch, CancellationToken.None).ConfigureAwait(false);
        var finished = committed with { State = SessionMoveState.Finished };
        Store.Save(finished);
        return new(SessionMoveState.Finished, finished, warnings);
    }

    // What the target prepared, read again from its record and its bundle.
    private static async Task<PreparedSessionMove?> ReadPreparedAsync(SessionMoveTransaction record)
    {
        if (record.BundleFolder is not { } bundle || record.ProjectId is null || record.ProjectPath is null || record.WorktreeRoot is null || record.WorkFolder is null)
            return null;
        if (await SessionBundle.ReadManifestAsync(bundle, CancellationToken.None).ConfigureAwait(false) is not { } manifest) return null;
        return new PreparedSessionMove(manifest, bundle, record.ProjectId, record.ProjectPath, record.WorktreeRoot, record.WorkFolder, record.Branch, record.CreatedBranch);
    }
}
