using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly ModelProviderRegistry? _sessionProviders;
    // The provider of a session that names none: the default of the configuration, as the page shows it.
    private readonly DesktopDefaultProvider? _defaultProvider;
    private readonly Func<ProjectDescriptor?, ModelProviderDescriptor, string?, Task<SessionViewDescriptor>>? _createSession;
    // A session that works in a worktree: the worktree is created first, and removed again when no session comes of it.
    private readonly GitWorktreeService? _worktrees;
    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, string?, string, Task<SessionViewDescriptor>>? _createInWorktree;
    private readonly Func<OwnedProjectReferenceScope?, CancellationToken, Task<IReadOnlyList<OwnedPromptChoice>?>>? _draftPrompts;
    private readonly Func<string, string?, string, string, Task<bool>>? _renameSession;
    private readonly Func<string, string?, string, string, Task<string>>? _deleteSession;
    // One instance monitor makes catalog-affecting admission atomic. Workers keep
    // their own original tasks; no monitor is held over awaited work.
    private object _sessionGate => _importGate;
    private Task<WorkspaceCreateSessionResponse>? _sessionWork;
    private Task<WorkspaceRenameSessionResponse>? _renameWork;
    private Task<WorkspaceDeleteSessionResponse>? _deleteWork;
    private bool _sessionsClosed;

    internal WorkspaceService(CodeAltaHost host, string epoch, GitWorktreeService? worktrees = null) : this(host.WorkspaceReads, host.ProjectCatalog, epoch)
    {
        _sessionProviders = host.ModelProviderRegistry;
        _defaultProvider = new(host.CatalogOptions);
        _createSession = (project, provider, title) => host.Commands.CreateDraftSessionAsync(project, provider, title);
        _worktrees = worktrees;
        _createInWorktree = (project, provider, title, folder) => host.Commands.CreateDraftSessionAsync(project, provider, title, null, folder);
        _draftPrompts = host.Commands.GetDraftPromptChoicesAsync;
        _renameSession = host.Commands.RenameSessionAsync;
        _deleteSession = host.Commands.DeleteCatalogSessionAsync;
    }

    // Only catalogs: this read does not initialize a provider, create a session or confer send authority.
    [NeoRpcMethod("draftPrompts")]
    public async Task<WorkspaceDraftPromptsResponse> DraftPromptsAsync(WorkspaceDraftPromptsRequest request, CancellationToken cancellationToken)
    {
        WorkspaceDraftPromptsResponse Reply(string status, IReadOnlyList<OwnedPromptChoice>? prompts = null)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath,
                prompts?.Select(p => new SessionPromptChoice(p.Id, p.Name)).ToArray() ?? []);
        if (_draftPrompts is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch)
            || (request.ProjectId is null) != (request.ProjectPath is null)
            || request.ProjectId is not null && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        try
        {
            var prompts = await _draftPrompts(request.ProjectId is null ? null : new(request.ProjectId, request.ProjectPath!), cancellationToken).ConfigureAwait(false);
            return Reply(prompts is null ? "unavailable" : "ok", prompts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Reply("read_failed"); }
    }

    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        ModelProviderRegistry providers, Func<ProjectDescriptor?, ModelProviderDescriptor, string?, Task<SessionViewDescriptor>> createSession)
        : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(createSession);
        _sessionProviders = providers;
        _createSession = createSession;
    }

    /// <summary>Creates an owned global or exact-catalog-project session; never accepts a renderer-supplied project descriptor.</summary>
    /// <remarks>
    /// A project session can be asked to work in a new git worktree: the worktree is created first, from the
    /// commit the folder of the project is on or from a named branch, and the answer is <c>worktree_failed</c>
    /// with the reason when git creates none.
    /// </remarks>
    /// <param name="request">Exact scope, expected host epoch and optional canonical enabled provider identity. Null uses the default or first enabled provider.</param>
    /// <param name="cancellationToken">Cancels only the wait after admission, not the creation.</param>
    /// <returns>The created identity, a definite refusal, or an uncertain outcome after admission.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("createSession", TimeoutMilliseconds = 600_000)]
    public async Task<WorkspaceCreateSessionResponse> CreateSessionAsync(WorkspaceCreateSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceCreateSessionResponse Reply(string status, string? id = null, string? workspacePath = null)
            => status is "invalid_scope" or "unconfigured"
                ? new(status, _importEpoch, null, null, null, null, null)
                : new(status, _importEpoch, request?.Scope, request?.ProjectId, request?.ProjectPath, id, workspacePath, request?.ProviderId);
        if (_sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidTitle(request.Title)
            || request.ProviderId is not null && !ValidScopeValue(request.ProviderId, 256)
            || request.Scope is not ("global" or "project")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath is not null)
            || request.Scope == "project" && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096))
            || request.Worktree && request.Scope != "project"
            || request.BaseBranch is not null && (!request.Worktree || !GitWorktreeService.IsReferenceName(request.BaseBranch)))
            return Reply("invalid_scope");
        if (request.Worktree && (_worktrees is null || _createInWorktree is null)) return Reply("unconfigured");
        if (request.Scope == "project")
        {
            try { if (!Path.IsPathFullyQualified(request.ProjectPath!)) return Reply("invalid_scope"); }
            catch (ArgumentException) { return Reply("invalid_scope"); }
        }
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();

        Task<WorkspaceCreateSessionResponse> work;
        lock (_sessionGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed");
            if (CatalogAdmissionBusy) return Reply("busy");
            var providers = _sessionProviders.ListProviders();
            var matches = request.ProviderId is null ? [] : providers.Where(value => value.ProviderId.Value == request.ProviderId).Take(2).ToArray();
            var provider = request.ProviderId is null
                ? _defaultProvider?.Of(providers, request.ProjectPath) ?? providers.FirstOrDefault(static value => value.IsDefault) ?? providers.FirstOrDefault()
                : matches.Length == 1 ? matches[0] : null;
            if (provider is null) return Reply("provider_unavailable");
            var completion = new TaskCompletionSource<WorkspaceCreateSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionWork = work = completion.Task;
            _ = CreateSessionOwnedAsync(request, provider, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CloseSessionsAsync()
    {
        Task? create;
        Task? rename;
        Task? delete;
        lock (_sessionGate) { _sessionsClosed = true; create = _sessionWork; rename = _renameWork; delete = _deleteWork; }
        if (create is not null) await create.ConfigureAwait(false);
        if (rename is not null) await rename.ConfigureAwait(false);
        if (delete is not null) await delete.ConfigureAwait(false);
    }

    private async Task CreateSessionOwnedAsync(WorkspaceCreateSessionRequest request, ModelProviderDescriptor provider,
        TaskCompletionSource<WorkspaceCreateSessionResponse> completion)
    {
        WorkspaceCreateSessionResponse Reply(string status, string? id = null, string? workspacePath = null)
            => new(status, _importEpoch, request.Scope, request.ProjectId, request.ProjectPath, id, workspacePath, request.ProviderId);
        try
        {
            ProjectDescriptor? project = null;
            if (request.Scope == "project")
            {
                project = await _importCatalog!.GetByIdAsync(request.ProjectId!, CancellationToken.None).ConfigureAwait(false);
                if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath
                    || !Directory.Exists(project.ProjectPath))
                {
                    completion.TrySetResult(Reply("project_missing"));
                    return;
                }
            }
            // Catalog resolution can yield. Revalidate the original descriptor, not a replacement
            // carrying the same key or a newly configured default. This read never starts a runtime.
            if (!_sessionProviders!.TryGetProvider(provider.ProviderId, out var current)
                || !ReferenceEquals(provider, current) || !current.IsEnabled)
            {
                completion.TrySetResult(Reply("provider_unavailable"));
                return;
            }
            GitWorktreeCreation? worktree = null;
            if (request.Worktree)
            {
                worktree = await _worktrees!.CreateAsync(project!, request.BaseBranch, CancellationToken.None).ConfigureAwait(false);
                if (!worktree.Succeeded)
                {
                    completion.TrySetResult(Reply("worktree_failed") with { Reason = worktree.Status, Message = worktree.Message });
                    return;
                }
            }
            SessionViewDescriptor session;
            try
            {
                session = worktree is null ? await _createSession!(project, provider, request.Title).ConfigureAwait(false)
                    : await _createInWorktree!(project!, provider, request.Title, worktree.Folder!).ConfigureAwait(false);
            }
            catch (Exception) when (worktree is not null)
            {
                // Nothing was written there yet: the checkout that no session will use goes, and the failure stays what it is.
                try { await _worktrees!.RemoveAsync(project!.ProjectPath, worktree.Root!, force: false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
                throw;
            }
            if (!ValidScopeValue(session.SessionId, 256)
                || session.Kind != (project is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession)
                || session.ProjectRef != project?.Id || session.WorkingDirectory != (project?.ProjectPath ?? _importCatalog!.Options.GlobalRoot)
                || !ValidScopeValue(session.WorkingDirectory, 4096))
            {
                if (LogManager.IsInitialized)
                    LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(
                        $"Session creation returned an unexpected scope: kind={session.Kind} project={session.ProjectRef ?? "none"} directory={session.WorkingDirectory}");
                completion.TrySetResult(Reply("create_unconfirmed"));
                return;
            }
            completion.TrySetResult(Reply("ok", session.SessionId, session.WorkingDirectory) with { WorktreePath = session.WorktreeDirectory });
        }
        catch (Exception failure)
        {
            // A journal/provider write may have committed before failure. Never retry or claim no creation.
            if (LogManager.IsInitialized)
                LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(failure, "Session creation failed");
            completion.TrySetResult(Reply("create_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_sessionWork, completion.Task)) _sessionWork = null; } }
    }

    private static bool ValidScopeValue(string? value, int maximum)
        => value is { Length: > 0 } && value.Length <= maximum && value == value.Trim()
            && !value.Any(char.IsControl) && WellFormed(value);

    private static bool ValidTitle(string? value) => value is null || ValidScopeValue(value, 256);

    private static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

/// <param name="Worktree">Creates a git worktree for a project session, which then works there.</param>
/// <param name="BaseBranch">The branch the worktree starts from; the commit the folder of the project is on when null.</param>
internal sealed record WorkspaceCreateSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId, string? ProjectPath, string? Title,
    string? ProviderId = null, bool Worktree = false, string? BaseBranch = null);
/// <param name="WorktreePath">The folder the session works in, when it is a worktree.</param>
/// <param name="Reason">With <c>worktree_failed</c>: <c>not_repository</c>, <c>no_commit</c>, <c>git_unavailable</c>, <c>invalid</c>, <c>timeout</c> or <c>failed</c>.</param>
/// <param name="Message">With <c>worktree_failed</c>: what git said.</param>
internal sealed record WorkspaceCreateSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId, string? ProjectPath,
    string? SessionId, string? WorkspacePath, string? ProviderId = null, string? WorktreePath = null, string? Reason = null, string? Message = null);
internal sealed record WorkspaceDraftPromptsRequest(string ExpectedHostEpoch, string? ProjectId, string? ProjectPath);
internal sealed record WorkspaceDraftPromptsResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    IReadOnlyList<SessionPromptChoice> Prompts);
