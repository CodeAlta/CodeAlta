using System.Text.Json;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Runs the hooks of a session around its tool calls: before a call (to block it or change its arguments) and after it
/// (to change its result). A session applies it to every tool of its own (built-in, plugin and MCP tools, the calls of
/// Code Mode scripts, the tools of CodeAlta an agent CLI calls); a turn executor whose agent runs its own tools asks it
/// before them, with the capabilities that apply there.
/// </summary>
/// <remarks>
/// The hooks run before the guards of the host (<see cref="AgentCommandGuard"/>, <see cref="AgentWriteGuard"/>, the tools a
/// session does without), the permission prompt and the sandbox: arguments a hook changes still go through all of them, so
/// a hook can only make the policy of the host stricter. An implementation answers for its own failures: what it throws
/// fails the call.
/// </remarks>
public abstract class AgentToolInterceptor
{
    /// <summary>
    /// Called before a tool call runs.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Stops the hooks with the call.</param>
    /// <returns>What to do with the call; <see cref="AgentToolCallDecision.Allow"/> lets it run as it is.</returns>
    public virtual ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolCallDecision.Allow);

    /// <summary>
    /// Called after a tool call ran, with the result its tool returned, before the model is given it.
    /// </summary>
    /// <param name="call">The call, with the arguments it ran with.</param>
    /// <param name="result">The result of the tool.</param>
    /// <param name="cancellationToken">Stops the hooks with the call.</param>
    /// <returns>The result to give the model.</returns>
    public virtual ValueTask<AgentToolResult> AfterToolCallAsync(AgentToolHookCall call, AgentToolResult result, CancellationToken cancellationToken)
        => ValueTask.FromResult(result);

    /// <summary>
    /// Returns a tool whose calls go through this interceptor: its hooks run before the handler of the tool, which keeps
    /// the guards, the permission prompt and the sandbox of the session, and after it.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <param name="workingDirectory">The folder the session works in, where relative paths of the arguments are.</param>
    /// <returns>The tool with its calls intercepted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tool"/> is <see langword="null"/>.</exception>
    public AgentToolDefinition Intercept(AgentToolDefinition tool, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var inner = tool.Handler;
        return tool with
        {
            Handler = async (invocation, cancellationToken) =>
            {
                var call = AgentToolHookCall.Create(invocation, tool.Spec, workingDirectory, AgentToolHookCapabilities.All);
                var decision = await BeforeToolCallAsync(call, cancellationToken).ConfigureAwait(false);
                if (decision.BlockMessage is { } blocked)
                {
                    return new AgentToolResult(false, [new AgentToolResultItem.Text(blocked)], blocked);
                }

                if (decision.Arguments is { } arguments)
                {
                    invocation = invocation with { Arguments = arguments };
                    call = AgentToolHookCall.Create(invocation, tool.Spec, workingDirectory, AgentToolHookCapabilities.All);
                }

                var result = await inner(invocation, cancellationToken).ConfigureAwait(false);
                return await AfterToolCallAsync(call, result, cancellationToken).ConfigureAwait(false);
            },
        };
    }
}

/// <summary>What the hooks of a tool call can do with it, which depends on who runs the tool.</summary>
[Flags]
public enum AgentToolHookCapabilities
{
    /// <summary>The hooks only see the call.</summary>
    None = 0,

    /// <summary>A hook can block the call.</summary>
    Block = 1,

    /// <summary>A hook can change the arguments of the call.</summary>
    ModifyArguments = 2,

    /// <summary>A hook can change the result of the call.</summary>
    ChangeResult = 4,

    /// <summary>Everything: a tool the session runs itself.</summary>
    All = Block | ModifyArguments | ChangeResult,
}

/// <summary>The shell command of a tool call, read from its arguments.</summary>
/// <param name="Command">The command line, as the agent wrote it.</param>
/// <param name="WorkingDirectory">The folder it runs in.</param>
public sealed record AgentShellCommand(string Command, string WorkingDirectory);

/// <summary>A tool call, as the hooks of a session see it.</summary>
public sealed record AgentToolHookCall
{
    // The shell tools of the agents that run their own (Claude Code, and `shell` for the commands of an ACP agent):
    // their command is `command`.
    private static readonly string[] AgentShellTools = ["Bash", "PowerShell", "shell"];

    /// <summary>Gets the invocation: the tool name, the arguments, the session and the call.</summary>
    public required AgentToolInvocation Invocation { get; init; }

    /// <summary>Gets the tool of the session the call runs, or <see langword="null"/> for a tool the agent runs itself.</summary>
    public AgentToolSpec? Spec { get; init; }

    /// <summary>Gets whether the agent (an agent CLI) runs the tool itself, outside CodeAlta.</summary>
    public bool RunByAgent { get; init; }

    /// <summary>Gets what the hooks can do with the call.</summary>
    public AgentToolHookCapabilities Capabilities { get; init; }

    /// <summary>Gets the folder the session works in, when known.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the shell command the call runs, for a shell tool (<c>shell_command</c>, <c>Bash</c>, <c>PowerShell</c>).</summary>
    public AgentShellCommand? ShellCommand { get; init; }

    /// <summary>Gets the full paths of the files the call writes, deletes or moves, for the file tools that name them.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];

    /// <summary>
    /// Describes a call: reads its shell command and the files it changes from the arguments of the tools that have them.
    /// </summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="spec">The tool of the session, or <see langword="null"/> for a tool the agent runs itself.</param>
    /// <param name="workingDirectory">The folder relative paths are in; <see langword="null"/> for the current one.</param>
    /// <param name="capabilities">What the hooks can do with the call.</param>
    /// <param name="runByAgent">Whether the agent runs the tool itself.</param>
    /// <param name="commandDirectory">The folder the agent says a shell command runs in, when it says it.</param>
    /// <returns>The call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="invocation"/> is <see langword="null"/>.</exception>
    public static AgentToolHookCall Create(
        AgentToolInvocation invocation,
        AgentToolSpec? spec,
        string? workingDirectory,
        AgentToolHookCapabilities capabilities,
        bool runByAgent = false,
        string? commandDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var folder = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        var arguments = invocation.Arguments;
        AgentShellCommand? shell = null;
        if (arguments.ValueKind == JsonValueKind.Object && GetString(arguments, "command") is { Length: > 0 } command
            && (invocation.ToolName == "shell_command" || (runByAgent && AgentShellTools.Contains(invocation.ToolName, StringComparer.Ordinal))))
        {
            var directory = commandDirectory ?? (runByAgent ? null : GetString(arguments, "workdir"));
            shell = new AgentShellCommand(command, string.IsNullOrWhiteSpace(directory) ? folder : ResolvePath(folder, directory));
        }

        var modified = arguments.ValueKind == JsonValueKind.Object
            ? AgentSession.GetModifiedFiles(new AgentMessagePart.ToolCall(invocation.ToolCallId, invocation.ToolName, arguments), folder)
            : [];
        return new AgentToolHookCall
        {
            Invocation = invocation,
            Spec = spec,
            RunByAgent = runByAgent,
            Capabilities = capabilities,
            WorkingDirectory = workingDirectory,
            ShellCommand = shell,
            ModifiedFiles = modified,
        };
    }

    private static string? GetString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ResolvePath(string folder, string path)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>What the hooks decided before a tool call.</summary>
public sealed record AgentToolCallDecision
{
    /// <summary>Gets the decision that lets the call run as it is.</summary>
    public static AgentToolCallDecision Allow { get; } = new();

    /// <summary>Gets the text the model reads instead of a result when the call is blocked, or <see langword="null"/>.</summary>
    public string? BlockMessage { get; init; }

    /// <summary>Gets the arguments the call runs with instead of its own, or <see langword="null"/> to keep them.</summary>
    public JsonElement? Arguments { get; init; }

    /// <summary>Creates a decision that blocks the call.</summary>
    /// <param name="message">What the model reads as the failed result of the call.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="message"/> is <see langword="null"/>, empty or white space.</exception>
    public static AgentToolCallDecision Block(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new AgentToolCallDecision { BlockMessage = message };
    }

    /// <summary>Creates a decision that runs the call with other arguments.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The decision.</returns>
    public static AgentToolCallDecision WithArguments(JsonElement arguments) => new() { Arguments = arguments };
}
