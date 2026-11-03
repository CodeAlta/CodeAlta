using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Moves;

/// <summary>
/// The target side of the moves of a host, arbiter of them in paired mode: it takes offers, receives the archive of a
/// bundle in chunks at the length it holds (so a transfer resumes after a lost connection), verifies it by its hash,
/// prepares the session (<see cref="SessionMoveService.PrepareAsync"/>), commits it on its own records
/// (<see cref="LocalSessionMoveArbiter"/>) and answers the outcome to a source that asks after a lost connection.
/// </summary>
/// <remarks>
/// What a target received for a move is kept in <c>&lt;bundles&gt;/&lt;move&gt;/</c> of the move records: the offer it
/// accepted (with the device that made it, the only one that may go on with that move) and the archive. They go once
/// the source says it relinquished the session.
/// </remarks>
public sealed class FleetMoveReceiver
{
    private const string OfferFile = "incoming.json";
    private const string ArchiveFile = "incoming.zip";
    private readonly CodeAltaHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the target side of the moves of a host.</summary>
    /// <param name="host">The host.</param>
    /// <param name="moves">The moves of the host.</param>
    /// <param name="cloneRoot">Where a repository the target lacks is cloned (<c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>); null clones nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="moves"/> is null.</exception>
    public FleetMoveReceiver(CodeAltaHost host, SessionMoveService moves, string? cloneRoot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(moves);
        _host = host;
        Moves = moves;
        CloneRoot = string.IsNullOrWhiteSpace(cloneRoot) ? null : Path.GetFullPath(cloneRoot);
        Arbiter = new LocalSessionMoveArbiter(moves.Store);
    }

    /// <summary>Gets the moves of the host.</summary>
    public SessionMoveService Moves { get; }

    /// <summary>Gets the arbiter of the moves this machine takes.</summary>
    public LocalSessionMoveArbiter Arbiter { get; }

    /// <summary>Gets where a repository the target lacks is cloned; null when it clones nothing.</summary>
    public string? CloneRoot { get; }

    /// <summary>Gets the default root of the clones of a machine: <c>~/code</c>.</summary>
    public static string DefaultCloneRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code");

    /// <summary>The target as a source on this machine, or the methods served for a caller, talk to it.</summary>
    /// <param name="callerId">The device that drives the moves: only it may go on with a move it offered.</param>
    /// <returns>The target.</returns>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    public IFleetMoveTarget For(string callerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
        return new Bound(this, callerId);
    }

    /// <summary>Ends the moves this machine took that a crash or a lost connection left unfinished, by its own decisions.</summary>
    /// <param name="cancellationToken">Stops the recovery.</param>
    /// <returns>What each move is now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<IReadOnlyList<SessionMoveStepResult>> RecoverAsync(CancellationToken cancellationToken)
        => Moves.RecoverAsync(Arbiter, SessionMoveRole.Target, null, cancellationToken);

    /// <summary>Takes an offer, or says whether it would.</summary>
    /// <param name="offer">The offer.</param>
    /// <param name="callerId">The device that makes it.</param>
    /// <param name="cancellationToken">Stops the checks and a clone.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="FleetException">The offer is not valid, or another device offered that move.</exception>
    public async Task<FleetMoveOfferResult> OfferAsync(FleetMoveOffer offer, string callerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Validate(offer);
        if (Moves.Store.Read(offer.MoveId, SessionMoveRole.Target) is { } record)
            return new(record.State, null, null, ReadIncoming(offer.MoveId)?.Offer.ArchiveLength ?? 0, record.ProjectId, null, record.ProjectPath, false, []);
        if (Moves.Store.ReadDecision(offer.MoveId) is { Outcome: SessionMoveState.Aborted })
            return Refused("aborted", "The move was undone on this machine.");

        if (await _host.RuntimeService.TryGetRecoverableSessionAsync(offer.SessionId, cancellationToken).ConfigureAwait(false) is not null
            && _host.RuntimeService.Relinquishments.Read(offer.SessionId) is not { IsRelinquished: true })
            return Refused("session_exists", $"The session {offer.SessionId} is already on this machine.");
        if (offer.ArchiveLength > FleetMoveLimits.MaximumArchiveBytes)
            return Refused("too_large", $"The session takes {offer.ArchiveLength} bytes, more than the {FleetMoveLimits.MaximumArchiveBytes} a move carries.");

        var warnings = new List<string>();
        if (offer.ProviderKey is { } providerKey
            && !_host.ModelProviderRegistry.ListProviders().Any(provider => provider.IsEnabled && string.Equals(provider.ProviderId.Value, providerKey, StringComparison.Ordinal)))
            warnings.Add($"The provider {providerKey} of the session is not configured on this machine: choose another one before its next prompt.");

        var project = await _host.RuntimeService.FindProjectOfRepositoryAsync(offer.RepositoryKey, offer.RelativeFolder, cancellationToken).ConfigureAwait(false);
        var clonePath = project is null ? ClonePath(offer) : null;
        if (project is null && (offer.Probe || !offer.CloneRepository || clonePath is null))
        {
            return new("refused", "project_missing", $"No project of this machine is in the repository {offer.RepositoryKey}"
                + (offer.RelativeFolder is "." ? "." : $" at {offer.RelativeFolder}."), 0, null, null, null, clonePath is not null, warnings);
        }

        if (offer.Probe) return new("accepted", null, null, 0, project!.Id, Name(project), project.ProjectPath, false, warnings);

        if (project is null)
        {
            var cloned = await CloneAsync(offer, clonePath!, cancellationToken).ConfigureAwait(false);
            if (cloned.Project is null) return Refused("clone_failed", cloned.Message ?? $"The repository {offer.RepositoryKey} could not be cloned.");
            project = cloned.Project;
            warnings.Add($"The repository was cloned into {project.ProjectPath}.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = ReadIncoming(offer.MoveId);
            if (existing is not null && !string.Equals(existing.CallerId, callerId, StringComparison.Ordinal))
                throw new FleetException(FleetErrorCodes.Forbidden, null, "Another device offered this move.");
            var folder = Directory.CreateDirectory(IncomingFolder(offer.MoveId)).FullName;
            var archive = Path.Combine(folder, ArchiveFile);
            // Another archive than the one offered before (the source packed it again): what was received does not count.
            if (existing is null || existing.Offer.ArchiveLength != offer.ArchiveLength
                || !string.Equals(existing.Offer.ArchiveSha256, offer.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(archive)) File.Delete(archive);
                WriteIncoming(offer.MoveId, new FleetMoveIncoming(offer with { Via = null, CloneRepository = false }, callerId, DateTimeOffset.UtcNow));
            }

            var received = File.Exists(archive) ? new FileInfo(archive).Length : 0;
            return new("accepted", null, null, received, project.Id, Name(project), project.ProjectPath, false, warnings);
        }
        finally
        {
            _gate.Release();
        }

        static FleetMoveOfferResult Refused(string code, string message) => new("refused", code, message, 0, null, null, null, false, []);
    }

    /// <summary>Writes a chunk of the archive at the length held.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="callerId">The device that sends it.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The length held now.</returns>
    /// <exception cref="FleetException">No such offer, another device's, a chunk past the length held or past the archive.</exception>
    public async Task<FleetMoveChunkResult> TransferAsync(FleetMoveChunk chunk, string callerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var incoming = RequireIncoming(chunk.MoveId, callerId);
        var archive = Path.Combine(IncomingFolder(chunk.MoveId), ArchiveFile);
        if (Moves.Store.Read(chunk.MoveId, SessionMoveRole.Target) is not null) return new(incoming.Offer.ArchiveLength);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(chunk.Data);
        }
        catch (FormatException)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The data of a chunk is base64.");
        }

        if (bytes.Length is 0 or > FleetMoveLimits.ChunkBytes)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A chunk has 1 to {FleetMoveLimits.ChunkBytes} bytes.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(archive, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            // A chunk sent again after a lost reply lands where it was: the archive is cut back to it.
            if (chunk.Offset < 0 || chunk.Offset > stream.Length)
                throw new FleetException(FleetErrorCodes.InvalidParams, null, $"The archive holds {stream.Length} bytes: a chunk goes there.");
            if (chunk.Offset + bytes.Length > incoming.Offer.ArchiveLength)
                throw new FleetException(FleetErrorCodes.InvalidParams, null, "The chunk goes past the length of the archive that was offered.");
            stream.SetLength(chunk.Offset);
            stream.Position = chunk.Offset;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new(stream.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Verifies the archive by its length and hash, unpacks it and prepares the session.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="callerId">The device that asks.</param>
    /// <param name="cancellationToken">Stops the checks and git.</param>
    /// <returns><c>prepared</c> (or the state of a move prepared already), or why the session cannot come.</returns>
    /// <exception cref="FleetException">No such offer, or another device's.</exception>
    public async Task<FleetMoveStepResult> PrepareAsync(string moveId, string callerId, CancellationToken cancellationToken)
    {
        if (Moves.Store.Read(moveId, SessionMoveRole.Target) is { } record) return Step(record.State, record, []);
        var incoming = RequireIncoming(moveId, callerId);
        var archive = Path.Combine(IncomingFolder(moveId), ArchiveFile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string bundle;
        try
        {
            if (!File.Exists(archive) || new FileInfo(archive).Length != incoming.Offer.ArchiveLength)
                return new("incomplete", 0, null, null, ["The archive of the session was not received whole."]);
            string hash;
            await using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, incoming.Offer.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                // It is sent again from its start.
                File.Delete(archive);
                return new("bundle_invalid", 0, null, null, ["The archive of the session is not the one that was offered: it is sent again."]);
            }

            bundle = Moves.GetBundleFolder(moveId, SessionMoveRole.Target);
            if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
            await using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    await SessionBundle.UnpackAsync(stream, bundle, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    return new("bundle_invalid", 0, null, null, [exception.Message]);
                }
            }

            var manifest = await SessionBundle.ReadManifestAsync(bundle, cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.MoveId != moveId || manifest.SessionId != incoming.Offer.SessionId)
                return new("bundle_invalid", 0, null, null, ["The bundle is not the one of this move."]);
        }
        finally
        {
            _gate.Release();
        }

        var prepared = await Moves.PrepareAsync(bundle, cancellationToken).ConfigureAwait(false);
        return Step(prepared.Status, prepared.Transaction, prepared.Warnings);
    }

    /// <summary>Commits a prepared move, as its arbiter, and installs the session.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="callerId">The device that asks.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns><c>finished</c>, <c>aborted</c> or <c>missing</c>.</returns>
    /// <exception cref="FleetException">Another device offered that move.</exception>
    public async Task<FleetMoveStepResult> CommitAsync(string moveId, string callerId, CancellationToken cancellationToken)
    {
        RequireIncoming(moveId, callerId);
        var committed = await Moves.CommitAsync(moveId, Arbiter, cancellationToken).ConfigureAwait(false);
        return Step(committed.Status, committed.Transaction, committed.Warnings);
    }

    /// <summary>Says the outcome of a move, deciding it first when asked to.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="resolve">Decides a move not decided yet: it is aborted, and what was prepared goes.</param>
    /// <param name="cancellationToken">Stops the recovery of the move.</param>
    /// <returns>The outcome and the records of this machine.</returns>
    public async Task<FleetMoveStatus> StatusAsync(string moveId, bool resolve, CancellationToken cancellationToken)
    {
        var decision = resolve ? await Arbiter.ResolveAsync(moveId, cancellationToken).ConfigureAwait(false) : Moves.Store.ReadDecision(moveId);
        // The target ends its side by the same decision: it installs a committed move, removes what an aborted one prepared.
        if (resolve) await Moves.SettleTargetAsync(moveId, Arbiter, cancellationToken).ConfigureAwait(false);
        return new FleetMoveStatus(moveId, decision?.Outcome, decision?.Epoch ?? 0, Records(Moves.Store, moveId));
    }

    /// <summary>The source relinquished the session: the copies of the bundle go.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="callerId">The device that says it.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>The state of the move here.</returns>
    public Task<FleetMoveStepResult> CompleteAsync(string moveId, string callerId, CancellationToken cancellationToken)
    {
        var record = Moves.Store.Read(moveId, SessionMoveRole.Target);
        if (ReadIncoming(moveId) is { } incoming && !string.Equals(incoming.CallerId, callerId, StringComparison.Ordinal))
            throw new FleetException(FleetErrorCodes.Forbidden, null, "Another device offered this move.");
        if (record is not null && SessionMoveState.IsFinal(record.State)) DeleteQuietly(Path.Combine(Moves.Store.BundlesDirectory, moveId));
        return Task.FromResult(record is null ? new FleetMoveStepResult("missing", 0, null, null, []) : Step(record.State, record, []));
    }

    /// <summary>The records of one move on a machine, as the node API describes them.</summary>
    /// <param name="store">The records of the machine.</param>
    /// <param name="moveId">The move.</param>
    /// <returns>Its source and target records, those that exist.</returns>
    public static IReadOnlyList<FleetMoveRecord> Records(SessionMoveTransactionStore store, string moveId)
    {
        ArgumentNullException.ThrowIfNull(store);
        return [.. new[] { store.Read(moveId, SessionMoveRole.Source), store.Read(moveId, SessionMoveRole.Target) }.OfType<SessionMoveTransaction>().Select(Describe)];
    }

    /// <summary>Describes a record as the node API does.</summary>
    /// <param name="record">The record.</param>
    /// <returns>Its description.</returns>
    public static FleetMoveRecord Describe(SessionMoveTransaction record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new FleetMoveRecord(record.MoveId, record.Role, record.State, record.SessionId, record.FromMachine, record.ToMachine, record.Epoch, record.WorkFolder,
            record.Branch, record.Reason, record.UpdatedAt);
    }

    internal static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy left behind holds nothing a later move reads: the next cleanup takes it.
        }
    }

    private static FleetMoveStepResult Step(string status, SessionMoveTransaction? record, IReadOnlyList<string> warnings)
        => new(status, record?.Epoch ?? 0, record?.WorkFolder, record?.Branch, warnings);

    private static string Name(ProjectDescriptor project) => string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName;

    private static void Validate(FleetMoveOffer offer)
    {
        try
        {
            SessionMoveTransactionStore.ValidateMoveId(offer.MoveId);
        }
        catch (ArgumentException exception)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message);
        }

        if (!SessionLeaseStore.IsValidSessionId(offer.SessionId)) throw new FleetException(FleetErrorCodes.InvalidParams, null, $"'{offer.SessionId}' is not a session id.");
        if (string.IsNullOrWhiteSpace(offer.RepositoryKey) || offer.RepositoryKey.Length > 512 || string.IsNullOrWhiteSpace(offer.RelativeFolder) || offer.RelativeFolder.Length > 1024)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "An offer names the repository of the session and its folder there.");
        if (!offer.Probe && (offer.ArchiveLength <= 0 || offer.ArchiveSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "An offer names the length and the SHA-256 of the archive.");
    }

    private string IncomingFolder(string moveId)
    {
        SessionMoveTransactionStore.ValidateMoveId(moveId);
        return Path.Combine(Moves.Store.BundlesDirectory, moveId);
    }

    private FleetMoveIncoming RequireIncoming(string moveId, string callerId)
    {
        try
        {
            SessionMoveTransactionStore.ValidateMoveId(moveId);
        }
        catch (ArgumentException exception)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message);
        }

        var incoming = ReadIncoming(moveId) ?? throw new FleetException(FleetErrorCodes.InvalidRequest, null, $"No offer of the move {moveId} was taken.");
        if (!string.Equals(incoming.CallerId, callerId, StringComparison.Ordinal))
            throw new FleetException(FleetErrorCodes.Forbidden, null, "Another device offered this move.");
        return incoming;
    }

    private FleetMoveIncoming? ReadIncoming(string moveId)
    {
        var path = Path.Combine(IncomingFolder(moveId), OfferFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetMoveJsonContext.Default.FleetMoveIncoming) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private void WriteIncoming(string moveId, FleetMoveIncoming incoming)
    {
        var path = Path.Combine(IncomingFolder(moveId), OfferFile);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(incoming, FleetMoveJsonContext.Default.FleetMoveIncoming));
        File.Move(staging, path, overwrite: true);
    }

    // Where a repository the target lacks is cloned: <root>/<key>, when the key is host/owner/repo and the URL is one a
    // machine fetches from the network (never a path of the target, which a caller must not make it read).
    private string? ClonePath(FleetMoveOffer offer)
    {
        if (CloneRoot is null || offer.RemoteUrl is not { Length: > 0 and <= 2048 } url || !IsNetworkUrl(url)) return null;
        if (RepositoryKey.Normalize(url) != offer.RepositoryKey) return null;
        var segments = offer.RepositoryKey.Split('/');
        if (segments.Length < 2 || segments.Any(static segment => segment is "" or "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        var path = Path.GetFullPath(Path.Combine([CloneRoot, .. segments]));
        return GitWorktreeService.IsWithin(path, CloneRoot) ? path : null;
    }

    private static bool IsNetworkUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Scheme is "https" or "http" or "ssh" or "git" && !string.IsNullOrEmpty(uri.Host);
        // scp-like: user@host:owner/repo
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        return colon > 0 && at > 0 && at < colon && !url.Contains('\\', StringComparison.Ordinal);
    }

    private async Task<(ProjectDescriptor? Project, string? Message)> CloneAsync(FleetMoveOffer offer, string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
        {
            var parent = Directory.CreateDirectory(Path.GetDirectoryName(path)!).FullName;
            var clone = await GitWorktreeService.RunGitAsync(parent, ["clone", "--", offer.RemoteUrl!, path], GitWorktreeService.WriteTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!clone.Succeeded) return (null, (clone.Error.Length > 0 ? clone.Error : clone.Output).Trim());
        }

        if (await RepositoryKey.ReadAsync(path, cancellationToken).ConfigureAwait(false) != offer.RepositoryKey)
            return (null, $"{path} holds another repository.");
        var folder = offer.RelativeFolder is "." ? path : Path.GetFullPath(Path.Combine(path, offer.RelativeFolder));
        if (!GitWorktreeService.IsWithin(folder, path) || !Directory.Exists(folder)) return (null, $"The repository has no folder {offer.RelativeFolder}.");
        await _host.ProjectCatalog.UpsertFromPathAsync(folder, cancellationToken).ConfigureAwait(false);
        return (await _host.RuntimeService.FindProjectOfRepositoryAsync(offer.RepositoryKey, offer.RelativeFolder, cancellationToken).ConfigureAwait(false), null);
    }

    private sealed class Bound(FleetMoveReceiver receiver, string callerId) : IFleetMoveTarget
    {
        public Task<FleetMoveOfferResult> OfferAsync(FleetMoveOffer offer, CancellationToken cancellationToken) => receiver.OfferAsync(offer, callerId, cancellationToken);

        public Task<FleetMoveChunkResult> TransferAsync(FleetMoveChunk chunk, CancellationToken cancellationToken) => receiver.TransferAsync(chunk, callerId, cancellationToken);

        public Task<FleetMoveStepResult> PrepareAsync(FleetMoveRef move, CancellationToken cancellationToken) => receiver.PrepareAsync(move.MoveId, callerId, cancellationToken);

        public Task<FleetMoveStepResult> CommitAsync(FleetMoveRef move, CancellationToken cancellationToken) => receiver.CommitAsync(move.MoveId, callerId, cancellationToken);

        public Task<FleetMoveStatus> StatusAsync(FleetMoveStatusParams parameters, CancellationToken cancellationToken)
            => receiver.StatusAsync(parameters.MoveId, parameters.Resolve, cancellationToken);

        public Task<FleetMoveStepResult> CompleteAsync(FleetMoveRef move, CancellationToken cancellationToken) => receiver.CompleteAsync(move.MoveId, callerId, cancellationToken);
    }
}

/// <summary>An offer a target took: what it offered, and the device that made it.</summary>
/// <param name="Offer">The offer.</param>
/// <param name="CallerId">The device that may go on with the move.</param>
/// <param name="ReceivedAt">When it was taken.</param>
internal sealed record FleetMoveIncoming(FleetMoveOffer Offer, string CallerId, DateTimeOffset ReceivedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetMoveIncoming))]
internal sealed partial class FleetMoveJsonContext : JsonSerializerContext;
