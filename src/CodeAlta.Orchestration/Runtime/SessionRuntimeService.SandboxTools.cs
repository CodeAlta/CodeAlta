using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
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
    /// <returns>The starter, or null; and why no command can start, when the sandbox cannot run.</returns>
    public async Task<(ShellCommandStarter? Starter, string? Refusal)> GetJobStarterAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var sandbox = await GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!sandbox.IsSandboxed) return (null, null);
        if (sandbox.Profile is not { } profile) return (null, SandboxProblem(sandbox));
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
            return (null, "The session is not attached: its sandbox starts with its next turn.");
        var start = SandboxStartOf(sessionId, entry.ProviderKey, profile, entry.WorktreeDirectory ?? entry.WorkingDirectory, entry.ProjectRoots, entry.ProcessEnvironment, commandGuard: false);
        SessionSandboxWorkers.Lease lease;
        try
        {
            lease = await SandboxWorkers.AcquireAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException unavailable)
        {
            return (null, unavailable.Message);
        }

        // One lease per starter: the first command takes it and keeps the worker until it ends.
        var taken = 0;
        // The variables of the session are those of its worker already.
        return ((command, folder, sessionVariables, onOutput) =>
        {
            var held = Interlocked.Exchange(ref taken, 1) == 0 ? lease : null;
            var process = lease.Client.StartProcess(command, folder, onOutput);
            if (held is not null) process.Completion.ContinueWith(_ => held.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new SandboxedShellCommand(process);
        }, null);
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
        var folders = new SandboxSessionFolders
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            Writable = [.. projectRoots.Where(static root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath)
                .Where(root => !string.Equals(root, Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase))],
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

        public IReadOnlyList<AgentToolDefinition> Route(AgentBuiltInToolOptions options, IReadOnlyList<AgentToolDefinition> tools)
            => [.. tools.Select(tool => SandboxWorker.Tools.Contains(tool.Spec.Name)
                ? tool with { Handler = (invocation, cancellationToken) => RunInWorkerAsync(tool, options, invocation, cancellationToken) }
                : tool.Spec.Name == WebGet
                    ? tool with { Handler = (invocation, cancellationToken) => WebGetAsync(tool, options, invocation, cancellationToken) }
                    : tool)];

        public async ValueTask<string?> RefuseRunAsync(bool providerRunsItsOwnTools, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sandbox.IsSandboxed) return null;
            if (sandbox.Profile is not { } profile) return SandboxProblem(sandbox);
            if (providerRunsItsOwnTools)
                return $"This session is sandboxed (profile '{profile.Name}'), and its provider runs its own tools, which do not run in a sandbox yet. " +
                       "Choose a provider whose model CodeAlta drives, or turn the sandbox off for this session.";
            return runtime.GetSandboxAvailability().Refusal(profile.Network);
        }

        private async Task<AgentToolResult> RunInWorkerAsync(AgentToolDefinition tool, AgentBuiltInToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sandbox.IsSandboxed) return await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
            if (sandbox.Profile is not { } profile) return Refused(tool, SandboxProblem(sandbox));
            try
            {
                using var lease = await runtime.SandboxWorkers.AcquireAsync(StartOf(profile, options), cancellationToken).ConfigureAwait(false);
                return await lease.Client.InvokeAsync(invocation, new SandboxToolHost(options.OnPermissionRequest,
                    options.CommandGuard is { } guard ? (command, folder, token) => guard(command, folder, token) : null), cancellationToken).ConfigureAwait(false);
            }
            catch (SandboxUnavailableException unavailable)
            {
                return Refused(tool, unavailable.Message);
            }
        }

        private async Task<AgentToolResult> WebGetAsync(AgentToolDefinition tool, AgentBuiltInToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
        {
            var sandbox = await runtime.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sandbox.IsSandboxed) return await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
            if (sandbox.Profile is not { } profile) return Refused(tool, SandboxProblem(sandbox));
            if (profile.Network == SandboxNetwork.Off) return Refused(tool, $"the sandbox of this session (profile '{profile.Name}') has no network.");
            var policy = runtime.SandboxWorkers.PolicyOf(StartOf(profile, options));
            using var http = SandboxHttp.Create(policy, runtime.SandboxWorkers.ReservedPorts);
            var sandboxed = AgentBuiltInToolFactory.CreateDefaultTools(WithHttpClient(options, http)).Single(static candidate => candidate.Spec.Name == WebGet);
            return await sandboxed.Handler(invocation, cancellationToken).ConfigureAwait(false);
        }

        private SessionSandboxStart StartOf(ResolvedSandboxProfile profile, AgentBuiltInToolOptions options)
            => runtime.SandboxStartOf(sessionId, providerId, profile, options.WorkingDirectory ?? Environment.CurrentDirectory, projectRoots, options.Environment, options.CommandGuard is not null);

        private static AgentToolResult Refused(AgentToolDefinition tool, string reason) => new(false, [], $"{tool.Spec.Name} did not run: {reason}");

        private static AgentBuiltInToolOptions WithHttpClient(AgentBuiltInToolOptions options, HttpClient http) => new()
        {
            ProviderId = options.ProviderId,
            SessionId = options.SessionId,
            WorkingDirectory = options.WorkingDirectory,
            Environment = options.Environment,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
            EnableUserInputTool = false,
            DisabledTools = options.DisabledTools,
            WriteGuard = options.WriteGuard,
            CommandGuard = options.CommandGuard,
            HttpClient = http,
            Provider = options.Provider,
            DefaultReadFileLineLimit = options.DefaultReadFileLineLimit,
            MaxReadFileLineLimit = options.MaxReadFileLineLimit,
            MaxGrepMatches = options.MaxGrepMatches,
            MaxWebGetBytes = options.MaxWebGetBytes,
            WebGetTimeout = options.WebGetTimeout,
        };
    }
}
