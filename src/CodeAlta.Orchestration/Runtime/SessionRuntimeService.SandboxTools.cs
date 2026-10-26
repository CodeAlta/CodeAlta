using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime.Sandbox;
using CodeAlta.Sandbox;
using CodeAlta.Sandbox.Mxc;
using CodeAlta.Sandbox.Network;
using CodeAlta.Sandbox.Worker;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The ports of CodeAlta's own MCP servers (the window and the developer instance): no profile reaches them.
    private static readonly IReadOnlySet<int> ReservedSandboxPorts = System.Collections.Frozen.FrozenSet.ToFrozenSet([2582, 2583]);

    private readonly Lock _sandboxGate = new();
    private SessionSandboxWorkers? _sandboxWorkers;

    /// <summary>
    /// Gets or initializes the sandbox the sessions run in when they are sandboxed; null, the default, for the one of
    /// this computer (<see cref="MxcSandboxBackend"/>). Tests give another.
    /// </summary>
    public ISandboxBackend? SandboxBackend { get; init; }

    /// <summary>
    /// Gets or initializes the command line of the sandbox worker; null, the default, for this executable with
    /// <see cref="SandboxWorker.Option"/>. Tests, which run in a test host, give the one of an executable of CodeAlta.
    /// </summary>
    public string? SandboxWorkerCommandLine { get; init; }

    /// <summary>Gets or initializes the folder where the sandboxes keep their data; null for <see cref="SandboxHostDiscovery.DefaultDataRoot"/>.</summary>
    public string? SandboxDataRoot { get; init; }

    internal SessionSandboxWorkers SandboxWorkers
    {
        get
        {
            lock (_sandboxGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _sandboxWorkers ??= new SessionSandboxWorkers(SandboxBackend ?? new MxcSandboxBackend(), WorkerCommandLine,
                    () => SandboxHostDiscovery.Current(SandboxDataRoot ?? SandboxHostDiscovery.DefaultDataRoot(), [_catalogOptions.GlobalRoot, _catalogOptions.StateRoot]),
                    ReservedSandboxPorts);
            }
        }
    }

    /// <summary>Gets whether the sandbox can run on this computer, and what it can do here.</summary>
    /// <returns>The availability.</returns>
    public SandboxAvailability GetSandboxAvailability() => SandboxWorkers.Backend.GetAvailability();

    /// <summary>
    /// Gets what starts the background commands of a session: for a sandboxed session, its worker; null for a
    /// session that is not sandboxed, whose commands start in the shell of the host.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The starter, or null; what gives the worker back when the starter starts no command (a job that is refused), to
    /// be disposed then; and why no command can start, when the sandbox cannot run.
    /// </returns>
    public async Task<(ShellCommandStarter? Starter, IDisposable? Unused, string? Refusal)> GetJobStarterAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var sandbox = await GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!sandbox.IsSandboxed) return (null, null, null);
        if (sandbox.Profile is not { } profile) return (null, null, SandboxProblem(sandbox));
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
            return (null, null, "The session is not attached: its sandbox starts with its next turn.");
        var sessionFolder = entry.WorktreeDirectory ?? entry.WorkingDirectory;
        var start = SandboxStartOf(sessionId, entry.ProviderKey, profile, sessionFolder, entry.ProjectRoots, entry.ProcessEnvironment, commandGuard: false);
        SessionSandboxWorkers.Lease lease;
        try
        {
            lease = await SandboxWorkers.AcquireAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException unavailable)
        {
            return (null, null, unavailable.Message);
        }

        // One lease per starter: the first command takes it and keeps the worker until it ends.
        var taken = 0;
        // The variables of the session are those of its worker already.
        return ((command, folder, sessionVariables, onOutput) =>
        {
            var held = Interlocked.Exchange(ref taken, 1) == 0 ? lease : null;
            var process = lease.Client.StartProcess(command, JobFolder(folder, sessionFolder, lease.Policy.WorkingDirectory), onOutput);
            if (held is not null) process.Completion.ContinueWith(_ => held.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new SandboxedShellCommand(process);
        }, new Unused(() =>
        {
            // A job that was refused before it started any command gives the worker back.
            if (Interlocked.Exchange(ref taken, 1) == 0) lease.Dispose();
        }), null);
    }

    /// <summary>
    /// The folder a background command of a sandboxed session starts in: the folder of the session is the one its
    /// sandbox gives it, which differs when the session works in a folder of secrets (a global session, in the folder of
    /// CodeAlta, which the sandbox does not grant).
    /// </summary>
    internal static string JobFolder(string folder, string? sessionFolder, string sandboxFolder)
    {
        if (sessionFolder is null) return folder;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionFolder)), comparison)
            ? sandboxFolder
            : folder;
    }

    private sealed class Unused(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>
    /// Gets the MCP servers a sandboxed session may use, as its profile lists them; null for a session that is not
    /// sandboxed. A sandboxed session whose profile cannot be used may use none.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The servers, or null.</returns>
    public async ValueTask<IReadOnlyList<string>?> GetSandboxMcpServersAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var sandbox = await GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return sandbox.IsSandboxed ? sandbox.Profile?.McpServers ?? [] : null;
    }

    // The note a run carries about the sandbox of its session: what it allows, when the agent was told something else, or
    // nothing yet while it is sandboxed. Null when there is nothing to tell. The instructions stay the same: their text
    // is what the provider keeps in its cache.
    private async Task<(string Text, string Noted)?> SandboxNoteForAsync(RuntimeSessionEntry entry, CancellationToken cancellationToken)
    {
        var sandbox = await GetSessionSandboxAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);
        var scratchpad = sandbox.IsSandboxed && !string.IsNullOrWhiteSpace(ScratchpadRoot) ? Path.Combine(ScratchpadRoot, "sandbox", entry.SessionId) : null;
        var note = SandboxOutsideCommands.Note(sandbox, scratchpad);
        return entry.SandboxNoted is { } told ? told == note.Noted ? null : note : note.Noted == "off" ? null : note;
    }

    // What the agent was told is kept with the session, so that a session that comes back is not told twice.
    private async Task RecordSandboxNotedAsync(SessionViewDescriptor session, RuntimeSessionEntry entry, string noted)
    {
        entry.SandboxNoted = noted;
        try
        {
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (localState is not null && localState.SandboxNoted != noted)
                {
                    localState.SandboxNoted = noted;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            // The next run of a session that comes back tells it again, which is harmless.
        }
    }

    // The router of the built-in tools of a session, which reads its sandbox at each call.
    internal IAgentBuiltInToolRouter CreateSandboxToolRouter(string sessionId, string providerId, IReadOnlyList<string> projectRoots)
        => new SandboxToolRouter(this, sessionId, providerId, projectRoots);

    private string WorkerCommandLine()
        => SandboxWorkerCommandLine ?? $"\"{Environment.ProcessPath}\" {SandboxWorker.Option}";

    // The sandbox of a session runs only what it can hold; this says why it cannot run, for the user and the agent.
    private static string SandboxProblem(SessionSandbox sandbox)
        => $"This session is sandboxed (profile '{sandbox.Choice.Profile}'), and its sandbox cannot run: {sandbox.Problem}";

    private SessionSandboxStart SandboxStartOf(string sessionId, string providerId, ResolvedSandboxProfile profile, string workingDirectory,
        IReadOnlyList<string> projectRoots, AgentProcessEnvironment? environment, bool commandGuard)
    {
        // A sandboxed session writes a scratchpad of its own: the shared one would let it leave instructions for others.
        string? scratchpad = string.IsNullOrWhiteSpace(ScratchpadRoot) ? null : Path.Combine(ScratchpadRoot, "sandbox", sessionId);
        // The session writes its working directory, never its project folder besides: a session in a worktree leaves
        // the checkout of the user as it is. The project folders are the repositories whose git folder its worktree
        // may write.
        var folders = new SandboxSessionFolders
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            Repositories = [.. projectRoots.Where(static root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath)],
            Scratchpad = scratchpad,
        };
        return new SessionSandboxStart(sessionId, providerId, profile, folders, environment?.Variables ?? [], commandGuard);
    }

    // A session that goes away leaves no worker behind; none is created for a runtime that never sandboxed anything.
    private void StopUnusedSandboxWorker(string sessionId)
    {
        SessionSandboxWorkers? workers;
        lock (_sandboxGate) workers = _sandboxWorkers;
        if (workers is not null) _ = workers.StopIfUnusedAsync(sessionId);
    }

    private async ValueTask DisposeSandboxWorkersAsync()
    {
        SessionSandboxWorkers? workers;
        lock (_sandboxGate)
        {
            workers = _sandboxWorkers;
            _sandboxWorkers = null;
        }

        if (workers is not null) await workers.DisposeAsync().ConfigureAwait(false);
    }

    // The type of a provider, as its descriptor says; null for a provider that is not registered.
    internal string? ProviderTypeOf(string providerKey)
        => _agentHub.SelectionProviders.FirstOrDefault(provider => string.Equals(provider.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase))?.ProviderType;

    // Starts the process of an agent that runs its own tools in the sandbox of its session.
    private sealed class AgentProcessHost(SessionRuntimeService runtime, string sessionId, string providerId, ResolvedSandboxProfile profile, IReadOnlyList<string> projectRoots)
        : IAgentProcessHost
    {
        // What the container of the process is made of: another profile is another process.
        public string Key { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\u001F',
            [profile.Name, profile.Network.ToString(), profile.Outside.ToString(), .. profile.Hosts, "|", .. profile.Write, "|", .. profile.Read, "|", .. profile.Deny, "|",
             .. profile.LocalPorts.Select(static port => port.ToString(System.Globalization.CultureInfo.InvariantCulture)), "|", .. profile.Env, "|", .. profile.McpServers]))), 0, 8);

        public async Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(launch);
            var folder = launch.WorkingDirectory ?? projectRoots.FirstOrDefault(static root => !string.IsNullOrWhiteSpace(root)) ?? Environment.CurrentDirectory;
            var start = runtime.SandboxStartOf(sessionId, providerId, profile, folder, projectRoots, environment: null, commandGuard: false);
            try
            {
                return await runtime.SandboxWorkers.StartAgentProcessAsync(start, launch, cancellationToken).ConfigureAwait(false);
            }
            catch (SandboxUnavailableException unavailable)
            {
                throw new InvalidOperationException($"The sandbox of this session (profile '{profile.Name}') did not start the agent: {unavailable.Message}", unavailable);
            }
        }
    }

    // A background command of the job service, run by the worker of the session.
    private sealed class SandboxedShellCommand(SandboxedProcess process) : IShellCommandProcess
    {
        public int? ProcessId => process.ProcessId;

        public Task<int> Completion => process.Completion;

        public void Kill() => process.Kill();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Routes the built-in tools of a session: those that read, write and execute on the computer go to the worker of
    /// the sandbox when the session is sandboxed at the moment of the call, and run here otherwise; <c>webget</c> runs
    /// here, with the network of the sandbox.
    /// </summary>
    private sealed class SandboxToolRouter(SessionRuntimeService runtime, string sessionId, string providerId, IReadOnlyList<string> projectRoots) : IAgentBuiltInToolRouter
    {
        private const string WebGet = "webget";
        private const string ShellCommand = "shell_command";

        // The profile of the session when its run started, for the parameters its tools get in that run.
        private ResolvedSandboxProfile? _runProfile;

        // What starts the process of a provider that runs its own tools, for the run admitted last.
        private IAgentProcessHost? _processHost;

        public IAgentProcessHost? ProcessHost => Volatile.Read(ref _processHost);

        public IReadOnlyList<AgentToolDefinition> Route(AgentBuiltInToolOptions options, IReadOnlyList<AgentToolDefinition> tools)
        {
            var profile = Volatile.Read(ref _runProfile);
            return [.. tools.Select(tool => tool.Spec.Name == ShellCommand
                ? tool with
                {
                    Spec = profile is { Outside: SandboxOutside.Ask } ? SandboxOutsideCommands.WithParameter(tool.Spec) : tool.Spec,
                    Handler = (invocation, cancellationToken) => ShellCommandAsync(tool, options, invocation, cancellationToken),
                }
                : SandboxWorker.Tools.Contains(tool.Spec.Name)
                    ? tool with { Handler = (invocation, cancellationToken) => RunInWorkerAsync(tool, options, invocation, cancellationToken) }
                    : tool.Spec.Name == WebGet
                        ? tool with { Handler = (invocation, cancellationToken) => WebGetAsync(tool, options, invocation, cancellationToken) }
                        : tool)];
        }

        public async ValueTask<string?> RefuseRunAsync(bool providerRunsItsOwnTools, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _runProfile, sandbox.IsSandboxed ? sandbox.Profile : null);
            Volatile.Write(ref _processHost, null);
            if (!sandbox.IsSandboxed) return null;
            if (sandbox.Profile is not { } profile) return SandboxProblem(sandbox);
            if (!providerRunsItsOwnTools) return runtime.GetSandboxAvailability().Refusal(profile.Network);
            // A provider that runs its own tools runs them in its own process: that process starts in the sandbox, or
            // the run does not start.
            if (!SandboxedProviders.StartsInSandbox(runtime.ProviderTypeOf(providerId)))
                return $"This session is sandboxed (profile '{profile.Name}'), and its provider runs its own tools in a process the sandbox cannot start. " +
                       "Choose another provider, or turn the sandbox off for this session.";
            // The agent reaches the service of its vendor even with a profile without network.
            if (runtime.GetSandboxAvailability().Refusal(profile.Network == SandboxNetwork.Off ? SandboxNetwork.Hosts : profile.Network) is { } refusal) return refusal;
            Volatile.Write(ref _processHost, new AgentProcessHost(runtime, sessionId, providerId, profile, projectRoots));
            return null;
        }

        private async Task<AgentToolResult> RunInWorkerAsync(AgentToolDefinition tool, AgentBuiltInToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sandbox.IsSandboxed) return await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
            if (sandbox.Profile is not { } profile) return Refused(tool, SandboxProblem(sandbox));
            try
            {
                var refusedBefore = runtime.SandboxWorkers.RecentRefusals(sessionId);
                using var lease = await runtime.SandboxWorkers.AcquireAsync(StartOf(profile, options), cancellationToken).ConfigureAwait(false);
                var result = await lease.Client.InvokeAsync(invocation, new SandboxToolHost(options.OnPermissionRequest,
                    options.CommandGuard is { } guard ? (command, folder, token) => guard(command, folder, token) : null), cancellationToken).ConfigureAwait(false);
                // What blocked a command, when it can be told, stops the agent from trying it again the same way.
                return tool.Spec.Name == ShellCommand && !result.Success
                    && SandboxOutsideCommands.FailureNote(profile, result, refusedBefore, runtime.SandboxWorkers.RecentRefusals(sessionId)) is { } note
                    ? SandboxOutsideCommands.WithNote(result, note)
                    : result;
            }
            catch (SandboxUnavailableException unavailable)
            {
                return Refused(tool, unavailable.Message);
            }
        }

        // A command of a sandboxed session runs in its worker; one it asks to run outside, with a reason, runs here once
        // the user allows it, whatever the permission mode.
        private async Task<AgentToolResult> ShellCommandAsync(AgentToolDefinition tool, AgentBuiltInToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
        {
            var taken = SandboxOutsideCommands.TryTake(invocation, out var inside, out var reason);
            // The guard of the host checks the command here: the worker that would check it runs in the sandbox, and a
            // worker started for a background command checks none.
            if (options.CommandGuard is { } guard && CommandOf(inside) is { } guarded
                && await guard(guarded, FolderOf(inside, options) ?? Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return new AgentToolResult(false, [], refusal);
            }

            if (!taken) return await RunInWorkerAsync(tool, options, inside, cancellationToken).ConfigureAwait(false);
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            // A session that is no longer sandboxed runs its commands here, as it asks for them.
            if (!sandbox.IsSandboxed) return await tool.Handler(inside, cancellationToken).ConfigureAwait(false);
            if (sandbox.Profile is not { } profile) return Refused(tool, SandboxProblem(sandbox));
            if (profile.Outside != SandboxOutside.Ask)
                return Refused(tool, $"the sandbox of this session (profile '{profile.Name}') runs no command outside it. Ask the user to run it.");
            var command = CommandOf(inside);
            var folder = FolderOf(inside, options);
            var request = new AgentCommandPermissionRequest(options.ProviderId, options.SessionId, DateTimeOffset.UtcNow, RunId: null, InteractionId: invocation.ToolCallId,
                ApprovalId: null, Command: command, WorkingDirectory: folder, Actions: null, Reason: reason.Trim(), Network: null, ProposedExecPolicyAmendment: null,
                ProposedNetworkPolicyAmendments: null) { OutsideSandbox = true };
            var decision = await options.OnPermissionRequest(request, cancellationToken).ConfigureAwait(false);
            if (decision.Kind is not (AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession))
                return Refused(tool, "the user did not allow it to run outside the sandbox.");
            // Allowed once, for this command: the shell of the host does not ask it again.
            var allowed = Copy(options, null, static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)));
            var host = AgentBuiltInToolFactory.CreateDefaultTools(allowed).Single(static candidate => candidate.Spec.Name == ShellCommand);
            var result = await host.Handler(inside, cancellationToken).ConfigureAwait(false);
            // After the output, which starts with its exit code as the timelines read it.
            return SandboxOutsideCommands.WithNote(result, $"[sandbox: {profile.Name}] The command ran outside the sandbox, as the user allowed.");
        }

        private async Task<AgentToolResult> WebGetAsync(AgentToolDefinition tool, AgentBuiltInToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sandbox.IsSandboxed) return await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
            if (sandbox.Profile is not { } profile) return Refused(tool, SandboxProblem(sandbox));
            if (profile.Network == SandboxNetwork.Off) return Refused(tool, $"the sandbox of this session (profile '{profile.Name}') has no network.");
            var policy = runtime.SandboxWorkers.PolicyOf(StartOf(profile, options));
            using var http = SandboxHttp.Create(policy, runtime.SandboxWorkers.ReservedPorts);
            var sandboxed = AgentBuiltInToolFactory.CreateDefaultTools(Copy(options, http, null)).Single(static candidate => candidate.Spec.Name == WebGet);
            return await sandboxed.Handler(invocation, cancellationToken).ConfigureAwait(false);
        }

        private SessionSandboxStart StartOf(ResolvedSandboxProfile profile, AgentBuiltInToolOptions options)
            => runtime.SandboxStartOf(sessionId, providerId, profile, options.WorkingDirectory ?? Environment.CurrentDirectory, projectRoots, options.Environment, options.CommandGuard is not null);

        private static AgentToolResult Refused(AgentToolDefinition tool, string reason) => new(false, [], $"{tool.Spec.Name} did not run: {reason}");

        private static string? CommandOf(AgentToolInvocation invocation)
            => invocation.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object && invocation.Arguments.TryGetProperty("command", out var text)
               && text.ValueKind == System.Text.Json.JsonValueKind.String
                ? text.GetString()
                : null;

        // The folder of a command, as the shell tool resolves it.
        private static string? FolderOf(AgentToolInvocation invocation, AgentBuiltInToolOptions options)
            => invocation.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object && invocation.Arguments.TryGetProperty("workdir", out var workdir)
               && workdir.ValueKind == System.Text.Json.JsonValueKind.String
                ? Path.GetFullPath(Path.Combine(options.WorkingDirectory ?? Environment.CurrentDirectory, workdir.GetString()!))
                : options.WorkingDirectory;

        // The options of the tools of the session, with another client for the web or another answer to their permissions.
        private static AgentBuiltInToolOptions Copy(AgentBuiltInToolOptions options, HttpClient? http, AgentPermissionRequestHandler? permissions) => new()
        {
            ProviderId = options.ProviderId,
            SessionId = options.SessionId,
            WorkingDirectory = options.WorkingDirectory,
            Environment = options.Environment,
            OnPermissionRequest = permissions ?? options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
            EnableUserInputTool = false,
            DisabledTools = options.DisabledTools,
            WriteGuard = options.WriteGuard,
            CommandGuard = options.CommandGuard,
            HttpClient = http ?? options.HttpClient,
            Provider = options.Provider,
            DefaultReadFileLineLimit = options.DefaultReadFileLineLimit,
            MaxReadFileLineLimit = options.MaxReadFileLineLimit,
            MaxGrepMatches = options.MaxGrepMatches,
            MaxWebGetBytes = options.MaxWebGetBytes,
            WebGetTimeout = options.WebGetTimeout,
        };
    }
}
