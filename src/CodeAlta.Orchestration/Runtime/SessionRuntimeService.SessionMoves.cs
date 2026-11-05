using System.Text;
using System.Text.Json;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What a source asks of the bundle of a session it gives to another machine.</summary>
/// <param name="MoveId">The move (<see cref="SessionMoveTransactionStore.ValidateMoveId"/>).</param>
/// <param name="BundleFolder">The folder the bundle is written to; it must not exist or be empty.</param>
/// <param name="FromMachine">The name of this machine.</param>
/// <param name="ToMachine">The name of the machine the session goes to.</param>
public sealed record SessionBundleExportRequest(string MoveId, string BundleFolder, string? FromMachine, string? ToMachine)
{
    /// <summary>Gets the ownership epoch of the session on this machine.</summary>
    public long Epoch { get; init; }

    /// <summary>Gets the device id of this machine, which the bundle and the move record carry besides its name; null when it has none.</summary>
    public string? FromDeviceId { get; init; }

    /// <summary>Gets the largest git payload the bundle may carry.</summary>
    public long MaximumPayloadBytes { get; init; } = GitSessionPayload.DefaultMaximumBytes;
}

/// <summary>What the export of a session bundle did.</summary>
/// <param name="Status">
/// <c>ok</c> (the session is frozen and its bundle written), or why nothing was exported: <c>busy</c>, <c>closed</c>,
/// <c>queued</c>, <c>remote_control</c>, <c>background_running</c>, <c>interaction_pending</c>, <c>session_in_use</c>,
/// <c>session_missing</c>, <c>not_movable</c>, <c>moving</c>, <c>relinquished</c>, <c>bundle_exists</c>,
/// <c>export_failed</c>, or a status of the git payload (<see cref="GitPayloadCapture.Status"/>).
/// </param>
/// <param name="Manifest">The manifest of the bundle, when it was written.</param>
/// <param name="Warnings">What went wrong without stopping the export, in words.</param>
public sealed record SessionBundleExportResult(string Status, SessionMoveManifest? Manifest, IReadOnlyList<string> Warnings)
{
    internal static SessionBundleExportResult Refused(string status, string? warning = null) => new(status, null, warning is null ? [] : [warning]);
}

/// <summary>A worktree a target prepared for a moved session, before the session is installed.</summary>
/// <param name="Manifest">The manifest of the bundle.</param>
/// <param name="BundleFolder">The verified bundle.</param>
/// <param name="ProjectId">The project of this machine with the repository of the session.</param>
/// <param name="ProjectPath">The folder of that project.</param>
/// <param name="WorktreeRoot">The worktree prepared for the session.</param>
/// <param name="WorkFolder">The folder the session works in, in that worktree.</param>
/// <param name="Branch">The branch checked out there; <see langword="null"/> for a detached HEAD.</param>
/// <param name="CreatedBranch">Whether the branch was created for the move.</param>
public sealed record PreparedSessionMove(SessionMoveManifest Manifest, string BundleFolder, string ProjectId, string ProjectPath,
    string WorktreeRoot, string WorkFolder, string? Branch, bool CreatedBranch);

/// <summary>What a target made of a bundle.</summary>
/// <param name="Status">
/// <c>ok</c>, or why the session cannot come: <c>bundle_invalid</c>, <c>project_missing</c>, <c>session_exists</c>,
/// <c>closed</c>, or a status of the worktree (<see cref="GitPayloadPreparation.Status"/>).
/// </param>
/// <param name="Prepared">What was prepared, when <paramref name="Status"/> is <c>ok</c>.</param>
/// <param name="Warnings">What went wrong without stopping it, or what refused it, in words.</param>
public sealed record SessionMovePreparation(string Status, PreparedSessionMove? Prepared, IReadOnlyList<string> Warnings);

// A session given to another machine: frozen and exported by the source, prepared and installed by the target with the
// same id, then relinquished by the source. The source keeps its copy read-only; every writer of its sessions folder
// honors the relinquishment kept beside the lease (HoldForWrite, ClaimEntryLease).
public sealed partial class SessionRuntimeService
{
    private SessionRelinquishmentStore? _relinquishments;

    /// <summary>Gets the store of the sessions of this instance that are frozen for a move or were given away.</summary>
    public SessionRelinquishmentStore Relinquishments => _relinquishments ??= new SessionRelinquishmentStore(_catalogOptions);

    // The refusal of a write to a session that moved, or is moving, to another machine; null when it may be written.
    private SessionInUseException? RelinquishedRefusal(string sessionId)
        => Relinquishments.Read(sessionId) is { } record ? SessionInUseException.Moved(sessionId, record) : null;

    // Holds a session for a write, then checks that it has not moved: checked once the lease is held, so that a
    // relinquishment written by the holder before it released the lease is always seen.
    private IDisposable CheckNotRelinquished(string sessionId, IDisposable hold)
    {
        if (RelinquishedRefusal(sessionId) is not { } refusal) return hold;
        hold.Dispose();
        throw refusal;
    }

    internal Task<SessionBundleExportResult> ExportOwnedSessionBundleAsync(string sessionId, SessionBundleExportRequest request, Func<string, bool> ownerBusy)
        => AdmitAsync(() => ExportBundleBodyAsync(sessionId, request, ownerBusy), CancellationToken.None);

    private async Task<SessionBundleExportResult> ExportBundleBodyAsync(string sessionId, SessionBundleExportRequest request, Func<string, bool> ownerBusy)
    {
        if (Directory.Exists(request.BundleFolder) && Directory.EnumerateFileSystemEntries(request.BundleFolder).Any())
            return SessionBundleExportResult.Refused("bundle_exists");
        if (Relinquishments.Read(sessionId) is { } moved) return SessionBundleExportResult.Refused(moved.IsRelinquished ? "relinquished" : "moving");
        if (await ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } session)
            return SessionBundleExportResult.Refused("session_missing");
        if (session.Kind != SessionViewKind.ProjectSession) return SessionBundleExportResult.Refused("not_movable", "Only a session of a project moves to another machine.");

        var warnings = new List<string>();
        if (!TryHoldForWrite(sessionId, out var lease)) return SessionBundleExportResult.Refused("session_in_use");
        using (lease)
        {
            if (ownerBusy(sessionId)) return SessionBundleExportResult.Refused("busy");
            var actor = GetActorForWork(sessionId);
            Task? retirement = null;
            var status = await actor.QueryAsync(async token =>
            {
                if (await WorkFolderRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
                if (_remoteControlWanted.ContainsKey(sessionId)) return "remote_control";
                if ((await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false))?.HasPendingQueuedPrompts() == true) return "queued";
                if (_entries.TryGetValue(sessionId, out var entry))
                {
                    // Its provider ends, and with it what it writes: the journal is complete once the retirement ended.
                    retirement = _forwarding.RetireAsync(entry.Attachment);
                    if (_entries.TryRemove(sessionId, out _)) ReleaseEntryLeaseAfter(sessionId, retirement);
                    _transitions[sessionId] = retirement;
                }

                return "ok";
            }, CancellationToken.None).ConfigureAwait(false);
            if (status != "ok") return SessionBundleExportResult.Refused(status);
            if (retirement is not null)
            {
                try
                {
                    await retirement.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    warnings.Add($"The provider of the session {sessionId} did not stop cleanly: {exception.Message}");
                }
                finally
                {
                    await actor.QueryAsync(_ =>
                    {
                        if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement))
                            _transitions.TryRemove(sessionId, out var removed);
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                    PublishSessionLifecycleEvent(sessionId);
                }
            }

            // Frozen on the actor, once nothing of the session runs: from here on no writer of this sessions folder
            // writes it, until the move is undone.
            status = await actor.QueryAsync(async token =>
            {
                if (_entries.ContainsKey(sessionId) || _transitions.ContainsKey(sessionId) || ownerBusy(sessionId)) return "busy";
                if (await WorkFolderRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
                return Relinquishments.Freeze(sessionId, request.MoveId, request.ToMachine) ? "ok" : "moving";
            }, CancellationToken.None).ConfigureAwait(false);
            if (status != "ok") return SessionBundleExportResult.Refused(status);
        }

        PublishSessionLifecycleEvent(sessionId);
        try
        {
            var result = await WriteBundleAsync(session, request, warnings).ConfigureAwait(false);
            if (result.Status == "ok") return result;
            UndoExport(sessionId, request);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            UndoExport(sessionId, request);
            return SessionBundleExportResult.Refused("export_failed", exception.Message);
        }
        catch
        {
            UndoExport(sessionId, request);
            throw;
        }
    }

    private async Task<SessionBundleExportResult> WriteBundleAsync(SessionViewDescriptor session, SessionBundleExportRequest request, List<string> warnings)
    {
        var sessionId = session.SessionId;
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        if (await store.GetJournalPathAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } journalPath || !File.Exists(journalPath))
            return SessionBundleExportResult.Refused("session_missing");
        // The sandbox the session runs in, whether it chose it or a setting of this machine gives it, goes with it: the target
        // holds it to at least that. One whose profile cannot be used here has no floor to give, and does not move.
        var sandbox = await GetSessionSandboxAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (sandbox.IsSandboxed && sandbox.Profile is null)
            return SessionBundleExportResult.Refused("sandbox_unusable", $"The sandbox profile '{sandbox.Choice.Profile}' of the session cannot be used ({sandbox.Problem}).");
        var permissionMode = await GetPermissionModeAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var bundle = Directory.CreateDirectory(request.BundleFolder).FullName;
        var workFolder = ExistingWorktree(session.WorktreeDirectory) ?? session.WorkingDirectory;

        // The git payload first: a session whose work cannot travel is refused before its journal is copied.
        var git = await GitSessionPayload.CaptureAsync(workFolder, bundle, request.MaximumPayloadBytes, CancellationToken.None).ConfigureAwait(false);
        if (git.Status != GitWorktreeService.Ok || git.Repository is not { } repository)
            return SessionBundleExportResult.Refused(git.Status, git.Message);

        // The bundle goes to another machine: no secret of this one goes with it, also one set after the session wrote it
        // (the journal, the whole output of a command beside it, what its provider keeps).
        var masker = _agentHub.SecretMasker;
        var journal = await SessionBundle.CopyIntoAsync(journalPath, bundle, SessionMoveManifest.JournalFile, masker, CancellationToken.None).ConfigureAwait(false);
        if (await store.GetAttachmentDirectoryAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } attachments)
            await SessionBundle.CopyFolderAsync(attachments, Path.Combine(bundle, SessionMoveManifest.AttachmentsFolder), masker, CancellationToken.None).ConfigureAwait(false);

        SessionMoveProviderSession? providerSession = null;
        if (await store.GetStateAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } state)
        {
            var (copied, warning) = await _agentHub.ExportProviderSessionAsync(state, workFolder, Path.Combine(bundle, SessionMoveManifest.ProviderFolder), CancellationToken.None)
                .ConfigureAwait(false);
            if (warning is not null) warnings.Add(warning);
            if (copied)
            {
                await SessionBundle.MaskFolderAsync(Path.Combine(bundle, SessionMoveManifest.ProviderFolder), masker, CancellationToken.None).ConfigureAwait(false);
                providerSession = new SessionMoveProviderSession(state.ProviderKey, SessionMoveManifest.ProviderFolder);
            }
        }

        var manifest = await SessionBundle.WriteManifestAsync(bundle, new SessionMoveManifest
        {
            MoveId = request.MoveId,
            SessionId = sessionId,
            SessionCreatedAt = session.CreatedAt,
            Title = masker?.MaskText(session.Title) ?? session.Title,
            SpaceId = session.SpaceId,
            FromMachine = request.FromMachine,
            FromDeviceId = request.FromDeviceId,
            Epoch = request.Epoch,
            CreatedAt = DateTimeOffset.UtcNow,
            Journal = journal,
            WorkingFolder = workFolder,
            ProviderKey = session.ResolvedProviderKey,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort?.ToString(),
            Sandbox = sandbox.Profile is not null ? sandbox.Choice.Profile : null,
            SandboxFloor = sandbox.Profile is { } profile ? ToMoveFloor(SandboxConfinement.Of(profile)) : null,
            PermissionMode = permissionMode,
            AgentPromptId = session.AgentPromptId,
            Repository = repository,
            ProviderSession = providerSession,
        }, CancellationToken.None).ConfigureAwait(false);
        return new SessionBundleExportResult("ok", manifest, warnings);
    }

    // An export that failed: the session is written again here, and what was written of the bundle goes.
    private void UndoExport(string sessionId, SessionBundleExportRequest request)
    {
        try
        {
            if (Directory.Exists(request.BundleFolder)) Directory.Delete(request.BundleFolder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A bundle left behind holds a copy of the session: it is removed with the move's records.
        }

        Relinquishments.Unfreeze(sessionId, request.MoveId);
        PublishSessionLifecycleEvent(sessionId);
    }

    /// <summary>
    /// Undoes the freeze of a session whose move the arbiter never committed: it can be written here again.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move that froze it.</param>
    /// <returns>Whether it was unfrozen; <see langword="false"/> when that move did not freeze it.</returns>
    /// <exception cref="IOException">The record cannot be removed.</exception>
    public bool UnfreezeMovedSession(string sessionId, string moveId)
    {
        var unfrozen = Relinquishments.Unfreeze(sessionId, moveId);
        if (unfrozen) PublishSessionLifecycleEvent(sessionId);
        return unfrozen;
    }

    /// <summary>
    /// Records that a session this machine froze belongs to another machine now: it stays read-only here.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move that gave it.</param>
    /// <param name="toMachine">The machine that owns it.</param>
    /// <param name="epoch">The ownership epoch the move committed.</param>
    /// <exception cref="IOException">The record cannot be written.</exception>
    public void RelinquishMovedSession(string sessionId, string moveId, string? toMachine, long epoch)
    {
        Relinquishments.Relinquish(sessionId, moveId, toMachine, epoch);
        PublishSessionLifecycleEvent(sessionId);
    }

    /// <summary>
    /// Prepares this machine to take a session from a bundle: the bundle is checked against its manifest, the project
    /// with the repository of the session is found, and a worktree is made on its branch at its HEAD with its work in
    /// progress, checked against the manifest. Nothing of the session exists here yet.
    /// </summary>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="cancellationToken">Stops the checks and git.</param>
    /// <returns>What was prepared.</returns>
    /// <exception cref="ArgumentException"><paramref name="bundleFolder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<SessionMovePreparation> PrepareMovedSessionAsync(string bundleFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (await SessionBundle.ReadManifestAsync(bundleFolder, cancellationToken).ConfigureAwait(false) is not { } manifest)
            return new("bundle_invalid", null, ["The bundle has no manifest that can be read."]);
        if (await SessionBundle.VerifyAsync(bundleFolder, manifest, cancellationToken).ConfigureAwait(false) is { } broken)
            return new("bundle_invalid", null, [broken]);
        if (!SessionLeaseStore.IsValidSessionId(manifest.SessionId)) return new("bundle_invalid", null, [$"'{manifest.SessionId}' is not a session id."]);
        if (RefuseMovedSandbox(manifest) is { } sandboxRefusal) return sandboxRefusal;

        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        if (await store.GetJournalPathAsync(manifest.SessionId, cancellationToken).ConfigureAwait(false) is not null
            && Relinquishments.Read(manifest.SessionId) is not { IsRelinquished: true })
            return new("session_exists", null, [$"The session {manifest.SessionId} is already on this machine."]);

        var project = await FindProjectOfRepositoryAsync(manifest.Repository.Key, manifest.Repository.RelativeFolder, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return new("project_missing", null, [$"No project of this machine is in the repository {manifest.Repository.Key}" +
                (manifest.Repository.RelativeFolder is "." ? "." : $" at {manifest.Repository.RelativeFolder}.")]);

        var warnings = new List<string>();
        if (manifest.ProviderKey is { } providerKey && _agentHub.GetProviderRegistrationVersion(providerKey) == 0)
            warnings.Add($"The provider {providerKey} of the session is not configured on this machine: choose another one before its next prompt.");

        var worktrees = new GitWorktreeService(_catalogOptions, _configStore);
        var root = GitWorktreeService.FindCheckoutRoot(project.ProjectPath) ?? project.ProjectPath;
        var parent = worktrees.ParentFolder(worktrees.ReadSettings(), project, root);
        Directory.CreateDirectory(parent);
        var name = WorktreeNames.PickFree(Random.Shared, candidate => Path.Exists(Path.Combine(parent, candidate)));
        var prepared = await GitSessionPayload.PrepareAsync(project.ProjectPath, manifest.Repository, bundleFolder, Path.Combine(parent, name), cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.Succeeded) return new(prepared.Status, null, prepared.Message is null ? warnings : [.. warnings, prepared.Message]);
        if (manifest.Repository.Payload.LeftBehind.Any(static file => file.Reason == SessionMoveLeftBehind.Secret))
            warnings.Add("Files that look like secrets stayed on the source: " + string.Join(", ", manifest.Repository.Payload.LeftBehind
                .Where(static file => file.Reason == SessionMoveLeftBehind.Secret).Select(static file => file.Path)) + ".");
        return new("ok", new PreparedSessionMove(manifest, Path.GetFullPath(bundleFolder), project.Id, project.ProjectPath,
            prepared.WorktreeRoot!, prepared.WorkFolder!, prepared.Branch, prepared.CreatedBranch), warnings);
    }

    /// <summary>Takes away the worktree a target prepared for a move that was not committed.</summary>
    /// <param name="prepared">What was prepared.</param>
    /// <returns>A task that completes once git ended.</returns>
    public Task DiscardPreparedMoveAsync(PreparedSessionMove prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return GitSessionPayload.RemoveAsync(prepared.ProjectPath, prepared.WorktreeRoot, prepared.CreatedBranch ? prepared.Branch : null, CancellationToken.None);
    }

    /// <summary>
    /// Installs a prepared session once its move is committed: its journal and attachments with the same id, its header
    /// on the project and the worktree of this machine, the move record (<c>codealta.sessionMoved</c>), the conversation
    /// its provider keeps, and a note its next prompt carries. Running it again after a failure goes on where it stopped.
    /// </summary>
    /// <param name="prepared">What was prepared.</param>
    /// <param name="toMachine">The name of this machine.</param>
    /// <param name="epoch">The ownership epoch the move committed.</param>
    /// <param name="cancellationToken">Stops before the journal is written.</param>
    /// <returns>What went wrong without stopping the install, in words.</returns>
    /// <exception cref="InvalidOperationException">The session cannot be installed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before the journal was written.</exception>
    public Task<IReadOnlyList<string>> InstallMovedSessionAsync(PreparedSessionMove prepared, string? toMachine, long epoch, CancellationToken cancellationToken)
        => InstallMovedSessionAsync(prepared, toMachine, null, epoch, cancellationToken);

    /// <summary>
    /// Installs a prepared session once its move is committed, like
    /// <see cref="InstallMovedSessionAsync(PreparedSessionMove, string?, long, CancellationToken)"/>, and records the device id of this
    /// machine in the move record beside its name.
    /// </summary>
    /// <param name="prepared">What was prepared.</param>
    /// <param name="toMachine">The name of this machine.</param>
    /// <param name="toDeviceId">The device id of this machine; null when it has none.</param>
    /// <param name="epoch">The ownership epoch the move committed.</param>
    /// <param name="cancellationToken">Stops before the journal is written.</param>
    /// <returns>What went wrong without stopping the install, in words.</returns>
    /// <exception cref="InvalidOperationException">The session cannot be installed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before the journal was written.</exception>
    public Task<IReadOnlyList<string>> InstallMovedSessionAsync(PreparedSessionMove prepared, string? toMachine, string? toDeviceId, long epoch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return AdmitAsync(() => InstallMovedSessionBodyAsync(prepared, toMachine, toDeviceId, epoch, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<string>> InstallMovedSessionBodyAsync(PreparedSessionMove prepared, string? toMachine, string? toDeviceId, long epoch, CancellationToken cancellationToken)
    {
        var manifest = prepared.Manifest;
        var sessionId = manifest.SessionId;
        var warnings = new List<string>();
        var journalStore = _sessionViewCatalog.JournalStore;
        var store = journalStore.CreateSessionStore();
        var project = await _projectCatalog.GetByIdAsync(prepared.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The project {prepared.ProjectId} is gone.");

        // A session that comes back to this machine replaces the copy it left here.
        var returning = Relinquishments.Read(sessionId) is { IsRelinquished: true };
        var attachmentsFolder = journalStore.GetNewSessionAttachmentDirectory(new SessionViewDescriptor { SessionId = sessionId, CreatedAt = manifest.SessionCreatedAt });
        var journalPath = Path.ChangeExtension(attachmentsFolder, ".jsonl");
        using (var lease = Leases?.Hold(sessionId))
        {
            var existing = await store.GetJournalPathAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (returning)
            {
                if (existing is not null && !string.Equals(existing, journalPath, FolderComparison)) File.Delete(existing);
                if (File.Exists(journalPath)) File.Delete(journalPath);
                if (Directory.Exists(attachmentsFolder)) Directory.Delete(attachmentsFolder, recursive: true);
                Relinquishments.Clear(sessionId);
                existing = null;
            }

            if (existing is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SessionBundle.CopyFolderAsync(Path.Combine(prepared.BundleFolder, SessionMoveManifest.AttachmentsFolder), attachmentsFolder, CancellationToken.None)
                    .ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
                var temporary = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(Path.Combine(prepared.BundleFolder, SessionMoveManifest.JournalFile), temporary);
                File.Move(temporary, journalPath);
                existing = journalPath;
            }

            await store.ProjectJournalAsync(existing, CancellationToken.None).ConfigureAwait(false);
            await _agentSessionCatalog.InvalidateAsync(sessionId, CancellationToken.None).ConfigureAwait(false);

            // The header and the summary name this machine's project and worktree.
            if (await journalStore.ReadHeaderAsync(sessionId, manifest.SessionCreatedAt, CancellationToken.None).ConfigureAwait(false) is not { } header)
                throw new InvalidOperationException($"The journal of the session {sessionId} has no header.");
            var summary = await store.GetSessionSummaryAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (header.ProjectRef != project.Id || header.WorkingDirectory != project.ProjectPath || summary?.WorktreeDirectory != prepared.WorkFolder)
            {
                await journalStore.MoveSessionAsync(header.ToDescriptor(), new SessionViewMove(project.Id, project.ProjectPath, prepared.WorkFolder, null, null)
                    { ProviderFamily = await RuntimeFamilyOfKeyWrittenAsFamilyAsync(summary, CancellationToken.None).ConfigureAwait(false) }, CancellationToken.None).ConfigureAwait(false);
                await store.ProjectJournalAsync(existing, CancellationToken.None).ConfigureAwait(false);
                await _agentSessionCatalog.InvalidateAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (await ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } session)
            throw new InvalidOperationException($"The session {sessionId} cannot be read once installed.");

        // The sandbox and the permission mode it ran in on the source are its own here, whatever the settings of this
        // machine say: it is never less confined for having moved (the profile was checked when the move was prepared).
        if (manifest.Sandbox is { } movedSandbox && !string.Equals(NormalizeOptionalText(session.Sandbox), movedSandbox, StringComparison.OrdinalIgnoreCase))
            await SetOwnedSandboxAsync(session, movedSandbox).ConfigureAwait(false);
        if (manifest.PermissionMode is { } movedMode
            && Runtime.SessionPermissionModes.Rank(await GetPermissionModeAsync(sessionId, CancellationToken.None).ConfigureAwait(false)) > Runtime.SessionPermissionModes.Rank(movedMode))
            await SetOwnedPermissionModeAsync(session, movedMode).ConfigureAwait(false);
        var installedJournal = await store.GetJournalPathAsync(sessionId, CancellationToken.None).ConfigureAwait(false) ?? journalPath;
        if (!JournalTailContains(installedJournal, manifest.MoveId))
        {
            var record = new SessionMovedRecord
            {
                MoveId = manifest.MoveId,
                From = manifest.FromMachine,
                To = toMachine,
                FromDeviceId = manifest.FromDeviceId,
                ToDeviceId = toDeviceId,
                OldWorkingFolder = manifest.WorkingFolder,
                NewWorkingFolder = prepared.WorkFolder,
                Epoch = epoch,
                Time = DateTimeOffset.UtcNow,
            };
            using var raw = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(record, SessionMoveJsonContext.Default.SessionMovedRecord));
            await AppendSessionEventAsync(session, new AgentRawEvent(new ModelProviderId(session.ResolvedProviderKey), sessionId, record.Time,
                SessionMovedRecord.EventType, raw.RootElement.Clone()), CancellationToken.None).ConfigureAwait(false);
            await RecordWorkFolderAsync(sessionId, AgentSessionUpdateKind.Info, prepared.WorkFolder,
                $"This session moved from {MachineName(manifest.FromMachine)} (`{manifest.WorkingFolder}`) to {MachineName(toMachine)}: it now works in `{prepared.WorkFolder}`.",
                warnings).ConfigureAwait(false);
        }

        // What its provider keeps of it goes where the provider of this machine looks for it.
        if (manifest.ProviderSession is { } providerSession
            && await store.GetStateAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } state)
        {
            try
            {
                if (await _agentHub.ImportProviderSessionAsync(state, Path.Combine(prepared.BundleFolder, providerSession.Folder), prepared.WorkFolder, CancellationToken.None)
                        .ConfigureAwait(false) is { } warning)
                    warnings.Add(warning);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"The conversation its provider keeps for the session {sessionId} was not placed: {exception.Message}");
            }
        }

        _workFolderNotices[sessionId] = new WorkFolderNotice(manifest.WorkingFolder,
            MovedToMachineNoteText(manifest.FromMachine, manifest.WorkingFolder, toMachine, prepared.WorkFolder));
        PublishSessionLifecycleEvent(sessionId);
        PublishSessionCatalogEvent(session);
        await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        return warnings;
    }

    // A session sandboxed on the source comes only where a profile of the same name is at least as strict: the fleet is
    // never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8). Null when it may come.
    private SessionMovePreparation? RefuseMovedSandbox(SessionMoveManifest manifest)
    {
        if (manifest.Sandbox is not { } name) return null;
        if (manifest.SandboxFloor is not { } floor) return new("bundle_invalid", null, [$"The bundle names the sandbox '{name}' of the session, but not what it confines."]);
        if (ResolveSandboxConfinement(name) is not { } here)
            return new("sandbox_missing", null, [$"This machine has no sandbox profile '{name}' that can be used: the session would run here less confined than on {MachineName(manifest.FromMachine)}."]);
        return here.WeakerThan(FromMoveFloor(name, floor)) is { } why
            ? new("sandbox_weaker", null, [$"The sandbox profile '{name}' of this machine lets the session do more than on {MachineName(manifest.FromMachine)}: {why}."])
            : null;
    }

    private static SessionMoveSandboxFloor ToMoveFloor(SandboxConfinement confinement)
        => new(SandboxConfinement.NetworkName(confinement.Network), confinement.Hosts, confinement.LocalPorts, confinement.Write, confinement.Read, confinement.Deny,
            confinement.Env, confinement.McpServers, confinement.Outside == SandboxOutside.Ask ? "ask" : "never", confinement.Fleet);

    private static SandboxConfinement FromMoveFloor(string profile, SessionMoveSandboxFloor floor)
        => new(profile, SandboxConfinement.ParseNetwork(floor.Network), floor.Hosts ?? [], floor.LocalPorts ?? [], floor.Write ?? [], floor.Read ?? [], floor.Deny ?? [],
            floor.Env ?? [], floor.McpServers ?? [], floor.Outside == "ask" ? SandboxOutside.Ask : SandboxOutside.Never, floor.Fleet);

    internal static string MovedToMachineNoteText(string? fromMachine, string fromDirectory, string? toMachine, string toDirectory)
        => $"<codealta-context>Working directory: this session moved from {MachineName(fromMachine)}, `{fromDirectory}`, to {MachineName(toMachine)}, `{toDirectory}`, "
            + $"a git worktree of the project on its branch with the work in progress that was not committed. Read, edit and run commands in `{toDirectory}` from now on. "
            + "Paths in earlier turns refer to the old machine, and what was run there (processes, servers, files outside the repository, files git ignores) did not come with the session.</codealta-context>";

    private static string MachineName(string? machine) => string.IsNullOrWhiteSpace(machine) ? "another machine" : machine.Trim();

    /// <summary>Finds the project of this machine in a repository, at the same folder of the repository: where a moved session goes.</summary>
    /// <param name="repositoryKey">The repository key (<see cref="RepositoryKey"/>).</param>
    /// <param name="relativeFolder">The folder of the project in the repository, with forward slashes; <c>.</c> for its root.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>The project; null when no project of this machine is there.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<ProjectDescriptor?> FindProjectOfRepositoryAsync(string repositoryKey, string relativeFolder, CancellationToken cancellationToken)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var project in projects)
        {
            if (project.Archived || string.IsNullOrWhiteSpace(project.ProjectPath) || !Directory.Exists(project.ProjectPath)) continue;
            var root = GitWorktreeService.FindCheckoutRoot(project.ProjectPath);
            if (root is null) continue;
            var relative = Path.GetRelativePath(root, project.ProjectPath).Replace('\\', '/');
            if (!string.Equals(relative, relativeFolder, FolderComparison)) continue;
            if (await RepositoryKey.ReadAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false) == repositoryKey) return project;
        }

        return null;
    }

    // Whether the end of a journal holds a text: the move record of a move whose install stopped after it.
    private static bool JournalTailContains(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, 256 * 1024);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            return Encoding.UTF8.GetString(buffer).Contains(text, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
