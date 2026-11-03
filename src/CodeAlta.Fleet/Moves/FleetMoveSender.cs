using System.Collections.Concurrent;
using System.Security.Cryptography;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Moves;

/// <summary>
/// The source side of the moves of a host: it gives a session to a target (<see cref="IFleetMoveTarget"/>) as a
/// recoverable transaction. It asks the target first (a probe, before anything is frozen), then freezes the session and
/// makes its bundle (<see cref="SessionMoveService.OfferAsync(string, string, string?, SessionMoveRoute?, long, long)"/>), packs it into one archive, sends it in chunks from the
/// length the target holds, asks the target to prepare and to commit, and ends its side by the decision of the target,
/// the arbiter in paired mode (<see cref="SessionMoveService.CompleteSourceAsync"/>).
/// </summary>
/// <remarks>
/// A lost connection never makes the source take the session back: the move stays <c>offered</c>, the session frozen,
/// until the same move is driven again (<see cref="MoveAsync"/> with the same id, through the devices it recorded), which
/// goes on where it stopped and asks the arbiter for the outcome. Only a move the arbiter never committed is undone.
/// </remarks>
public sealed class FleetMoveSender
{
    private const string ArchiveFile = "source.zip";
    private readonly CodeAltaHost _host;
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);

    /// <summary>Creates the source side of the moves of a host.</summary>
    /// <param name="host">The host.</param>
    /// <param name="moves">The moves of the host.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetMoveSender(CodeAltaHost host, SessionMoveService moves)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(moves);
        _host = host;
        Moves = moves;
    }

    /// <summary>Gets the moves of the host.</summary>
    public SessionMoveService Moves { get; }

    /// <summary>Says what a move of a session would meet: what keeps it busy, what moves and what stays.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The preview.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<FleetMovePreview> PreviewAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = _host.RuntimeService;
        if (await runtime.TryGetRecoverableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } session)
            return Empty("session_missing", "The session is not on this machine.");
        if (session.Kind != SessionViewKind.ProjectSession) return Empty("not_movable", "Only a session of a project moves to another machine.");
        if (runtime.Relinquishments.Read(sessionId) is { } relinquished)
            return Empty(relinquished.IsRelinquished ? "relinquished" : "moving", relinquished.IsRelinquished ? "The session moved to another machine." : "The session is moving.");

        var jobs = runtime.Jobs.List(sessionId).Where(static job => job.State == SessionJobState.Running).Select(static job => job.Label).ToArray();
        string? busy = null;
        if (_host.Commands.Asks.ListWaitingSessions().Contains(sessionId, StringComparer.OrdinalIgnoreCase)) busy = "waiting";
        else if ((await _host.Commands.ReadSessionsAtWorkAsync([sessionId]).ConfigureAwait(false)).Contains(sessionId)) busy = "working";
        else if (jobs.Length > 0) busy = "background_running";

        var folder = await runtime.GetSessionWorktreeAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? session.WorkingDirectory;
        var git = await GitSessionPayload.PreviewAsync(folder, cancellationToken).ConfigureAwait(false);
        return new FleetMovePreview(git.Status == "ok" ? "ok" : git.Status, session.SessionId, session.Title, busy, git.RepositoryKey, git.RelativeFolder, git.Branch,
            git.RemoteUrl, git.UnpushedCommits, git.Dirty, git.Secrets, jobs, session.ResolvedProviderKey, git.Message);

        FleetMovePreview Empty(string status, string message)
            => new(status, sessionId, null, null, null, ".", null, null, 0, false, [], [], null, message);
    }

    /// <summary>
    /// Gives a session to a target, or goes on with a move that stopped (same id): one run of a move at a time, a second
    /// call waits for the first, then goes on from where it left the move.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move.</param>
    /// <param name="target">The target.</param>
    /// <param name="targetName">The name of the target, as the session will say it moved to.</param>
    /// <param name="route">
    /// The authenticated devices <paramref name="target"/> stands for: the target, or the relay and the device it forwards
    /// to. A new move records it; a move that goes on must name the one it recorded (<c>move_target_mismatch</c>
    /// otherwise), so that no other machine, of the same name or not, is ever asked to commit it.
    /// </param>
    /// <param name="clone">Asks the target to clone the repository when it has no project of it.</param>
    /// <param name="cancellationToken">Stops the move between its steps; what was done stays recoverable.</param>
    /// <returns>The outcome (<see cref="FleetMoveResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">An id is not valid.</exception>
    public async Task<FleetMoveResult> MoveAsync(string sessionId, string moveId, IFleetMoveTarget target, string? targetName, SessionMoveRoute route, bool clone,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.DeviceId, nameof(route));
        SessionMoveTransactionStore.ValidateMoveId(moveId);
        while (true)
        {
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_running.TryAdd(moveId, mine.Task))
            {
                try
                {
                    return await RunAsync(sessionId, moveId, target, targetName, route, clone, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _running.TryRemove(new KeyValuePair<string, Task>(moveId, mine.Task));
                    mine.TrySetResult();
                }
            }

            // Another run of the same move (a channel that was lost, for one): it ends first, then this one goes on from there.
            if (_running.TryGetValue(moveId, out var other)) await other.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends, by the decision of their target, the moves this machine gave through one route and that a lost connection
    /// left unfinished: those its records bind to that route's devices, never those of a machine of the same name. The
    /// moves stay as they are while the target cannot say.
    /// </summary>
    /// <param name="target">The target, reached by <paramref name="route"/>.</param>
    /// <param name="route">The authenticated devices <paramref name="target"/> stands for.</param>
    /// <param name="cancellationToken">Stops the recovery.</param>
    /// <returns>What each move is now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<IReadOnlyList<FleetMoveResult>> ResumeAsync(IFleetMoveTarget target, SessionMoveRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(route);
        var results = new List<FleetMoveResult>();
        foreach (var record in Moves.Store.ListUnfinished().Where(record => record.Role == SessionMoveRole.Source && record.State != SessionMoveState.Exporting
                     && route.Matches(record)))
            results.Add(await MoveAsync(record.SessionId, record.MoveId, target, record.ToMachine, route, false, cancellationToken).ConfigureAwait(false));
        return results;
    }

    private async Task<FleetMoveResult> RunAsync(string sessionId, string moveId, IFleetMoveTarget target, string? targetName, SessionMoveRoute route, bool clone,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        FleetMoveResult Refuse(string? code, string? message, IReadOnlyList<string>? said = null)
            => new("refused", code, message, moveId, sessionId, 0, null, said ?? []);
        var record = Moves.Store.Read(moveId, SessionMoveRole.Source);
        if (record is not null && !string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            return Refuse("move_exists", "That move id is used by another session.");
        if (record is not null && SessionMoveState.IsFinal(record.State)) return Done(record, null);
        // Offered to one device: only it, the arbiter in paired mode, says whether it committed, and none other is asked
        // to take the session. A record that names no device goes on with none.
        if (record is not null && record.State != SessionMoveState.Exporting && !route.Matches(record))
            return Refuse("move_target_mismatch", $"The move goes on only with the machine it was offered to{(record.ToMachine is { } name ? $" ({name})" : "")}.");

        // A move whose export a crash interrupted was offered to no one: it begins again (SessionMoveService.OfferAsync).
        if (record is null || record.State == SessionMoveState.Exporting)
        {
            // Asked before anything is frozen: a target that would refuse never leaves the session frozen here.
            var preview = await PreviewAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (preview.Status != "ok") return Refuse(preview.Status, preview.Message);
            if (preview.Busy is { } busy) return Refuse(busy, "The session is at work: a session moves while it is idle.");
            FleetMoveOfferResult probe;
            try
            {
                probe = await target.OfferAsync(Offer(preview, moveId, 0, "", probe: true, clone), cancellationToken).ConfigureAwait(false);
            }
            catch (FleetException exception)
            {
                return Refuse(exception.Error.Code, exception.Error.Message);
            }
            catch (Exception exception) when (IsUnreachable(exception))
            {
                return Refuse("unreachable", exception.Message);
            }

            if (probe.Status == "refused" && !(clone && probe.Code == "project_missing" && probe.CanClone))
                return Refuse(probe.Code, probe.Message, probe.Warnings);
            warnings.AddRange(probe.Warnings);

            var offered = await Moves.OfferAsync(sessionId, moveId, targetName, route, CurrentEpoch(sessionId), GitSessionPayload.DefaultMaximumBytes)
                .ConfigureAwait(false);
            warnings.AddRange(offered.Warnings);
            if (offered.Status != SessionMoveState.Offered || offered.Transaction is null)
                return Refuse(offered.Status, offered.Warnings.FirstOrDefault(), warnings);
            record = offered.Transaction;
        }

        if (record.BundleFolder is not { } bundle || await SessionBundle.ReadManifestAsync(bundle, cancellationToken).ConfigureAwait(false) is not { } manifest)
            return await AbandonAsync(record, target, "bundle_missing", "The bundle of the move is gone.", warnings, cancellationToken).ConfigureAwait(false);

        FleetMoveStepResult? installed = null;
        try
        {
            var (archive, length, hash) = await PackAsync(moveId, bundle, cancellationToken).ConfigureAwait(false);
            var offer = await target.OfferAsync(new FleetMoveOffer(moveId, sessionId, manifest.Title, manifest.FromMachine, manifest.Repository.Key,
                manifest.Repository.RelativeFolder, manifest.Repository.Branch, await RemoteUrlAsync(manifest, cancellationToken).ConfigureAwait(false),
                manifest.ProviderKey, length, hash, Probe: false, CloneRepository: clone), cancellationToken).ConfigureAwait(false);
            warnings.AddRange(offer.Warnings);
            if (offer.Status == "refused") return await AbandonAsync(record, target, offer.Code, offer.Message, warnings, cancellationToken).ConfigureAwait(false);
            if (offer.Status == "accepted")
            {
                await SendAsync(target, moveId, archive, offer.ReceivedBytes, length, cancellationToken).ConfigureAwait(false);
                var prepared = await target.PrepareAsync(new FleetMoveRef(moveId), cancellationToken).ConfigureAwait(false);
                warnings.AddRange(prepared.Warnings);
                if (prepared.Status != SessionMoveState.Prepared)
                    return await AbandonAsync(record, target, prepared.Status, prepared.Warnings.FirstOrDefault(), warnings, cancellationToken).ConfigureAwait(false);
            }

            if (offer.Status is "accepted" or SessionMoveState.Prepared or SessionMoveState.Committed)
            {
                installed = await target.CommitAsync(new FleetMoveRef(moveId), cancellationToken).ConfigureAwait(false);
                warnings.AddRange(installed.Warnings);
            }
        }
        catch (FleetException exception) when (!IsUnreachable(exception))
        {
            // The target refused a step: the arbiter says whether the move stands (it does not, unless it committed it).
            return await AbandonAsync(record, target, exception.Error.Code, exception.Error.Message, warnings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            return Result(SessionMoveState.Offered, "unreachable",
                $"The connection to the target was lost ({exception.Message}): the session stays frozen until the move goes on.", warnings, record);
        }

        return await CompleteAsync(record, target, installed, warnings, cancellationToken).ConfigureAwait(false);
    }

    // Ends the source's side by the decision of the arbiter, then tells the target, which drops its copies.
    private async Task<FleetMoveResult> CompleteAsync(SessionMoveTransaction record, IFleetMoveTarget target, FleetMoveStepResult? installed, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var completed = await Moves.CompleteSourceAsync(record.MoveId, new FleetMoveArbiterClient(target), cancellationToken).ConfigureAwait(false);
        warnings.AddRange(completed.Warnings);
        if (completed.Status == SessionMoveState.Offered)
            return Result(SessionMoveState.Offered, "unreachable", "The target cannot say whether it took the session: it stays frozen until the move goes on.", warnings, record);
        if (completed.Status == SessionMoveState.Finished)
        {
            try
            {
                var done = await target.CompleteAsync(new FleetMoveRef(record.MoveId), cancellationToken).ConfigureAwait(false);
                installed ??= done;
            }
            catch (Exception exception) when (exception is FleetException || IsUnreachable(exception))
            {
                // The target keeps its copy of the archive until the next completion: nothing depends on it.
            }

            FleetMoveReceiver.DeleteQuietly(Path.Combine(Moves.Store.BundlesDirectory, record.MoveId));
        }

        return Done(completed.Transaction ?? record, installed, warnings);
    }

    // A move the target refused after the session was frozen: the arbiter decides it was not committed, and the session
    // is written here again. A target that cannot say leaves it frozen.
    private async Task<FleetMoveResult> AbandonAsync(SessionMoveTransaction record, IFleetMoveTarget target, string? code, string? message, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var completed = await Moves.CompleteSourceAsync(record.MoveId, new FleetMoveArbiterClient(target), cancellationToken).ConfigureAwait(false);
        warnings.AddRange(completed.Warnings);
        if (completed.Status == SessionMoveState.Aborted)
        {
            FleetMoveReceiver.DeleteQuietly(Path.Combine(Moves.Store.BundlesDirectory, record.MoveId));
            return Result(SessionMoveState.Aborted, code, message, warnings, completed.Transaction);
        }

        // The arbiter committed it after all (a commit whose answer was lost), or cannot say.
        return completed.Status == SessionMoveState.Finished ? Done(completed.Transaction ?? record, null, warnings)
            : Result(SessionMoveState.Offered, code ?? "unreachable", message, warnings, record);
    }

    private FleetMoveResult Done(SessionMoveTransaction record, FleetMoveStepResult? installed, List<string>? warnings = null)
        => new(record.State, record.State == SessionMoveState.Aborted ? "aborted" : null, record.Reason, record.MoveId, record.SessionId, record.Epoch,
            installed?.WorkFolder, warnings ?? []);

    private static FleetMoveResult Result(string status, string? code, string? message, IReadOnlyList<string>? warnings = null, SessionMoveTransaction? record = null)
        => new(status, code, message, record?.MoveId ?? "", record?.SessionId ?? "", record?.Epoch ?? 0, null, warnings ?? []);

    // The epoch the session has here: the one the last move that brought it gave it, else 0.
    private long CurrentEpoch(string sessionId)
        => Moves.Store.List().Where(record => record.Role == SessionMoveRole.Target && record.State == SessionMoveState.Finished
                && string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Select(static record => record.Epoch).DefaultIfEmpty(0).Max();

    private static FleetMoveOffer Offer(FleetMovePreview preview, string moveId, long length, string hash, bool probe, bool clone)
        => new(moveId, preview.SessionId, preview.Title, null, preview.RepositoryKey ?? "", preview.RelativeFolder, preview.Branch, preview.RemoteUrl, preview.ProviderKey,
            length, hash, probe, clone);

    private static async Task<string?> RemoteUrlAsync(SessionMoveManifest manifest, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(manifest.WorkingFolder)) return null;
        return (await GitSessionPayload.PreviewAsync(manifest.WorkingFolder, cancellationToken).ConfigureAwait(false)).RemoteUrl;
    }

    // The archive of the bundle, packed once: a move that goes on sends the same bytes, with the same hash.
    private async Task<(string Path, long Length, string Sha256)> PackAsync(string moveId, string bundle, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Moves.Store.BundlesDirectory, moveId, ArchiveFile);
        if (!File.Exists(path))
        {
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await SessionBundle.PackAsync(bundle, stream, cancellationToken).ConfigureAwait(false);
                File.Move(staging, path);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken).ConfigureAwait(false));
        return (path, read.Length, hash);
    }

    private static async Task SendAsync(IFleetMoveTarget target, string moveId, string archive, long from, long length, CancellationToken cancellationToken)
    {
        if (from < 0 || from > length) from = 0;
        var buffer = new byte[FleetMoveLimits.ChunkBytes];
        await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var offset = from;
        while (offset < length)
        {
            stream.Position = offset;
            var read = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(buffer.Length, length - offset), throwOnEndOfStream: true, cancellationToken).ConfigureAwait(false);
            var answer = await target.TransferAsync(new FleetMoveChunk(moveId, offset, Convert.ToBase64String(buffer, 0, read)), cancellationToken).ConfigureAwait(false);
            if (answer.ReceivedBytes <= offset) throw new FleetException(FleetErrorCodes.ProtocolError, null, "The target did not take the chunk.");
            offset = answer.ReceivedBytes;
        }
    }

    // A failure that leaves the outcome unknown: the channel, not the target, failed.
    private static bool IsUnreachable(Exception exception) => exception is FleetConnectionClosedException or TimeoutException or IOException or ObjectDisposedException
        || exception is FleetException { Error.Code: FleetErrorCodes.ConnectionClosed or FleetErrorCodes.ServerBusy } || exception is OperationCanceledException;
}
