using System.Collections.Concurrent;
using System.Text;
using CodeAlta.Sandbox;
using CodeAlta.Sandbox.Network;
using CodeAlta.Sandbox.Worker;

namespace CodeAlta.Orchestration.Runtime.Sandbox;

/// <summary>What the worker of one session is started with.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProviderId">Its provider.</param>
/// <param name="Profile">The profile it runs with.</param>
/// <param name="Folders">The folders it works in.</param>
/// <param name="Environment">The variables of the session (its identity and markers).</param>
/// <param name="CommandGuard">Whether its commands go through the guard of the host.</param>
internal sealed record SessionSandboxStart(string SessionId, string ProviderId, ResolvedSandboxProfile Profile, SandboxSessionFolders Folders,
    IReadOnlyList<KeyValuePair<string, string>> Environment, bool CommandGuard);

/// <summary>
/// The sandbox workers of the sessions of a runtime: one per sandboxed session, started on its first tool call,
/// started again when its policy changes, and stopped when the session has had nothing to do for a while.
/// </summary>
internal sealed class SessionSandboxWorkers : IAsyncDisposable
{
    private static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(10);

    private readonly ISandboxBackend _backend;
    private readonly Func<string> _commandLine;
    private readonly Func<SandboxHost> _host;
    private readonly IReadOnlySet<int> _reservedPorts;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _sweeping;
    private SandboxHost? _discovered;

    public SessionSandboxWorkers(ISandboxBackend backend, Func<string> commandLine, Func<SandboxHost> host, IReadOnlySet<int> reservedPorts)
    {
        _backend = backend;
        _commandLine = commandLine;
        _host = host;
        _reservedPorts = reservedPorts;
        _sweeping = Task.Run(SweepAsync);
    }

    public ISandboxBackend Backend => _backend;

    public IReadOnlySet<int> ReservedPorts => _reservedPorts;

    /// <summary>Gets the policy a session would run with, without starting anything.</summary>
    public SandboxPolicy PolicyOf(SessionSandboxStart start)
        => SandboxPolicyBuilder.Build(start.Profile, start.Folders, Host(), start.Environment, start.SessionId);

    /// <summary>
    /// Gets the worker of a session, started with its policy: the running one when the policy is the same, a new one
    /// otherwise. The lease keeps the worker from being stopped as idle while it is held.
    /// </summary>
    /// <exception cref="SandboxUnavailableException">The sandbox or its worker did not start.</exception>
    public async Task<Lease> AcquireAsync(SessionSandboxStart start, CancellationToken cancellationToken)
    {
        var policy = PolicyOf(start);
        var key = KeyOf(policy);
        var slot = _slots.GetOrAdd(start.SessionId, static _ => new Slot());
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.Current is { } running && (running.Key != key || running.Client.HasEnded))
            {
                slot.Current = null;
                await running.DisposeAsync().ConfigureAwait(false);
            }

            if (slot.Current is null)
            {
                SandboxNetworkProxy? proxy = null;
                try
                {
                    if (policy.Network == SandboxNetwork.Hosts) proxy = SandboxNetworkProxy.Start(policy.Hosts, policy.LocalPorts, _reservedPorts);
                    var client = await SandboxWorkerClient.StartAsync(_backend, policy, _commandLine(), proxy?.Endpoint,
                        new SandboxWorkerInit(start.SessionId, start.ProviderId, policy.WorkingDirectory, [policy.WorkingDirectory, .. start.Folders.Writable],
                            start.Folders.Scratchpad, Host().HomeDirectory, start.CommandGuard), cancellationToken).ConfigureAwait(false);
                    slot.Current = new Running(client, proxy, key, policy);
                }
                catch
                {
                    if (proxy is not null) await proxy.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            var current = slot.Current;
            current.Touch(+1);
            return new Lease(current);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Starts the process of an agent that runs its own tools (the CLI of Claude Code) in a container of its own, with
    /// the profile of the session and what the process needs: the folder of its executable, its account folder, the
    /// hosts of its vendor and the variables that configure it. A profile without network gets the hosts of the vendor
    /// alone: the agent does not work without its service. The process and its proxy end together.
    /// </summary>
    /// <exception cref="SandboxUnavailableException">The sandbox did not start.</exception>
    public async Task<CodeAlta.Agent.Runtime.IAgentHostedProcess> StartAgentProcessAsync(SessionSandboxStart start, CodeAlta.Agent.Runtime.AgentProcessLaunch launch,
        CancellationToken cancellationToken)
    {
        var profile = AgentProfile(start.Profile, launch);
        var environment = launch.Environment.Where(static pair => pair.Value is not null).Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value!))
            .Concat(start.Environment).DistinctBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var policy = SandboxPolicyBuilder.Build(profile, start.Folders with { Protected = [.. start.Folders.Protected, .. launch.ProtectedPaths] }, Host(), environment,
            start.SessionId + "-agent");
        SandboxNetworkProxy? proxy = null;
        try
        {
            if (policy.Network == SandboxNetwork.Hosts) proxy = SandboxNetworkProxy.Start(policy.Hosts, policy.LocalPorts, _reservedPorts);
            var process = await _backend.StartAsync(policy, SandboxCommandLine.Create(launch.FileName, launch.Arguments), proxy?.Endpoint, cancellationToken).ConfigureAwait(false);
            return new AgentProcess(process, proxy);
        }
        catch
        {
            if (proxy is not null) await proxy.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The profile the process of an agent runs with: the profile of the session and what the process needs.</summary>
    internal static ResolvedSandboxProfile AgentProfile(ResolvedSandboxProfile profile, CodeAlta.Agent.Runtime.AgentProcessLaunch launch) => profile with
    {
        Network = profile.Network == SandboxNetwork.Off ? SandboxNetwork.Hosts : profile.Network,
        Hosts = profile.Network == SandboxNetwork.Off ? [.. launch.Hosts] : [.. profile.Hosts.Union(launch.Hosts, StringComparer.OrdinalIgnoreCase)],
        Write = [.. profile.Write, .. launch.ReadWriteFolders],
        Read = [.. profile.Read, .. launch.ReadOnlyFolders],
        Env = [.. profile.Env.Union(launch.PassedVariables, StringComparer.OrdinalIgnoreCase)],
    };

    /// <summary>Gets the hosts the proxy of a session refused last, for the note of a failed command.</summary>
    public IReadOnlyList<string> RecentRefusals(string sessionId)
        => _slots.TryGetValue(sessionId, out var slot) && slot.Current?.Proxy is { } proxy ? proxy.RecentRefusals : [];

    /// <summary>Stops the worker of a session, and what it runs.</summary>
    public async Task StopAsync(string sessionId)
    {
        if (!_slots.TryRemove(sessionId, out var slot)) return;
        await slot.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (slot.Current is { } running) await running.DisposeAsync().ConfigureAwait(false);
            slot.Current = null;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Stops the worker of a session that has gone, unless a call or a background command still holds it.</summary>
    public async Task StopIfUnusedAsync(string sessionId)
    {
        if (!_slots.TryGetValue(sessionId, out var slot)) return;
        try
        {
            await slot.Gate.WaitAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (slot.Current is { IsUnused: true } running)
            {
                slot.Current = null;
                await running.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            await _sweeping.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var sessionId in _slots.Keys.ToArray()) await StopAsync(sessionId).ConfigureAwait(false);
        _closing.Dispose();
    }

    private SandboxHost Host() => _discovered ??= _host();

    // A worker nobody used for a while, with no call and no command running, is stopped; the next call starts one.
    private async Task SweepAsync()
    {
        while (!_closing.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _closing.Token).ConfigureAwait(false);
            foreach (var (sessionId, slot) in _slots)
            {
                if (slot.Current is { } running && running.IsIdle(IdleTime) && slot.Gate.Wait(0))
                {
                    try
                    {
                        if (ReferenceEquals(slot.Current, running) && running.IsIdle(IdleTime))
                        {
                            slot.Current = null;
                            await running.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        slot.Gate.Release();
                    }
                }
            }
        }
    }

    // What changes the container of a worker: a worker with another policy is another container.
    private static string KeyOf(SandboxPolicy policy)
    {
        var key = new StringBuilder();
        void Add(IEnumerable<string> values)
        {
            foreach (var value in values) key.Append(value).Append('\u001F');
            key.Append('\u001E');
        }

        key.Append(policy.ProfileName).Append('\u001E').Append(policy.WorkingDirectory).Append('\u001E').Append(policy.Network).Append('\u001E');
        Add(policy.ReadWrite);
        Add(policy.ReadOnly);
        Add(policy.Denied);
        Add(policy.Protected);
        Add(policy.Hosts);
        Add(policy.LocalPorts.Select(static port => port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Add(policy.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + pair.Value));
        return key.ToString();
    }

    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Running? Current { get; set; }
    }

    internal sealed class Running(SandboxWorkerClient client, SandboxNetworkProxy? proxy, string key, SandboxPolicy policy) : IAsyncDisposable
    {
        private int _users;
        private long _lastUse = Environment.TickCount64;

        public SandboxWorkerClient Client { get; } = client;

        public SandboxNetworkProxy? Proxy { get; } = proxy;

        public string Key { get; } = key;

        public SandboxPolicy Policy { get; } = policy;

        public void Touch(int users)
        {
            Interlocked.Add(ref _users, users);
            Volatile.Write(ref _lastUse, Environment.TickCount64);
        }

        public bool IsUnused => Volatile.Read(ref _users) == 0;

        public bool IsIdle(TimeSpan time) => Volatile.Read(ref _users) == 0 && Environment.TickCount64 - Volatile.Read(ref _lastUse) > time.TotalMilliseconds;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            if (Proxy is not null) await Proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The process of an agent in its container, and the proxy of its network.
    private sealed class AgentProcess(ISandboxProcess process, SandboxNetworkProxy? proxy) : CodeAlta.Agent.Runtime.IAgentHostedProcess
    {
        private readonly Lazy<Task<int>> _exited = new(() => process.WaitForExitAsync(CancellationToken.None));
        private int _disposed;

        public int Id => process.Id;

        public Stream StandardInput => process.StandardInput;

        public Stream StandardOutput => process.StandardOutput;

        public Stream StandardError => process.StandardError;

        public Task<int> Exited => _exited.Value;

        public void Kill() => process.Kill();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await process.DisposeAsync().ConfigureAwait(false);
            if (proxy is not null) await proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A worker held while a call or a command uses it.</summary>
    internal sealed class Lease(Running running) : IDisposable
    {
        private int _released;

        public SandboxWorkerClient Client => running.Client;

        public SandboxPolicy Policy => running.Policy;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) running.Touch(-1);
        }
    }
}
