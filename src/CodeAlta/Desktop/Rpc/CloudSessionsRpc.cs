using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Terminals;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The cloud sessions of Claude Code, for the page: what a project and a provider allow before a start, the start itself
/// (in a terminal of the window, which is shown when Claude Code asks something), the messages sent to a session that
/// runs in the cloud, and its return to this computer, into the project folder or a new worktree. Only the documented
/// commands of Claude Code run: nothing is read from the cloud service.
/// </summary>
[NeoRpcService("cloudSessions", Version = 1)]
internal sealed class CloudSessionsService
{
    // The starts the page can read again by their request: the last ones.
    private const int KeptStarts = 32;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    private readonly OwnedSessionCommandService? _commands;
    private readonly ProjectCatalog? _projects;
    private readonly ModelProviderRegistry? _providers;
    private readonly Func<IReadOnlyList<ModelProviderStateSnapshot>>? _providerStates;
    private readonly DesktopTerminals? _terminals;
    private readonly GitWorktreeService? _worktrees;
    private readonly string? _epoch;
    private readonly ConcurrentDictionary<string, Start> _starts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _startOrder = new();
    private readonly ConcurrentDictionary<string, byte> _continuing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an unavailable service, for a window without an owned host.</summary>
    internal CloudSessionsService() { }

    /// <summary>Creates the service of an owned host.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal CloudSessionsService(OwnedSessionCommandService commands, ProjectCatalog projects, ModelProviderRegistry providers,
        Func<IReadOnlyList<ModelProviderStateSnapshot>> providerStates, DesktopTerminals terminals, GitWorktreeService worktrees, string epoch)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(providerStates);
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_commands, _projects, _providers, _providerStates, _terminals, _worktrees, _epoch) = (commands, projects, providers, providerStates, terminals, worktrees, epoch);
    }

    /// <summary>Gets or initializes what makes the terminal a start runs in; tests replace it.</summary>
    internal Func<string?, IAgentInteractiveTerminal>? CreateTerminal { get; init; }

    /// <summary>
    /// What the project and the provider say before a start: the repository and its branch, what is not on its remote,
    /// the tracked files changed, whether the remote is on GitHub, and whether the account of the provider runs cloud
    /// sessions, as its last probe said.
    /// </summary>
    [NeoRpcMethod("readiness")]
    public async Task<CloudReadinessResponse> ReadinessAsync(CloudReadinessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudReadinessResponse.Refused(refused);
        var project = await FindProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return CloudReadinessResponse.Refused("project_missing");
        var folder = project.ProjectPath;
        var repository = (await GitAsync(folder, ["rev-parse", "--is-inside-work-tree"], cancellationToken).ConfigureAwait(false))?.Trim() == "true";
        string? branch = null;
        int? unpushed = null;
        var changed = 0;
        var gitHub = false;
        if (repository)
        {
            branch = (await GitAsync(folder, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false))?.Trim() is { Length: > 0 and <= 200 } head && head != "HEAD" ? head : null;
            unpushed = int.TryParse((await GitAsync(folder, ["rev-list", "--count", "@{u}..HEAD"], cancellationToken).ConfigureAwait(false))?.Trim(), out var ahead) ? ahead : null;
            changed = (await GitAsync(folder, ["status", "--porcelain", "--untracked-files=no"], cancellationToken).ConfigureAwait(false))?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length ?? 0;
            gitHub = (await GitAsync(folder, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false))?.Contains("github.com", StringComparison.OrdinalIgnoreCase) == true;
        }

        return new CloudReadinessResponse("ok", _epoch, repository, branch, unpushed, changed, gitHub, AccountOf(request.ProviderKey), OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Starts a cloud session of a project and records it as a session: it ends with the session, or why there is none.
    /// While it runs, <c>startState</c> says where it is; a lost answer is read again there by its request.
    /// </summary>
    [NeoRpcMethod("start")]
    public async Task<CloudStartResponse> StartAsync(CloudStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudStartResponse.Of(refused, request.RequestId);
        if (!ValidRequestId(request.RequestId) || string.IsNullOrWhiteSpace(request.Task) || request.Task.Length > 16 * 1024
            || request.Title is { Length: > 256 }) return CloudStartResponse.Of("invalid_request", request.RequestId);
        var start = new Start();
        if (!_starts.TryAdd(request.RequestId, start)) return await _starts[request.RequestId].Completion.Task.ConfigureAwait(false);
        Keep(request.RequestId);
        try
        {
            // Once admitted the start is the host's: a caller that stops waiting does not leave it without an outcome.
            var project = await FindProjectAsync(request.ProjectId, CancellationToken.None).ConfigureAwait(false);
            if (project is null) return start.End(CloudStartResponse.Of("project_missing", request.RequestId));
            if (FindProvider(request.ProviderKey) is not { } provider) return start.End(CloudStartResponse.Of("provider_unavailable", request.RequestId));
            var terminal = (CreateTerminal ?? (projectId => new DesktopInteractiveTerminal(_terminals!, projectId)))(project.Id);
            // The start goes on when the page stops waiting: only cancelStart ends it.
            var result = await _commands!.CreateCloudSessionAsync(project, provider, request.SpaceId, request.Title, request.Task, terminal,
                () => start.Step = "asking", start.Cancel.Token).ConfigureAwait(false);
            return start.End(new CloudStartResponse(result.Status, _epoch, request.RequestId, result.Session?.SessionId, result.CloudSessionId, result.Url,
                result.Message, result.TerminalId, null));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return start.End(CloudStartResponse.Of("failed", request.RequestId) with { Message = exception.Message });
        }
        finally
        {
            // Every start that was admitted ends with an outcome, whatever stopped it: startState then says it.
            start.End(CloudStartResponse.Of("failed", request.RequestId));
        }
    }

    /// <summary>Where a start is: <c>running</c> with its step (<c>starting</c>, <c>asking</c>), its outcome once it ended, or <c>unknown</c>.</summary>
    [NeoRpcMethod("startState")]
    public CloudStartResponse StartState(CloudRequestRef request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudStartResponse.Of(refused, request.RequestId);
        if (!_starts.TryGetValue(request.RequestId, out var start)) return CloudStartResponse.Of("unknown", request.RequestId);
        return start.Completion.Task.IsCompletedSuccessfully ? start.Completion.Task.Result : CloudStartResponse.Of("running", request.RequestId) with { Step = start.Step };
    }

    /// <summary>Ends a start: Claude Code is ended. A cloud session it created before is kept and recorded.</summary>
    [NeoRpcMethod("cancelStart")]
    public CloudStatusResponse CancelStart(CloudRequestRef request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!_starts.TryGetValue(request.RequestId, out var start)) return new("unknown", null);
        start.Cancel.Cancel();
        return new("ok", null);
    }

    /// <summary>Records a cloud session started elsewhere from the link the user pasted, to bring it here next.</summary>
    [NeoRpcMethod("bringHere")]
    public async Task<CloudStartResponse> BringHereAsync(CloudBringRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudStartResponse.Of(refused, null);
        if (string.IsNullOrWhiteSpace(request.Link) || request.Link.Length > 2048) return CloudStartResponse.Of("invalid_link", null);
        var project = await FindProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return CloudStartResponse.Of("project_missing", null);
        if (FindProvider(request.ProviderKey) is not { } provider) return CloudStartResponse.Of("provider_unavailable", null);
        var result = await _commands!.AdoptCloudSessionAsync(project, provider, request.SpaceId, null, request.Link).ConfigureAwait(false);
        return new CloudStartResponse(result.Status, _epoch, null, result.Session?.SessionId, result.CloudSessionId, result.Url, result.Message, null, null);
    }

    /// <summary>What a session holds of the cloud session it runs as, or ran as.</summary>
    [NeoRpcMethod("details")]
    public async Task<CloudSessionDetailsResponse> DetailsAsync(CloudSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudSessionDetailsResponse.Refused(refused);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128) return CloudSessionDetailsResponse.Refused("invalid_request");
        var details = await _commands!.GetCloudSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        return details is null
            ? CloudSessionDetailsResponse.Refused("not_cloud")
            : new CloudSessionDetailsResponse("ok", details.Id, details.Url, details.Title, details.Task, details.Branch, details.StartedAt, details.Note,
                [.. details.Sent.Select(static message => new CloudSentMessage(message.Text, message.At))], details.Continuing, details.ContinuedAt, details.InTerminal);
    }

    /// <summary>Sends a message to the cloud session a session runs as.</summary>
    [NeoRpcMethod("send")]
    public async Task<CloudStatusResponse> SendAsync(CloudSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 64 * 1024)
            return new("invalid_request", null);
        var result = await _commands!.SendToCloudSessionAsync(request.SessionId, request.Text).ConfigureAwait(false);
        return new(result.Status, result.Message);
    }

    /// <summary>
    /// Brings the cloud session a session runs as back to this computer: into the folder of the session, or a new worktree
    /// made for it; or, in a terminal, gives the command to type there, then reads what it wrote.
    /// </summary>
    [NeoRpcMethod("continueHere")]
    public async Task<CloudContinueResponse> ContinueHereAsync(CloudContinueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return CloudContinueResponse.Of(refused);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128) return CloudContinueResponse.Of("invalid_request");
        // One return of a session at a time from this window: a second one would make a second worktree.
        if (!_continuing.TryAdd(request.SessionId, 0)) return CloudContinueResponse.Of("busy");
        try
        {
            string? worktree = null;
            string? branch = null;
            ProjectDescriptor? project = null;
            if (request.Worktree)
            {
                var details = await _commands!.GetCloudSessionAsync(request.SessionId, CancellationToken.None).ConfigureAwait(false);
                if (details is null || details.ContinuedAt is not null) return CloudContinueResponse.Of("not_cloud");
                // A return already reserved (in a terminal, asked again) keeps the folder it reserved; a session already in
                // a worktree (an earlier return failed after it went there) is brought back there.
                if (!details.Continuing && details.Worktree is null)
                {
                    project = await FindProjectAsync(details.ProjectId, CancellationToken.None).ConfigureAwait(false);
                    if (project is null) return CloudContinueResponse.Of("project_missing");
                    var created = await _worktrees!.CreateAsync(project, null, null, CancellationToken.None).ConfigureAwait(false);
                    if (!created.Succeeded || created.Folder is null) return CloudContinueResponse.Of("worktree_failed") with { Message = created.Message };
                    (worktree, branch) = (created.Folder, created.Branch);
                }
            }

            var result = await _commands!.ContinueCloudSessionHereAsync(request.SessionId, worktree, request.FromTerminal).ConfigureAwait(false);
            if (result.Status == "ok" && worktree is not null && branch is not null)
            {
                // Claude Code checked out the branch of the cloud session there: the branch made for the worktree is not used.
                var head = (await GitAsync(worktree, ["rev-parse", "--abbrev-ref", "HEAD"], CancellationToken.None).ConfigureAwait(false))?.Trim();
                if (head is { Length: > 0 } && head != branch) await GitAsync(worktree, ["branch", "-d", branch], CancellationToken.None).ConfigureAwait(false);
            }
            else if (result.Status is not ("ok" or "terminal") && worktree is not null && project is not null)
            {
                // Refused before the session went there: the worktree made for it is not left behind.
                var after = await _commands.GetCloudSessionAsync(request.SessionId, CancellationToken.None).ConfigureAwait(false);
                if (after?.Worktree is null || !string.Equals(Path.GetFullPath(after.Worktree), Path.GetFullPath(worktree), StringComparison.OrdinalIgnoreCase))
                    await _worktrees!.RemoveAsync(project.ProjectPath, worktree, force: false, deleteBranch: true, CancellationToken.None).ConfigureAwait(false);
            }

            return new(result.Status, result.Message, result.ChangedFiles, result.Command, result.ConfigDirectory, result.Folder, OperatingSystem.IsWindows(),
                ClaudeCodeConversationWire.Home()) { Executable = result.Executable };
        }
        finally
        {
            _continuing.TryRemove(request.SessionId, out _);
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _commands is null ? "unconfigured" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    private async Task<ProjectDescriptor?> FindProjectAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 256) return null;
        var projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project => !project.Archived && string.Equals(project.Id, projectId, StringComparison.Ordinal));
    }

    private ModelProviderDescriptor? FindProvider(string? providerKey)
        => !string.IsNullOrWhiteSpace(providerKey) && providerKey.Length <= 256 && _providers!.TryGetProvider(new ModelProviderId(providerKey), out var provider)
           && provider.SupportsCloudSessions ? provider : null;

    // What the last probe of the provider said of its account: subscription, not_subscription, or unknown.
    private string AccountOf(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return "unknown";
        var state = _providerStates!().FirstOrDefault(snapshot => string.Equals(snapshot.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase));
        return state?.Capabilities.FirstOrDefault(static capability => capability.Id == "cloud-sessions") is { } cloud
            ? cloud.Supported ? "subscription" : "not_subscription"
            : "unknown";
    }

    private static async Task<string?> GitAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var run = await GitWorktreeService.RunGitAsync(folder, arguments, GitTimeout, cancellationToken).ConfigureAwait(false);
            return run.ExitCode == 0 ? run.Output : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool ValidRequestId(string? value)
        => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    private void Keep(string requestId)
    {
        _startOrder.Enqueue(requestId);
        while (_startOrder.Count > KeptStarts && _startOrder.TryDequeue(out var oldest))
        {
            if (_starts.TryGetValue(oldest, out var old) && old.Completion.Task.IsCompleted) _starts.TryRemove(oldest, out _);
        }
    }

    // A start the page follows by its request.
    private sealed class Start
    {
        public CancellationTokenSource Cancel { get; } = new();

        public TaskCompletionSource<CloudStartResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile string Step = "starting";

        public CloudStartResponse End(CloudStartResponse response)
        {
            Completion.TrySetResult(response);
            return response;
        }
    }
}

/// <summary>Asks what a project and a provider allow before a cloud session starts.</summary>
internal sealed record CloudReadinessRequest(string ExpectedEpoch, string ProjectId, string? ProviderKey);

/// <summary>
/// What a project and a provider allow before a cloud session starts. <paramref name="Account"/> is <c>subscription</c>,
/// <c>not_subscription</c> or <c>unknown</c>; <paramref name="Unpushed"/> is null without an upstream branch.
/// </summary>
internal sealed record CloudReadinessResponse(string Status, string? Epoch, bool Repository, string? Branch, int? Unpushed, int Changed, bool GitHub, string Account,
    bool Windows)
{
    internal static CloudReadinessResponse Refused(string status) => new(status, null, false, null, null, 0, false, "unknown", OperatingSystem.IsWindows());
}

/// <summary>Starts a cloud session of a project with a provider; <paramref name="RequestId"/> is a GUID the page gives it.</summary>
internal sealed record CloudStartRequest(string ExpectedEpoch, string RequestId, string ProjectId, string ProviderKey, string? SpaceId, string? Title, string Task);

/// <summary>Names a start by its request.</summary>
internal sealed record CloudRequestRef(string ExpectedEpoch, string RequestId);

/// <summary>Records a cloud session started elsewhere from its link.</summary>
internal sealed record CloudBringRequest(string ExpectedEpoch, string ProjectId, string ProviderKey, string? SpaceId, string Link);

/// <summary>
/// How a start went, or where it is: <c>created</c>, <c>existing</c>, <c>unrecorded</c>, <c>failed</c>, <c>canceled</c>,
/// <c>needs_claude_ai</c>, <c>terminal_unavailable</c>, <c>running</c> (with <paramref name="Step"/>), <c>unknown</c>, or a refusal.
/// </summary>
internal sealed record CloudStartResponse(string Status, string? Epoch, string? RequestId, string? SessionId, string? CloudSessionId, string? Url, string? Message,
    string? TerminalId, string? Step)
{
    internal static CloudStartResponse Of(string status, string? requestId) => new(status, null, requestId, null, null, null, null, null, null);
}

/// <summary>Names a session.</summary>
internal sealed record CloudSessionRequest(string ExpectedEpoch, string SessionId);

/// <summary>A message sent to a cloud session from CodeAlta.</summary>
internal sealed record CloudSentMessage(string Text, DateTimeOffset At);

/// <summary>
/// What a session holds of the cloud session it runs as, or ran as; <paramref name="InTerminal"/> when the return in
/// progress waits on the user, in a terminal.
/// </summary>
internal sealed record CloudSessionDetailsResponse(string Status, string? Id, string? Url, string? Title, string? Task, string? Branch, DateTimeOffset? StartedAt,
    string? Note, CloudSentMessage[] Sent, bool Continuing, DateTimeOffset? ContinuedAt, bool InTerminal)
{
    internal static CloudSessionDetailsResponse Refused(string status) => new(status, null, null, null, null, null, null, null, [], false, null, false);
}

/// <summary>Sends a message to the cloud session of a session.</summary>
internal sealed record CloudSendRequest(string ExpectedEpoch, string SessionId, string Text);

/// <summary>How a request went, with what Claude Code said.</summary>
internal sealed record CloudStatusResponse(string Status, string? Message);

/// <summary>Brings a cloud session back: into a new worktree or the folder of the session, and in a terminal or not.</summary>
internal sealed record CloudContinueRequest(string ExpectedEpoch, string SessionId, bool Worktree, bool FromTerminal);

/// <summary>
/// How a return went; for one in a terminal, the command to type there and the account folder it runs with, written for
/// the shell of the system (<paramref name="Windows"/>) with the home folder of the user (<paramref name="Home"/>).
/// </summary>
internal sealed record CloudContinueResponse(string Status, string? Message, int ChangedFiles, string? Command, string? ConfigDirectory, string? Folder, bool Windows,
    string? Home)
{
    /// <summary>For a bring-back in a terminal: the executable the provider runs, typed in the place of <c>claude</c>, or null.</summary>
    public string? Executable { get; init; }

    internal static CloudContinueResponse Of(string status) => new(status, null, 0, null, null, null, OperatingSystem.IsWindows(), null);
}