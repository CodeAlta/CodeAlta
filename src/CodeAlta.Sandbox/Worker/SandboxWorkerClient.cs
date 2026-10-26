using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Sandbox.Worker;

/// <summary>What the worker asks CodeAlta while a tool runs, answered as for a tool that runs in CodeAlta.</summary>
/// <param name="Permission">Answers a permission request of the tool.</param>
/// <param name="CommandGuard">Says why a command is refused before any permission, or null; null when no guard applies.</param>
public sealed record SandboxToolHost(AgentPermissionRequestHandler Permission, Func<string, string, CancellationToken, ValueTask<string?>>? CommandGuard);

/// <summary>A background command run by a sandbox worker.</summary>
public sealed class SandboxedProcess
{
    private readonly Action _kill;

    internal SandboxedProcess(Action kill, Action<string> onOutput)
    {
        _kill = kill;
        OnOutput = onOutput;
    }

    /// <summary>Gets the id of the shell of the command, once it started; null before, or when it could not start.</summary>
    public int? ProcessId { get; internal set; }

    /// <summary>Gets the task that ends with the exit code of the command, <c>-1</c> when it could not run; it never faults.</summary>
    public Task<int> Completion => Exit.Task;

    internal TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Action<string> OnOutput { get; }

    /// <summary>Ends the command and the processes it started.</summary>
    public void Kill() => _kill();
}

/// <summary>The worker of a sandboxed session, as CodeAlta talks to it: tool calls, and background commands.</summary>
public sealed class SandboxWorkerClient : IAsyncDisposable
{
    private readonly SandboxWorkerChannel _channel;
    private readonly IAsyncDisposable? _process;
    private readonly ConcurrentDictionary<long, Call> _calls = new();
    private readonly ConcurrentDictionary<long, SandboxedProcess> _processes = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closing = new();
    private int _disposed;
    private readonly Task _reading;
    private long _nextId;
    private string? _ended;

    private SandboxWorkerClient(SandboxWorkerChannel channel, IAsyncDisposable? process)
    {
        _channel = channel;
        _process = process;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>Gets whether the worker is gone: its process ended, or it broke the protocol.</summary>
    public bool HasEnded => Volatile.Read(ref _ended) is not null;

    /// <summary>Gets the process id of the worker as it reported it, once it is ready.</summary>
    public int? WorkerProcessId { get; private set; }

    /// <summary>Starts the worker of a session in a sandbox and waits until it is ready.</summary>
    /// <param name="backend">The sandbox.</param>
    /// <param name="policy">The policy of the session.</param>
    /// <param name="commandLine">The command line of the worker: an executable of CodeAlta and <see cref="SandboxWorker.Option"/>.</param>
    /// <param name="proxyEndpoint">The proxy of <see cref="SandboxNetwork.Hosts"/>, or null.</param>
    /// <param name="init">What the worker runs the tools of: the session, its provider and its folders.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The worker.</returns>
    /// <exception cref="SandboxUnavailableException">The sandbox or the worker did not start.</exception>
    public static async Task<SandboxWorkerClient> StartAsync(ISandboxBackend backend, SandboxPolicy policy, string commandLine, string? proxyEndpoint,
        SandboxWorkerInit init, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(init);
        var process = await backend.StartAsync(policy, commandLine, proxyEndpoint, cancellationToken).ConfigureAwait(false);
        var client = new SandboxWorkerClient(new SandboxWorkerChannel(process.StandardOutput, process.StandardInput), process);
        try
        {
            await client.InitializeAsync(init, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Connects to a worker that runs on two streams (the worker of tests, which runs without a sandbox).</summary>
    /// <param name="fromWorker">What the worker writes.</param>
    /// <param name="toWorker">What is written to the worker.</param>
    /// <param name="init">What the worker runs the tools of.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The worker.</returns>
    internal static async Task<SandboxWorkerClient> ConnectAsync(Stream fromWorker, Stream toWorker, SandboxWorkerInit init, CancellationToken cancellationToken)
    {
        var client = new SandboxWorkerClient(new SandboxWorkerChannel(fromWorker, toWorker), null);
        await client.InitializeAsync(init, cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>Runs a tool in the worker.</summary>
    /// <param name="invocation">The call, as the session gives it.</param>
    /// <param name="host">What answers the questions of the tool.</param>
    /// <param name="cancellationToken">Cancels the call: the worker stops the tool, and its result says so.</param>
    /// <returns>The result of the tool; a failure when the worker is gone.</returns>
    public async Task<AgentToolResult> InvokeAsync(AgentToolInvocation invocation, SandboxToolHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);
        if (Volatile.Read(ref _ended) is { } ended) return Failure(invocation.ToolName, ended);
        var id = Interlocked.Increment(ref _nextId);
        var call = new Call(invocation, host);
        _calls[id] = call;
        try
        {
            await _channel.WriteAsync(new SandboxWorkerMessage
            {
                Type = SandboxWorkerMessageTypes.Invoke, Id = id, Tool = invocation.ToolName, ToolCallId = invocation.ToolCallId, Arguments = invocation.Arguments,
            }, _closing.Token).ConfigureAwait(false);
            await using var cancel = cancellationToken.Register(() => _ = WriteQuietlyAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Cancel, Id = id }));
            return await call.Result.Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return Failure(invocation.ToolName, Volatile.Read(ref _ended) ?? exception.Message);
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>Starts a background command in the worker.</summary>
    /// <param name="command">The command line, for the shell of the tools.</param>
    /// <param name="folder">The folder it runs in.</param>
    /// <param name="onOutput">Receives what it writes, as it comes.</param>
    /// <returns>The command.</returns>
    public SandboxedProcess StartProcess(string command, string folder, Action<string> onOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onOutput);
        var id = Interlocked.Increment(ref _nextId);
        var process = new SandboxedProcess(() => _ = WriteQuietlyAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Kill, Id = id }), onOutput);
        if (Volatile.Read(ref _ended) is { } ended)
        {
            onOutput($"The sandbox of the session ended: {ended}{Environment.NewLine}");
            process.Exit.TrySetResult(-1);
            return process;
        }

        _processes[id] = process;
        _ = WriteQuietlyAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Start, Id = id, Command = command, Folder = folder });
        return process;
    }

    /// <summary>Ends the worker and everything it runs.</summary>
    /// <returns>A task that completes once the worker is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        End("The sandbox of the session was closed.");
        await _closing.CancelAsync().ConfigureAwait(false);
        if (_process is not null) await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or InvalidDataException)
        {
            // The reader stops with its stream.
        }

        _closing.Dispose();
    }

    private async Task InitializeAsync(SandboxWorkerInit init, CancellationToken cancellationToken)
    {
        await _channel.WriteAsync(new SandboxWorkerMessage
        {
            Type = SandboxWorkerMessageTypes.Init,
            SessionId = init.SessionId,
            ProviderId = init.ProviderId,
            WorkingDirectory = init.WorkingDirectory,
            WritableRoots = [.. init.WritableRoots],
            Scratchpad = init.Scratchpad,
            Home = init.Home,
            CommandGuard = init.CommandGuard,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new SandboxUnavailableException("The worker of the sandbox did not answer.", exception);
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _channel.ReadAsync().ConfigureAwait(false) is { } message)
            {
                Dispatch(message);
            }

            End("The worker of the sandbox ended.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException or JsonException)
        {
            End($"The worker of the sandbox stopped: {exception.Message}");
        }
    }

    private void Dispatch(SandboxWorkerMessage message)
    {
        switch (message.Type)
        {
            case SandboxWorkerMessageTypes.Ready:
                WorkerProcessId = message.ProcessId;
                _ready.TrySetResult();
                break;
            case SandboxWorkerMessageTypes.Result when _calls.TryGetValue(message.Id, out var call):
                var result = message.Payload?.Deserialize(AgentJsonSerializerContext.Default.AgentToolResult)
                    ?? new AgentToolResult(false, [], "The sandbox gave no result.");
                call.Result.TrySetResult(FromSandbox(result));
                break;
            case SandboxWorkerMessageTypes.Progress when _calls.TryGetValue(message.Id, out var running) && running.Invocation.Progress is { } progress:
                // What the tool writes while it runs: the session shows it, as for a tool that runs in CodeAlta.
                _ = progress(new AgentToolProgressUpdate(message.Text ?? string.Empty, message.Details), CancellationToken.None).AsTask();
                break;
            case SandboxWorkerMessageTypes.Permission:
                _ = AnswerPermissionAsync(message);
                break;
            case SandboxWorkerMessageTypes.Guard:
                _ = AnswerGuardAsync(message);
                break;
            case SandboxWorkerMessageTypes.Started when _processes.TryGetValue(message.Id, out var started):
                started.ProcessId = message.ProcessId;
                break;
            case SandboxWorkerMessageTypes.Output when _processes.TryGetValue(message.Id, out var writing):
                writing.OnOutput(message.Text ?? string.Empty);
                break;
            case SandboxWorkerMessageTypes.Exited when _processes.TryRemove(message.Id, out var exited):
                if (message.Error is { } error) exited.OnOutput(error + Environment.NewLine);
                exited.Exit.TrySetResult(message.ExitCode ?? -1);
                break;
        }
    }

    // A question of the worker belongs to the call that runs: with several calls at once, the first that has a host
    // answers; the decision is bound by the permission service to the request it names.
    private async Task AnswerPermissionAsync(SandboxWorkerMessage message)
    {
        AgentPermissionDecision decision;
        try
        {
            var request = message.Payload?.Deserialize(AgentJsonSerializerContext.Default.AgentPermissionRequest)
                ?? throw new InvalidDataException("The sandbox asked a permission without a request.");
            var call = FindCall(request.InteractionId);
            decision = call is null
                ? new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)
                : await call.Host.Permission(request with { ProviderId = call.Invocation.ProviderId, SessionId = call.Invocation.SessionId }, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or OperationCanceledException)
        {
            decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
        }

        await WriteQuietlyAsync(new SandboxWorkerMessage
        {
            Type = SandboxWorkerMessageTypes.Decision,
            Id = message.Id,
            Payload = JsonSerializer.SerializeToElement(decision, AgentJsonSerializerContext.Default.AgentPermissionDecision),
        }).ConfigureAwait(false);
    }

    private async Task AnswerGuardAsync(SandboxWorkerMessage message)
    {
        string? refusal = null;
        try
        {
            if (_calls.Values.FirstOrDefault(static call => call.Host.CommandGuard is not null) is { Host.CommandGuard: { } guard })
                refusal = await guard(message.Command ?? string.Empty, message.Folder ?? string.Empty, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            refusal = "The session is closing.";
        }

        await WriteQuietlyAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.GuardAnswer, Id = message.Id, Refusal = refusal }).ConfigureAwait(false);
    }

    private Call? FindCall(string interactionId)
        => _calls.Values.FirstOrDefault(call => string.Equals(call.Invocation.ToolCallId, interactionId, StringComparison.Ordinal))
           ?? _calls.Values.FirstOrDefault();

    private void End(string reason)
    {
        if (Interlocked.CompareExchange(ref _ended, reason, null) is not null) return;
        _ready.TrySetException(new SandboxUnavailableException(reason));
        foreach (var call in _calls.Values) call.Result.TrySetResult(Failure(call.Invocation.ToolName, reason));
        foreach (var process in _processes.Values)
        {
            process.OnOutput(reason + Environment.NewLine);
            process.Exit.TrySetResult(-1);
        }

        _processes.Clear();
    }

    private async Task WriteQuietlyAsync(SandboxWorkerMessage message)
    {
        try
        {
            await _channel.WriteAsync(message, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            End(exception.Message);
        }
    }

    private static AgentToolResult Failure(string tool, string reason) => new(false, [], $"{tool} did not run: {reason}");

    // What the worker answers comes from the sandbox, where any process may speak for it: its text and the bytes of its
    // images pass, never a path or a link that CodeAlta would read or follow with the rights of the user.
    internal static AgentToolResult FromSandbox(AgentToolResult result)
    {
        var items = result.Items.Select(static item => item switch
        {
            AgentToolResultItem.Text or AgentToolResultItem.Image => item,
            AgentToolResultItem.ImageUrl { Url: var url } when url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) => item,
            _ => new AgentToolResultItem.Text("[An item of the result that names a file or a link was left out: the sandbox gives text and images only.]"),
        }).ToArray();
        return result with { Items = items };
    }

    private sealed class Call(AgentToolInvocation invocation, SandboxToolHost host)
    {
        public AgentToolInvocation Invocation { get; } = invocation;

        public SandboxToolHost Host { get; } = host;

        public TaskCompletionSource<AgentToolResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>What the worker of a session runs its tools for.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProviderId">The provider of the session.</param>
/// <param name="WorkingDirectory">Its working directory.</param>
/// <param name="WritableRoots">The folders its file tools write in.</param>
/// <param name="Scratchpad">Its scratchpad folder, or null.</param>
/// <param name="Home">The home folder of the user, whose settings the file tools do not write; null for none.</param>
/// <param name="CommandGuard">Whether commands go through the guard of the host before any permission.</param>
public sealed record SandboxWorkerInit(string SessionId, string ProviderId, string WorkingDirectory, IReadOnlyList<string> WritableRoots, string? Scratchpad, string? Home, bool CommandGuard);
