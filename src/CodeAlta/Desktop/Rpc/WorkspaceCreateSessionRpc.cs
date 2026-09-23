using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly ModelProviderRegistry? _sessionProviders;
    private readonly Func<ProjectDescriptor?, ModelProviderDescriptor, string?, Task<SessionViewDescriptor>>? _createSession;
    private readonly Func<string, string?, string, string, Task<bool>>? _renameSession;
    private readonly Func<string, string?, string, string, Task<string>>? _deleteSession;
    private readonly object _sessionGate = new();
    private Task<WorkspaceCreateSessionResponse>? _sessionWork;
    private Task<WorkspaceRenameSessionResponse>? _renameWork;
    private Task<WorkspaceDeleteSessionResponse>? _deleteWork;
    private bool _sessionsClosed;

    internal WorkspaceService(CodeAltaHost host, string epoch) : this(host.WorkspaceReads, host.ProjectCatalog, epoch)
    {
        _sessionProviders = host.ModelProviderRegistry;
        _createSession = host.Commands.CreateDraftSessionAsync;
        _renameSession = host.Commands.RenameSessionAsync;
        _deleteSession = host.Commands.DeleteCatalogSessionAsync;
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
    /// <param name="request">Exact scope and expected host epoch.</param>
    /// <param name="cancellationToken">Cancels only the wait after admission, not the creation.</param>
    /// <returns>The created identity, a definite refusal, or an uncertain outcome after admission.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("createSession")]
    public async Task<WorkspaceCreateSessionResponse> CreateSessionAsync(WorkspaceCreateSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceCreateSessionResponse Reply(string status, string? id = null, string? workspacePath = null)
            => status is "invalid_scope" or "unconfigured"
                ? new(status, _importEpoch, null, null, null, null, null)
                : new(status, _importEpoch, request?.Scope, request?.ProjectId, request?.ProjectPath, id, workspacePath);
        if (_sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidTitle(request.Title)
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
            if (_sessionsClosed) return Reply("closed");
            if (_sessionWork is not null) return Reply("busy");
            var providers = _sessionProviders.ListProviders();
            var provider = providers.FirstOrDefault(static value => value.IsDefault) ?? providers.FirstOrDefault();
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
            => new(status, _importEpoch, request.Scope, request.ProjectId, request.ProjectPath, id, workspacePath);
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

internal sealed record WorkspaceCreateSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId, string? ProjectPath, string? Title);
internal sealed record WorkspaceCreateSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId, string? ProjectPath,
    string? SessionId, string? WorkspacePath);
