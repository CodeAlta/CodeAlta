using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly ModelProviderRegistry? _sessionProviders;
    private readonly Func<ProjectDescriptor?, ModelProviderDescriptor, string?, Task<SessionViewDescriptor>>? _createSession;
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

    internal WorkspaceService(CodeAltaHost host, string epoch) : this(host.WorkspaceReads, host.ProjectCatalog, epoch)
    {
        _sessionProviders = host.ModelProviderRegistry;
        _createSession = host.Commands.CreateDraftSessionAsync;
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
    /// <param name="request">Exact scope, expected host epoch and optional canonical enabled provider identity. Null uses the default or first enabled provider.</param>
    /// <param name="cancellationToken">Cancels only the wait after admission, not the creation.</param>
    /// <returns>The created identity, a definite refusal, or an uncertain outcome after admission.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("createSession")]
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
            || request.Scope == "project" && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)))
            return Reply("invalid_scope");
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
                ? providers.FirstOrDefault(static value => value.IsDefault) ?? providers.FirstOrDefault()
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
            var session = await _createSession!(project, provider, request.Title).ConfigureAwait(false);
            if (!ValidScopeValue(session.SessionId, 256)
                || session.Kind != (project is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession)
                || session.ProjectRef != project?.Id || session.WorkingDirectory != (project?.ProjectPath ?? _importCatalog!.Options.GlobalRoot)
                || !ValidScopeValue(session.WorkingDirectory, 4096))
            {
                completion.TrySetResult(Reply("create_unconfirmed"));
                return;
            }
            completion.TrySetResult(Reply("ok", session.SessionId, session.WorkingDirectory));
        }
        catch (Exception)
        {
            // A journal/provider write may have committed before failure. Never retry or claim no creation.
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

internal sealed record WorkspaceCreateSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId, string? ProjectPath, string? Title,
    string? ProviderId = null);
internal sealed record WorkspaceCreateSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId, string? ProjectPath,
    string? SessionId, string? WorkspacePath, string? ProviderId = null);
internal sealed record WorkspaceDraftPromptsRequest(string ExpectedHostEpoch, string? ProjectId, string? ProjectPath);
internal sealed record WorkspaceDraftPromptsResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    IReadOnlyList<SessionPromptChoice> Prompts);
