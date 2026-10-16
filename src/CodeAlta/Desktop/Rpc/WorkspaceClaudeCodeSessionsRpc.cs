using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The most conversations a page of the listing returns; the reply counts them all.
    internal const int ClaudeCodeSessionsLimit = 50;
    // The furthest a page of the listing starts: the provider looks at the newest transcripts of a folder only.
    private const int ClaudeCodeSessionsMaxOffset = 100_000;

    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, int, int, CancellationToken, Task<ExternalConversationListing>>? _listExternalConversations;
    // Project, provider, conversation and the space of the session.
    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, string, string?, Task<ExternalConversationImportResult>>? _importExternalConversation;
    private readonly Func<ProjectDescriptor, ModelProviderDescriptor, string, CancellationToken, Task<OwnedSharedConversationResult>>? _describeExternalConversation;
    // Opens an address with what the system registers for it. Tests replace it: nothing is opened on their screen.
    private readonly Func<string, bool> _openExternalConversation = DesktopLinks.Open;
    private Task<WorkspaceImportClaudeCodeSessionResponse>? _claudeImportWork;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch, ModelProviderRegistry providers,
        Func<ProjectDescriptor, ModelProviderDescriptor, int, int, CancellationToken, Task<ExternalConversationListing>> listExternalConversations,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, string?, Task<ExternalConversationImportResult>> importExternalConversation,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, CancellationToken, Task<OwnedSharedConversationResult>> describeExternalConversation,
        Func<string, bool> open,
        Func<ProjectDescriptor, ModelProviderDescriptor, string, CancellationToken, Task<ExternalConversationPreviewResult>>? previewExternalConversation = null,
        DesktopDefaultProvider? defaultProvider = null,
        Func<string, string, string, bool, Task<string>>? deleteProject = null)
        : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(listExternalConversations);
        ArgumentNullException.ThrowIfNull(importExternalConversation);
        ArgumentNullException.ThrowIfNull(describeExternalConversation);
        ArgumentNullException.ThrowIfNull(open);
        _sessionProviders = providers;
        _listExternalConversations = listExternalConversations;
        _importExternalConversation = importExternalConversation;
        _describeExternalConversation = describeExternalConversation;
        _openExternalConversation = open;
        _previewExternalConversation = previewExternalConversation;
        _defaultProvider = defaultProvider;
        _sessionSpaces = new SpaceCatalog(catalog);
        _deleteProject = deleteProject;
    }

    /// <summary>
    /// Lists the Claude Code sessions started in VS Code or by the CLI in a terminal in the folder of an exact catalog
    /// project, which no session of CodeAlta continues: the most recently changed first, a page of at most
    /// <see cref="ClaudeCodeSessionsLimit"/>, with how many there are in all. Only reads the transcripts.
    /// </summary>
    /// <param name="request">The exact project, the page and the expected host epoch.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>
    /// <c>ok</c> with the sessions; <c>no_provider</c> when no enabled provider of the space shares its conversations (Claude Code);
    /// or a refusal: <c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>project_missing</c>, <c>read_failed</c>.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("claudeCodeSessions", TimeoutMilliseconds = 120_000)]
    public async Task<WorkspaceClaudeCodeSessionsResponse> ClaudeCodeSessionsAsync(WorkspaceClaudeCodeSessionsRequest request, CancellationToken cancellationToken)
    {
        WorkspaceClaudeCodeSessionsResponse Reply(string status, string? providerId = null, IReadOnlyList<WorkspaceClaudeCodeSession>? sessions = null, int total = 0)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, providerId, request?.Offset ?? 0, sessions ?? [], total);
        if (_listExternalConversations is null || _sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)
            || request.Offset is < 0 or > ClaudeCodeSessionsMaxOffset || request.Count is < 1 or > ClaudeCodeSessionsLimit || !ValidSpace(request.SpaceId))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        var project = await ExactProjectAsync(request.ProjectId, request.ProjectPath).ConfigureAwait(false);
        if (project is null) return Reply("project_missing");
        if (ClaudeCodeProviderOf(project.ProjectPath, request.SpaceId) is not { } provider) return Reply("no_provider");
        try
        {
            var listing = await _listExternalConversations(project, provider, request.Offset, request.Count, cancellationToken).ConfigureAwait(false);
            return Reply("ok", provider.ProviderId.Value,
                [.. listing.Conversations.Take(request.Count).Select(static conversation => new WorkspaceClaudeCodeSession(
                    conversation.ConversationId,
                    conversation.Title.Length <= 256 ? conversation.Title : conversation.Title[..256],
                    conversation.LastChangedAt,
                    conversation.Origin,
                    conversation.IsHeld,
                    conversation.IsHeld ? HolderStatus(conversation.HolderStatus) : null))],
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
    /// already gives that session (<c>existing</c>). The new session belongs to the space the page shows, as a new
    /// session does, with the Claude Code provider of that space.
    /// </summary>
    /// <remarks>An admitted import runs to completion even if the caller stops waiting; close drains it before host disposal.</remarks>
    /// <param name="request">The exact project, the session of the CLI and the expected host epoch.</param>
    /// <param name="cancellationToken">Cancels only the wait after admission, not the import.</param>
    /// <returns>
    /// <c>ok</c> or <c>existing</c> with the session; <c>not_found</c> when the project has no such session to import;
    /// a refusal (<c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>project_missing</c>, <c>space_missing</c>,
    /// <c>no_provider</c> when no enabled provider of the space shares its conversations, <c>provider_outside_space</c>,
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
            || !ValidScopeValue(request.ProjectPath, 4096) || !ValidScopeValue(request.CliSessionId, 64) || !Guid.TryParseExact(request.CliSessionId, "D", out _)
            || !ValidSpace(request.SpaceId))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();

        Task<WorkspaceImportClaudeCodeSessionResponse> work;
        lock (_sessionGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed");
            if (CatalogAdmissionBusy || _claudeImportWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceImportClaudeCodeSessionResponse>(CatalogReplyOptions);
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
        WorkspaceImportClaudeCodeSessionResponse? response = null;
        try
        {
            var project = await ExactProjectAsync(request.ProjectId, request.ProjectPath).ConfigureAwait(false);
            if (project is null)
            {
                response = Reply("project_missing");
                return;
            }

            if (request.SpaceId is { } spaceId && _sessionSpaces is not null
                && await _sessionSpaces.GetAsync(spaceId, CancellationToken.None).ConfigureAwait(false) is null)
            {
                response = Reply("space_missing");
                return;
            }

            if (ClaudeCodeProviderOf(project.ProjectPath, request.SpaceId) is not { } provider)
            {
                response = Reply("no_provider");
                return;
            }

            var result = await _importExternalConversation!(project, provider, request.CliSessionId, request.SpaceId).ConfigureAwait(false);
            var session = result.Session;
            if (result.Status == SessionRuntimeService.ProviderOutsideSpace)
            {
                response = Reply(result.Status);
                return;
            }

            if (result.Status is not ("imported" or "existing"))
            {
                response = Reply("not_found");
                return;
            }

            if (session is null || !ValidScopeValue(session.SessionId, 256))
            {
                response = Reply("import_unconfirmed");
                return;
            }

            response = Reply(result.Status == "imported" ? "ok" : "existing", session.SessionId);
        }
        catch (Exception failure)
        {
            // The journal may have been written before the failure: never retry or claim no import.
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(failure, "Claude Code session import failed");
            response = Reply("import_unconfirmed");
        }
        finally { SettleCatalogWork(ref _claudeImportWork, completion, response); }
    }

    /// <summary>
    /// The conversation of a Claude Code session started in VS Code or a terminal in the folder of an exact catalog
    /// project, which no session of CodeAlta continues: the command that resumes it, the folder it is resumed from, and
    /// another Claude process that holds it now. Only a conversation the listing gives for the project is described.
    /// Read only; nothing is released, since CodeAlta does not hold such a conversation.
    /// </summary>
    /// <param name="request">The exact project, the session of the CLI as the listing gave it, and the expected host epoch.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>
    /// <c>ok</c> with the conversation; <c>not_found</c> when the listing does not give it for the project;
    /// <c>existing</c> when a session of CodeAlta continues it; or a refusal (<c>unconfigured</c>, <c>invalid_scope</c>,
    /// <c>stale_epoch</c>, <c>project_missing</c>, <c>no_provider</c>, <c>closed</c>, <c>unsupported</c>, <c>read_failed</c>).
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("claudeCodeSessionConversation", TimeoutMilliseconds = 60_000)]
    public async Task<WorkspaceClaudeCodeSessionConversationResponse> ClaudeCodeSessionConversationAsync(WorkspaceClaudeCodeSessionRequest request,
        CancellationToken cancellationToken)
        => await DescribeClaudeCodeSessionAsync(request, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the conversation of a Claude Code session started in VS Code or a terminal in the folder of an exact catalog
    /// project in the Claude Code extension of VS Code. The page names the conversation as the listing gave it: the
    /// address is the one the provider built for the transcript it found there, and only an address of that extension
    /// is opened.
    /// </summary>
    /// <param name="request">The exact project, the session of the CLI as the listing gave it, and the expected host epoch.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns><c>opened</c>, <c>open_failed</c>, or why the conversation was not described (see <see cref="ClaudeCodeSessionConversationAsync"/>).</returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("openClaudeCodeSessionInVsCode", TimeoutMilliseconds = 60_000)]
    public async Task<WorkspaceOpenClaudeCodeSessionResponse> OpenClaudeCodeSessionInVsCodeAsync(WorkspaceClaudeCodeSessionRequest request,
        CancellationToken cancellationToken)
    {
        var described = await DescribeClaudeCodeSessionAsync(request, cancellationToken).ConfigureAwait(false);
        // The address names the conversation only: the extension would look for it in the folder of its own account.
        var status = described.Status != "ok" || described.VsCodeUri is not { } address ? described.Status
            : described.ConfigDir is not null ? ClaudeCodeConversationWire.AccountFolder
            : _openExternalConversation(address) ? "opened" : "open_failed";
        return new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, request?.CliSessionId);
    }

    private async Task<WorkspaceClaudeCodeSessionConversationResponse> DescribeClaudeCodeSessionAsync(WorkspaceClaudeCodeSessionRequest? request,
        CancellationToken cancellationToken)
    {
        WorkspaceClaudeCodeSessionConversationResponse Reply(string status)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, request?.CliSessionId, null, null, null, null, OperatingSystem.IsWindows(), null);
        if (_describeExternalConversation is null || _sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)
            || !ClaudeCodeConversationWire.IsConversationId(request.CliSessionId) || !ValidSpace(request.SpaceId))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        var project = await ExactProjectAsync(request.ProjectId, request.ProjectPath).ConfigureAwait(false);
        if (project is null) return Reply("project_missing");
        if (ClaudeCodeProviderOf(project.ProjectPath, request.SpaceId) is not { } provider) return Reply("no_provider");
        OwnedSharedConversationResult result;
        try { result = await _describeExternalConversation(project, provider, request.CliSessionId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn(failure, "A Claude Code session could not be described");
            return Reply("read_failed");
        }

        if (result.Status != "ok") return Reply(result.Status);
        // Only the conversation asked for, of the Claude Code CLI, resumed from the folder of the project.
        if (result.Conversation is not { } conversation || conversation.ConversationId != request.CliSessionId
            || !ClaudeCodeConversationWire.IsClaudeCode(conversation) || conversation.WorkingDirectory != project.ProjectPath)
            return Reply("unsupported");
        return new("ok", _importEpoch, request.ProjectId, request.ProjectPath, request.CliSessionId, conversation.ResumeCommand, conversation.WorkingDirectory,
            conversation.Holder is { } holder ? ConversationHeldService.WireHolder(holder) : null, conversation.ConfigDirectory, OperatingSystem.IsWindows(),
            ClaudeCodeConversationWire.Home()) { VsCodeUri = conversation.EditorUri };
    }

    // What the registry of live sessions says the holder of a conversation does, as a short word (busy, idle, waiting...);
    // anything else is not passed on.
    private static string? HolderStatus(string? status)
        => status is { Length: > 0 and <= 32 } && status.All(static character => character is (>= 'a' and <= 'z') or '_' or '-') ? status : null;

    // The project of the catalog of that identity and folder, not archived and still on disk; null otherwise.
    private async Task<ProjectDescriptor?> ExactProjectAsync(string projectId, string projectPath)
    {
        var project = await _importCatalog!.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        return project is null || project.Archived || project.Id != projectId || project.ProjectPath != projectPath || !Directory.Exists(project.ProjectPath)
            ? null
            : project;
    }

    // The provider of a session imported in the project, among the enabled providers of the space the page shows (the
    // default space for none), as for a new session: the default provider of the project in that space when it shares
    // its conversations, else the first one there that does (Claude Code); null when none does.
    private ModelProviderDescriptor? ClaudeCodeProviderOf(string projectPath, string? spaceId)
    {
        IReadOnlyList<ModelProviderDescriptor> enabled = [.. _sessionProviders!.ListProviders().Where(static provider => provider.IsEnabled)];
        if (_defaultProvider is not null) enabled = _defaultProvider.InSpace(enabled, spaceId);
        var sharing = enabled.Where(static provider => provider.SharesConversations).ToArray();
        if (sharing.Length == 0) return null;
        return _defaultProvider is not null && DesktopDefaultProvider.Pick(enabled, _defaultProvider.Configured(projectPath)) is { SharesConversations: true } preferred
            ? preferred
            : sharing.FirstOrDefault(static provider => provider.IsDefault) ?? sharing[0];
    }

    // A space the page names: an identifier, or none.
    private static bool ValidSpace(string? spaceId) => spaceId is null || SpaceDescriptor.IsValidId(spaceId);
}

/// <param name="ExpectedHostEpoch">The epoch of the host the page knows.</param>
/// <param name="ProjectId">The exact project.</param>
/// <param name="ProjectPath">Its exact folder.</param>
/// <param name="Offset">How many sessions, the most recently changed first, come before the page.</param>
/// <param name="Count">The most sessions of the page, from 1 to 50.</param>
/// <param name="SpaceId">The space the page shows: the sessions are those of a provider of that space; null for the default space.</param>
internal sealed record WorkspaceClaudeCodeSessionsRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, int Offset, int Count,
    string? SpaceId = null);

/// <param name="CliSessionId">The session of the CLI.</param>
/// <param name="Title">Its title, as VS Code or the CLI shows it.</param>
/// <param name="LastChangedAt">When its transcript was last written.</param>
/// <param name="Origin">Where it was started: <c>vscode</c>, <c>terminal</c>, <c>other</c> or <c>unknown</c>.</param>
/// <param name="Held">Whether another Claude process holds it now (open in VS Code, a CLI in a terminal).</param>
/// <param name="HolderStatus">
/// What that process says it does (<c>busy</c>, <c>idle</c>, <c>waiting</c>, ...), when it is held and says it: a session
/// merely open in a VS Code panel is <c>idle</c>.
/// </param>
internal sealed record WorkspaceClaudeCodeSession(string CliSessionId, string Title, DateTimeOffset LastChangedAt, string Origin, bool Held, string? HolderStatus);

/// <param name="ProviderId">The provider an import gives the session; null when there is none.</param>
/// <param name="Offset">How many sessions come before the page, as asked.</param>
/// <param name="Sessions">The sessions of the page, the most recently changed first.</param>
/// <param name="Total">How many sessions there are, also those not returned.</param>
internal sealed record WorkspaceClaudeCodeSessionsResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? ProviderId,
    int Offset, IReadOnlyList<WorkspaceClaudeCodeSession> Sessions, int Total);

/// <param name="ExpectedHostEpoch">The epoch of the host the page knows.</param>
/// <param name="ProjectId">The exact project.</param>
/// <param name="ProjectPath">Its exact folder.</param>
/// <param name="CliSessionId">The session of the CLI, as the listing gave it.</param>
/// <param name="SpaceId">The space the page shows, whose provider describes the session; null for the default space.</param>
internal sealed record WorkspaceClaudeCodeSessionRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, string CliSessionId,
    string? SpaceId = null);

/// <summary>
/// The conversation of a Claude Code session started elsewhere. <paramref name="Status"/> is <c>ok</c> or why it is not
/// described; <paramref name="Held"/> is another Claude process that holds it now.
/// </summary>
/// <paramref name="ConfigDir"/> is the account folder of the provider (<c>CLAUDE_CONFIG_DIR</c>) the command is run with,
/// or null; <paramref name="Windows"/> and <paramref name="Home"/> say how the page writes it in the command.
internal sealed record WorkspaceClaudeCodeSessionConversationResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    string? CliSessionId, string? ResumeCommand, string? WorkingDirectory, ConversationHeldHolder? Held, string? ConfigDir, bool Windows, string? Home)
{
    // Kept by the host for openClaudeCodeSessionInVsCode; the page never sends nor receives an address.
    [JsonIgnore]
    internal string? VsCodeUri { get; init; }
}

/// <summary>
/// <paramref name="Status"/> is <c>opened</c>, <c>open_failed</c>, <c>account_folder</c> (the provider has an account
/// folder of its own, which the address of VS Code cannot name), or why the conversation was not described.
/// </summary>
internal sealed record WorkspaceOpenClaudeCodeSessionResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? CliSessionId);

/// <summary>What the page may be told of a conversation of the Claude Code CLI, and what the host may open of it.</summary>
internal static class ClaudeCodeConversationWire
{
    private const string VsCodeUriPrefix = "vscode://anthropic.claude-code/open?session=";

    /// <summary>
    /// Why a conversation is not opened in VS Code: its provider runs the CLI with an account folder of its own
    /// (<c>CLAUDE_CONFIG_DIR</c>), and the address of the extension names a session only, which the extension looks for
    /// in the folder of its own account.
    /// </summary>
    public const string AccountFolder = "account_folder";

    /// <summary>The home folder, which the page writes as <c>$HOME</c> in a command, as in the sign-in command of a provider.</summary>
    public static string? Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : null;

    /// <summary>Whether a text is an identifier the CLI gives a conversation: a GUID in its <c>D</c> format.</summary>
    public static bool IsConversationId(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// Whether a conversation is one of the Claude Code CLI: a CLI identifier, whose address is the one of the VS Code
    /// extension and whose command is <c>claude --resume</c>, for that identifier, resumed from a folder, with no account
    /// folder or one that is a full path.
    /// </summary>
    public static bool IsClaudeCode(OwnedSharedConversation conversation)
        => IsConversationId(conversation.ConversationId)
            && conversation.EditorUri == VsCodeUriPrefix + conversation.ConversationId
            && conversation.ResumeCommand == "claude --resume " + conversation.ConversationId
            && conversation.WorkingDirectory is { Length: > 0 and <= 4096 }
            // The account folder is put in a command, quoted by the page: a full path only.
            && (conversation.ConfigDirectory is null
                || conversation.ConfigDirectory is { Length: > 0 and <= 4096 } folder && Path.IsPathFullyQualified(folder) && !folder.Any(char.IsControl));
}

/// <param name="CliSessionId">The session of the CLI to import, as the listing gave it.</param>
/// <param name="SpaceId">
/// The space the page shows: the session belongs to it for its whole life, with a provider of that space; null records
/// none.
/// </param>
internal sealed record WorkspaceImportClaudeCodeSessionRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, string CliSessionId,
    string? SpaceId = null);

/// <param name="SessionId">The session of CodeAlta that continues the conversation, with <c>ok</c> and <c>existing</c>.</param>
internal sealed record WorkspaceImportClaudeCodeSessionResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? CliSessionId,
    string? SessionId);
