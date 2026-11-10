using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>A script of the library that a host runs itself, with no session and no model: what it runs and what it may do.</summary>
/// <param name="RunId">The run, which names its sandbox and its calls.</param>
/// <param name="Script">The name of the script.</param>
public sealed record UnattendedScriptRequest(string RunId, string Script)
{
    /// <summary>Gets its arguments, a JSON object; null for none.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>Gets what started the run as text, its <c>event</c> argument when <see cref="EventJson"/> is null.</summary>
    public string? EventText { get; init; }

    /// <summary>Gets what started the run as a JSON object, its <c>event</c> argument when it declares one.</summary>
    public string? EventJson { get; init; }

    /// <summary>Gets the project it runs in; null for none, in the home folder of the user.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the folder of that project.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the space it runs in, which decides the scripts and the sandbox it gets.</summary>
    public SpaceFilter? Spaces { get; init; }

    /// <summary>Gets the sandbox asked for: a profile, <c>off</c>, or null for what the settings give.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Gets whether its tools may edit files and run commands, beside those that read.</summary>
    public bool Write { get; init; }

    /// <summary>Gets the environment of its commands; null for the one of this process.</summary>
    public AgentProcessEnvironment? Environment { get; init; }

    /// <summary>Gets how long it may run; null for <see cref="UnattendedScriptRunner.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>What a script run with no session did.</summary>
/// <param name="Succeeded">Whether it ended with a value, or none, rather than an error.</param>
/// <param name="Value">What it returned, as JSON, cut to <see cref="UnattendedScriptRunner.MaximumValueLength"/>; null for nothing.</param>
/// <param name="Error">Why it did not run, or why it failed; null when it succeeded.</param>
public sealed record UnattendedScriptResult(bool Succeeded, string? Value, string? Error)
{
    /// <summary>Gets what it showed with <c>present()</c>, in its order.</summary>
    public IReadOnlyList<string> Presented { get; init; } = [];
}

/// <summary>
/// Runs a script of the library with no session and no model, as an automation does that only calls back into plugins or
/// tools. Nobody is there to approve a call, so what it may do is fixed: the <c>alta</c> commands that change nothing and the
/// commands of plugins, never one that starts or drives an agent; the built-in tools that read, and, when it may write, those
/// that edit and run commands; all of them in its sandbox, which a command cannot leave. It has no <c>agent()</c>,
/// <c>session()</c>, <c>ask()</c> nor MCP tools.
/// </summary>
public sealed class UnattendedScriptRunner
{
    /// <summary>The kind of caller of its <c>alta</c> commands.</summary>
    public const string CallerKind = "automation";

    /// <summary>The longest value of a run kept.</summary>
    public const int MaximumValueLength = 8000;

    /// <summary>How long a run may take when it says nothing else.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    // The built-in tools that change the computer: given to a run that may write only.
    private static readonly FrozenSet<string> WritingTools = new[]
    {
        "write_file", "replace_in_file", "delete_file_or_dir", "rename_file_or_dir", "apply_patch", "shell_command",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly ModelProviderId Provider = new("automation");

    private readonly AltaCommandDispatcher _dispatcher;
    private readonly CodeModeScriptLibrary _library;
    private readonly SessionRuntimeService? _runtime;
    private readonly Func<IReadOnlyList<string>> _pluginCommands;
    private readonly CodeModeScriptCache _cache = new();

    /// <summary>Creates the runner of a host.</summary>
    /// <param name="dispatcher">The <c>alta</c> commands of the host.</param>
    /// <param name="library">The library of scripts.</param>
    /// <param name="runtime">The runtime whose sandboxes the tools run in; null for a host without sandbox, whose sandboxed runs are refused.</param>
    /// <param name="pluginCommands">Gives the paths of the <c>alta</c> commands the running plugins contribute, such as <c>todo</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/>, <paramref name="library"/> or <paramref name="pluginCommands"/> is null.</exception>
    public UnattendedScriptRunner(AltaCommandDispatcher dispatcher, CodeModeScriptLibrary library, SessionRuntimeService? runtime, Func<IReadOnlyList<string>> pluginCommands)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(pluginCommands);
        (_dispatcher, _library, _runtime, _pluginCommands) = (dispatcher, library, runtime, pluginCommands);
    }

    /// <summary>Whether a command of <c>alta</c> is one such a run may call: one that changes nothing, or one of a plugin.</summary>
    /// <param name="command">The command.</param>
    /// <param name="pluginCommands">The paths of the commands of plugins.</param>
    /// <returns>True when it may.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool Allows(AltaCommandDescriptor command, IReadOnlyList<string> pluginCommands)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(pluginCommands);
        return command.Policy is { IsMutating: false }
            || pluginCommands.Any(path => string.Equals(command.Path, path, StringComparison.Ordinal) || command.Path.StartsWith(path + " ", StringComparison.Ordinal));
    }

    /// <summary>Runs a script to its end.</summary>
    /// <param name="request">The script and what it may do.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What it did; a script that cannot run is a result, not an exception.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<UnattendedScriptResult> RunAsync(UnattendedScriptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CodeModeScriptRunPrompt.TryArguments(_library, request.Script, request.ArgumentsJson, request.EventText, request.EventJson, request.ProjectPath, request.Spaces,
                out _, out var json, out var problem))
            return new(false, null, problem);

        var cwd = request.ProjectPath ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var options = new AgentBuiltInToolOptions
        {
            ProviderId = Provider,
            SessionId = request.RunId,
            WorkingDirectory = cwd,
            Environment = request.Environment,
            // Nobody is there to approve: what may write writes, and nothing leaves its sandbox.
            OnPermissionRequest = (permission, _) => Task.FromResult(
                permission is AgentCommandPermissionRequest { OutsideSandbox: true } ? AgentPermissionDecision.DeniedUnattended("nothing of an unattended script runs outside its sandbox")
                : request.Write ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
                : AgentPermissionDecision.DeniedByPolicy("this unattended script may not write")),
            EnableUserInputTool = false,
            DisabledTools = request.Write ? null : WritingTools,
        };

        var sandboxed = false;
        try
        {
            IReadOnlyList<AgentToolDefinition> tools;
            var sandbox = _runtime is null ? null : await _runtime.ResolveSandboxAsync(request.Sandbox, null, null, request.ProjectId, request.Spaces?.SpaceId, cancellationToken).ConfigureAwait(false);
            if (sandbox is { IsSandboxed: true })
            {
                var (routed, refusal) = await _runtime!.CreateUnattendedToolsAsync(request.RunId, sandbox, options, request.ProjectPath is { } root ? [root] : [], cancellationToken).ConfigureAwait(false);
                if (routed is null) return new(false, null, refusal);
                (tools, sandboxed) = (routed, true);
            }
            else if (_runtime is null && request.Sandbox is { } asked && !string.Equals(asked, "off", StringComparison.OrdinalIgnoreCase))
            {
                return new(false, null, $"This host runs no sandbox: the profile '{asked}' cannot be used.");
            }
            else
            {
                tools = AgentBuiltInToolFactory.CreateDefaultTools(options);
            }

            // The tools of the computer only; a tool that asks the user is not one of them.
            var invoker = new ToolInvoker([.. tools.Where(tool => AgentBuiltInToolFactory.BuiltInToolNames.Contains(tool.Spec.Name) && !(options.DisabledTools?.Contains(tool.Spec.Name) ?? false)
                && tool.Spec.Name != "request_user_input")], request.RunId);
            var identity = new AltaCallerIdentity { Kind = CallerKind, SourceProjectId = request.ProjectId, SubSessions = false, ScriptRunId = request.RunId };
            var plugins = _pluginCommands();
            var alta = new AltaScriptBinding([.. _dispatcher.Describe(identity, cwd).Where(command => Allows(command, plugins))]);
            var bound = new ToolScriptBinding(invoker, ["alta", "exec", "wait"]);
            CodeModeApi? api = null;
            api = new CodeModeApi([.. alta.Functions, .. bound.Functions, .. CodeModeDiscovery.Functions]);
            var discovery = new CodeModeDiscovery(() => api, () => _library.List(CodeModeScriptLibrary.ProjectRootOf(cwd), request.Spaces));
            var handler = new CodeModeCallRouter(
            [
                (AltaScriptBinding.Namespace, new AltaScriptCallHandler(_dispatcher, alta, identity, cwd)),
                (ToolScriptBinding.ToolsNamespace, bound),
                (ToolScriptBinding.McpNamespace, new CodeModeCallRouter([])),
                (CodeModeDiscovery.Namespace, discovery),
            ]);
            var source = CodeModeSource.Prepare($"return await workflow({JsonSerializer.Serialize(request.Script)}, {json});");
            var result = await new CodeModeHost(api, _cache).RunAsync(source, handler, new CodeModeRunOptions
            {
                Limits = CodeModeLimits.Default with { Timeout = request.Timeout ?? DefaultTimeout },
                ResolveScript = CodeModeToolFactory.ResolveWith(invoker, _library, cwd, request.Spaces, static _ => { }),
                WorkingDirectory = cwd,
            }, null, cancellationToken).ConfigureAwait(false);
            var value = result.ValueJson is { Length: > MaximumValueLength } full ? full[..(MaximumValueLength - 1)] + "…" : result.ValueJson;
            return new(result.Status == CodeModeRunStatus.Completed, value, result.Status == CodeModeRunStatus.Completed ? null : result.Error?.Message ?? $"The script ended: {result.Status}.")
            {
                Presented = [.. result.Presented.Select(static part => part.Markdown)],
            };
        }
        finally
        {
            if (sandboxed) _runtime!.ReleaseUnattendedTools(request.RunId);
        }
    }

    // The tools of a run without a session, as the nested calls of its script reach them.
    private sealed class ToolInvoker(IReadOnlyList<AgentToolDefinition> tools, string runId) : IAgentRunToolInvoker
    {
        private int _calls;

        public IReadOnlyList<AgentToolSpec> ListTools() => [.. tools.Select(static tool => tool.Spec)];

        public async Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            if (tools.FirstOrDefault(tool => tool.Spec.Name == call.ToolName) is not { } tool) return new AgentToolResult(false, [], $"This run has no tool '{call.ToolName}'.");
            var invocation = new AgentToolInvocation(Provider, runId, $"{runId}-call-{Interlocked.Increment(ref _calls)}", call.ToolName, call.Arguments) { Tools = this };
            try
            {
                return await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                return new AgentToolResult(false, [], exception.Message);
            }
        }
    }
}
