using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The most conversations a listing returns; the reply counts them all.
    internal const int ClaudeCodeSessionsLimit = 50;

    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, int, CancellationToken, Task<ExternalConversationListing>>? _listExternalConversations;
    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, string, Task<ExternalConversationImportResult>>? _importExternalConversation;
    private Task<WorkspaceImportClaudeCodeSessionResponse>? _claudeImportWork;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch, ModelProviderRegistry providers,
        Func<ProjectDescriptor, ModelProviderDescriptor, int, CancellationToken, Task<ExternalConversationListing>> listExternalConversations,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, Task<ExternalConversationImportResult>> importExternalConversation)
        : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(listExternalConversations);
        ArgumentNullException.ThrowIfNull(importExternalConversation);
        _sessionProviders = providers;
        _listExternalConversations = listExternalConversations;
        _importExternalConversation = importExternalConversation;
    }

    /// <summary>
    /// Lists the Claude Code sessions started in VS Code or by the CLI in a terminal in the folder of an exact catalog
    /// project, which no session of CodeAlta continues: the most recently changed first, at most
    /// <see cref="ClaudeCodeSessionsLimit"/>, with how many there are. Only reads the transcripts.
    /// </summary>
    /// <param name="request">The exact project and the expected host epoch.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>
    /// <c>ok</c> with the sessions; <c>no_provider</c> when no enabled provider shares its conversations (Claude Code);
    /// or a refusal: <c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>project_missing</c>, <c>read_failed</c>.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("claudeCodeSessions", TimeoutMilliseconds = 120_000)]
    public async Task<WorkspaceClaudeCodeSessionsResponse> ClaudeCodeSessionsAsync(WorkspaceClaudeCodeSessionsRequest request, CancellationToken cancellationToken)
    {
        WorkspaceClaudeCodeSessionsResponse Reply(string status, string? providerId = null, IReadOnlyList<WorkspaceClaudeCodeSession>? sessions = null, int total = 0)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, providerId, sessions ?? [], total);
        if (_listExternalConversations is null || _sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        var project = await ExactProjectAsync(request.ProjectId, request.ProjectPath).ConfigureAwait(false);
        if (project is null) return Reply("project_missing");
        if (ClaudeCodeProviderOf(project.ProjectPath) is not { } provider) return Reply("no_provider");
        try
        {
            var listing = await _listExternalConversations(project, provider, ClaudeCodeSessionsLimit, cancellationToken).ConfigureAwait(false);
            return Reply("ok", provider.ProviderId.Value,
                [.. listing.Conversations.Take(ClaudeCodeSessionsLimit).Select(static conversation => new WorkspaceClaudeCodeSession(
                    conversation.ConversationId,
                    conversation.Title.Length <= 256 ? conversation.Title : conversation.Title[..256],
                    conversation.LastChangedAt,
                    conversation.Origin,
                    conversation.IsHeld))],
                listing.Total);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn(failure, "Claude Code sessions could not be listed");
            return Reply("read_failed", provider.ProviderId.Value);
        }
    }

    /// <summary>
    /// Imports a Claude Code session started in VS Code or a terminal in the folder of an exact catalog project as a new
    /// session of that project, which continues the conversation; importing a session a session of CodeAlta continues
    /// already gives that session (<c>existing</c>).
    /// </summary>
    /// <remarks>An admitted import runs to completion even if the caller stops waiting; close drains it before host disposal.</remarks>
    /// <param name="request">The exact project, the session of the CLI and the expected host epoch.</param>
    /// <param name="cancellationToken">Cancels only the wait after admission, not the import.</param>
    /// <returns>
    /// <c>ok</c> or <c>existing</c> with the session; <c>not_found</c> when the project has no such session to import;
    /// a refusal (<c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>project_missing</c>, <c>no_provider</c>,
    /// <c>closed</c>, <c>busy</c>); or <c>import_unconfirmed</c> when it failed after admission.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("importClaudeCodeSession", TimeoutMilliseconds = 600_000)]
    public async Task<WorkspaceImportClaudeCodeSessionResponse> ImportClaudeCodeSessionAsync(WorkspaceImportClaudeCodeSessionRequest request,
        CancellationToken cancellationToken)
    {
        WorkspaceImportClaudeCodeSessionResponse Reply(string status)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, request?.CliSessionId, null);
        if (_importExternalConversation is null || _sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256)
            || !ValidScopeValue(request.ProjectPath, 4096) || !ValidScopeValue(request.CliSessionId, 64) || !Guid.TryParseExact(request.CliSessionId, "D", out _))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();

        Task<WorkspaceImportClaudeCodeSessionResponse> work;
        lock (_sessionGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed");
            if (CatalogAdmissionBusy || _claudeImportWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceImportClaudeCodeSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _claudeImportWork = work = completion.Task;
            _ = ImportClaudeCodeSessionOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportClaudeCodeSessionOwnedAsync(WorkspaceImportClaudeCodeSessionRequest request,
        TaskCompletionSource<WorkspaceImportClaudeCodeSessionResponse> completion)
    {
        WorkspaceImportClaudeCodeSessionResponse Reply(string status, string? sessionId = null)
            => new(status, _importEpoch, request.ProjectId, request.ProjectPath, request.CliSessionId, sessionId);
        try
        {
            var project = await ExactProjectAsync(request.ProjectId, request.ProjectPath).ConfigureAwait(false);
            if (project is null)
            {
                completion.TrySetResult(Reply("project_missing"));
                return;
            }

            if (ClaudeCodeProviderOf(project.ProjectPath) is not { } provider)
            {
                completion.TrySetResult(Reply("no_provider"));
                return;
            }

            var result = await _importExternalConversation!(project, provider, request.CliSessionId).ConfigureAwait(false);
            var session = result.Session;
            if (result.Status is not ("imported" or "existing"))
            {
                completion.TrySetResult(Reply("not_found"));
                return;
            }

            if (session is null || !ValidScopeValue(session.SessionId, 256))
            {
                completion.TrySetResult(Reply("import_unconfirmed"));
                return;
            }

            completion.TrySetResult(Reply(result.Status == "imported" ? "ok" : "existing", session.SessionId));
        }
        catch (Exception failure)
        {
            // The journal may have been written before the failure: never retry or claim no import.
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(failure, "Claude Code session import failed");
            completion.TrySetResult(Reply("import_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_claudeImportWork, completion.Task)) _claudeImportWork = null; } }
    }

    // The project of the catalog of that identity and folder, not archived and still on disk; null otherwise.
    private async Task<ProjectDescriptor?> ExactProjectAsync(string projectId, string projectPath)
    {
        var project = await _importCatalog!.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        return project is null || project.Archived || project.Id != projectId || project.ProjectPath != projectPath || !Directory.Exists(project.ProjectPath)
            ? null
            : project;
    }

    // The provider of a session imported in the project: the default provider of the project when it shares its
    // conversations, else the first enabled one that does (Claude Code); null when none does.
    private ModelProviderDescriptor? ClaudeCodeProviderOf(string projectPath)
    {
        var enabled = _sessionProviders!.ListProviders().Where(static provider => provider.IsEnabled).ToArray();
        var sharing = enabled.Where(static provider => provider.SharesConversations).ToArray();
        if (sharing.Length == 0) return null;
        return _defaultProvider?.Of(enabled, projectPath) is { SharesConversations: true } preferred
            ? preferred
            : sharing.FirstOrDefault(static provider => provider.IsDefault) ?? sharing[0];
    }
}

/// <param name="ExpectedHostEpoch">The epoch of the host the page knows.</param>
/// <param name="ProjectId">The exact project.</param>
/// <param name="ProjectPath">Its exact folder.</param>
internal sealed record WorkspaceClaudeCodeSessionsRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath);

/// <param name="CliSessionId">The session of the CLI.</param>
/// <param name="Title">Its title, as VS Code or the CLI shows it.</param>
/// <param name="LastChangedAt">When its transcript was last written.</param>
/// <param name="Origin">Where it was started: <c>vscode</c>, <c>terminal</c>, <c>other</c> or <c>unknown</c>.</param>
/// <param name="Held">Whether another Claude process holds it now (open in VS Code, a CLI in a terminal).</param>
internal sealed record WorkspaceClaudeCodeSession(string CliSessionId, string Title, DateTimeOffset LastChangedAt, string Origin, bool Held);

/// <param name="ProviderId">The provider an import gives the session; null when there is none.</param>
/// <param name="Sessions">The sessions, the most recently changed first.</param>
/// <param name="Total">How many sessions there are, also those not returned.</param>
internal sealed record WorkspaceClaudeCodeSessionsResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? ProviderId,
    IReadOnlyList<WorkspaceClaudeCodeSession> Sessions, int Total);

/// <param name="CliSessionId">The session of the CLI to import, as the listing gave it.</param>
internal sealed record WorkspaceImportClaudeCodeSessionRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, string CliSessionId);

/// <param name="SessionId">The session of CodeAlta that continues the conversation, with <c>ok</c> and <c>existing</c>.</param>
internal sealed record WorkspaceImportClaudeCodeSessionResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? CliSessionId,
    string? SessionId);
