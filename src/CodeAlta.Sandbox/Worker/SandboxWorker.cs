using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Sandbox.Worker;

/// <summary>
/// The worker of a sandboxed session: the process that runs, inside the sandbox, the tools of the session that read,
/// write and execute on the computer, and its background commands.
/// </summary>
/// <remarks>
/// The tools are the built-in tools of CodeAlta, run by the same handlers as outside a sandbox. What a handler asks
/// the host (a permission, the guard of commands) goes back to CodeAlta, which answers it as for any tool call; the
/// worker decides nothing by itself. It ends when its input ends.
/// </remarks>
public static class SandboxWorker
{
    /// <summary>The option the executables of CodeAlta are started with to run the worker.</summary>
    public const string Option = "--sandbox-worker";

    /// <summary>The tools the worker runs; the others stay in CodeAlta.</summary>
    public static IReadOnlySet<string> Tools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "view_image", "list_dir", "grep", "shell_command", "write_file", "replace_in_file", "delete_file_or_dir", "rename_file_or_dir", "apply_patch",
    };

    /// <summary>Runs the worker on the standard streams of the process, until its input ends.</summary>
    /// <returns>The exit code of the process.</returns>
    public static async Task<int> RunAsync()
    {
        // The commands of the session inherit the null device, never the streams of the protocol.
        var (input, output) = SandboxWorkerStandardStreams.Take();
        await RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Runs the worker on two streams, until the input ends.</summary>
    /// <param name="input">What CodeAlta writes to the worker.</param>
    /// <param name="output">What the worker writes to CodeAlta.</param>
    /// <param name="cancellationToken">Stops the worker.</param>
    /// <returns>A task that completes when the worker ends.</returns>
    public static async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        await using var worker = new Session(new SandboxWorkerChannel(input, output));
        await worker.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly SandboxWorkerChannel _channel;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _calls = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<SandboxWorkerMessage>> _waiting = new();
        private readonly ConcurrentDictionary<long, Process> _processes = new();
        private readonly ConcurrentBag<Task> _running = [];
        private Dictionary<string, AgentToolDefinition>? _tools;
        private long _nextRequest;

        public Session(SandboxWorkerChannel channel) => _channel = channel;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && await _channel.ReadAsync().ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case SandboxWorkerMessageTypes.Init:
                        _tools = CreateTools(message);
                        await _channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Ready, ProcessId = Environment.ProcessId }, cancellationToken).ConfigureAwait(false);
                        break;
                    case SandboxWorkerMessageTypes.Invoke:
                        var call = new CancellationTokenSource();
                        _calls[message.Id] = call;
                        _running.Add(Task.Run(() => InvokeAsync(message, call.Token), CancellationToken.None));
                        break;
                    case SandboxWorkerMessageTypes.Cancel:
                        if (_calls.TryGetValue(message.Id, out var cancelled)) await cancelled.CancelAsync().ConfigureAwait(false);
                        break;
                    case SandboxWorkerMessageTypes.Decision or SandboxWorkerMessageTypes.GuardAnswer:
                        if (_waiting.TryRemove(message.Id, out var waiter)) waiter.TrySetResult(message);
                        break;
                    case SandboxWorkerMessageTypes.Start:
                        _running.Add(Task.Run(() => RunProcessAsync(message), CancellationToken.None));
                        break;
                    case SandboxWorkerMessageTypes.Kill:
                        if (_processes.TryGetValue(message.Id, out var process)) Kill(process);
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var call in _calls.Values) await call.CancelAsync().ConfigureAwait(false);
            foreach (var process in _processes.Values) Kill(process);
            foreach (var waiter in _waiting.Values) waiter.TrySetCanceled();
            try
            {
                await Task.WhenAll(_running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // The container ends the processes that are left.
            }
        }

        private Dictionary<string, AgentToolDefinition> CreateTools(SandboxWorkerMessage init)
        {
            var workingDirectory = init.WorkingDirectory ?? Environment.CurrentDirectory;
            var options = new AgentBuiltInToolOptions
            {
                ProviderId = new ModelProviderId(init.ProviderId ?? "sandbox"),
                SessionId = init.SessionId ?? "sandbox",
                WorkingDirectory = workingDirectory,
                OnPermissionRequest = AskPermissionAsync,
                CommandGuard = init.CommandGuard ? CheckCommandAsync : null,
                // The file tools keep the rules of a session without shell inside the sandbox too: what the container
                // already refuses, and the settings of git, CodeAlta and the coding agents inside the working tree.
                WriteGuard = new AgentWriteGuard(init.WritableRoots is { Count: > 0 } roots ? roots : [workingDirectory], [],
                    init.Scratchpad is { } scratchpad ? [scratchpad] : [], init.Home),
            };
            return AgentBuiltInToolFactory.CreateDefaultTools(options)
                .Where(static tool => Tools.Contains(tool.Spec.Name))
                .ToDictionary(static tool => tool.Spec.Name, StringComparer.Ordinal);
        }

        private async Task InvokeAsync(SandboxWorkerMessage message, CancellationToken cancellationToken)
        {
            AgentToolResult result;
            try
            {
                if (_tools is null || message.Tool is not { } name || !_tools.TryGetValue(name, out var tool))
                {
                    result = new AgentToolResult(false, [], $"The sandbox does not run the tool '{message.Tool}'.");
                }
                else
                {
                    var invocation = new AgentToolInvocation(new ModelProviderId("sandbox"), "sandbox", message.ToolCallId ?? message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        name, message.Arguments ?? JsonDocument.Parse("{}").RootElement.Clone(),
                        (update, token) => new ValueTask(_channel.WriteAsync(new SandboxWorkerMessage
                        {
                            Type = SandboxWorkerMessageTypes.Progress, Id = message.Id, Text = update.Delta, Details = update.Details,
                        }, token)));
                    result = await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new AgentToolResult(false, [], $"{message.Tool} was canceled.");
            }
            catch (Exception exception)
            {
                result = new AgentToolResult(false, [], $"{message.Tool} failed in the sandbox: {exception.Message}");
            }
            finally
            {
                if (_calls.TryRemove(message.Id, out var call)) call.Dispose();
            }

            await _channel.WriteAsync(new SandboxWorkerMessage
            {
                Type = SandboxWorkerMessageTypes.Result,
                Id = message.Id,
                Payload = JsonSerializer.SerializeToElement(result, AgentJsonSerializerContext.Default.AgentToolResult),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        // A permission the handler asks is the host's to give.
        private async Task<AgentPermissionDecision> AskPermissionAsync(AgentPermissionRequest request, CancellationToken cancellationToken)
        {
            var answer = await AskAsync(new SandboxWorkerMessage
            {
                Type = SandboxWorkerMessageTypes.Permission,
                Payload = JsonSerializer.SerializeToElement(request, AgentJsonSerializerContext.Default.AgentPermissionRequest),
            }, cancellationToken).ConfigureAwait(false);
            return answer.Payload is { } payload
                ? payload.Deserialize(AgentJsonSerializerContext.Default.AgentPermissionDecision) ?? new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)
                : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
        }

        private async ValueTask<string?> CheckCommandAsync(string command, string workdir, CancellationToken cancellationToken)
            => (await AskAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Guard, Command = command, Folder = workdir }, cancellationToken).ConfigureAwait(false)).Refusal;

        private async Task<SandboxWorkerMessage> AskAsync(SandboxWorkerMessage request, CancellationToken cancellationToken)
        {
            // The ids of the worker's questions are negative: those of CodeAlta's calls are positive.
            var id = -Interlocked.Increment(ref _nextRequest);
            var waiter = new TaskCompletionSource<SandboxWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = waiter;
            try
            {
                await _channel.WriteAsync(request with { Id = id }, cancellationToken).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _waiting.TryRemove(id, out _);
            }
        }

        // A background command of the session: what it writes goes to CodeAlta as it comes, then its exit code.
        private async Task RunProcessAsync(SandboxWorkerMessage message)
        {
            Process? process = null;
            try
            {
                var startInfo = AgentBuiltInToolFactory.CreateShellStartInfo(message.Command ?? string.Empty, message.Folder ?? Environment.CurrentDirectory);
                startInfo.RedirectStandardInput = true;
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start.");
                process.StandardInput.Close();
                _processes[message.Id] = process;
                await _channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Started, Id = message.Id, ProcessId = process.Id }, CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError)).ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await _channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Exited, Id = message.Id, ExitCode = process.ExitCode }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                await _channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Exited, Id = message.Id, ExitCode = -1, Error = exception.Message }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _processes.TryRemove(message.Id, out _);
                process?.Dispose();
            }

            async Task Pump(StreamReader reader)
            {
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await _channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Output, Id = message.Id, Text = new string(buffer, 0, read) }, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
