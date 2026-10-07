using System.Reflection;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// What one CLI process of a session is started with. A change of any of these starts another process, which
/// resumes the conversation.
/// </summary>
internal sealed record ClaudeCodeLaunchKey(string? WorkingDirectory, string? Model, string? Effort);

/// <summary>
/// Builds the command line and the environment of a CLI process.
/// </summary>
internal static class ClaudeCodeLauncher
{
    /// <summary>The name of the MCP server through which the CLI reaches the tools of CodeAlta.</summary>
    public const string McpServerName = "codealta";

    /// <summary>The prefix the CLI gives the tools of that server.</summary>
    public const string McpToolPrefix = "mcp__" + McpServerName + "__";

    // The server is served by this process over the control protocol: nothing is listening anywhere.
    private const string McpConfig = "{\"mcpServers\":{\"" + McpServerName + "\":{\"type\":\"sdk\",\"name\":\"" + McpServerName + "\"}}}";

    // Variables that describe the Claude Code session CodeAlta itself may have been started from. They are not
    // the user's configuration: authentication and provider variables are inherited untouched.
    private static readonly string[] InheritedSessionVariables =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_CODE_HOST_SESSION_ID",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_AGENT_SDK_VERSION",
    ];

    public static ClaudeCodeLaunch Create(
        string executable,
        ClaudeCodeModelProviderRuntimeOptions options,
        ClaudeCodeLaunchKey key,
        string? newSessionId,
        string? resumeSessionId,
        bool withTools,
        bool showReasoning = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(key);

        var arguments = new List<string>
        {
            // Print mode with stream-json in both directions is the protocol of the Claude Agent SDKs.
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--replay-user-messages",
            // Permission prompts are sent to this side instead of being denied.
            "--permission-prompt-tool", "stdio",
        };

        if (!string.IsNullOrWhiteSpace(options.PermissionMode))
        {
            arguments.Add("--permission-mode");
            arguments.Add(options.PermissionMode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(key.Model))
        {
            arguments.Add("--model");
            arguments.Add(key.Model);
        }

        if (!string.IsNullOrWhiteSpace(key.Effort))
        {
            arguments.Add("--effort");
            arguments.Add(key.Effort);
        }

        // The `=` form binds the value to its option whatever the value is.
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            arguments.Add($"--resume={resumeSessionId}");
        }
        else if (!string.IsNullOrWhiteSpace(newSessionId))
        {
            arguments.Add($"--session-id={newSessionId}");
        }

        if (withTools)
        {
            arguments.Add("--mcp-config");
            arguments.Add(McpConfig);
            // The tools of CodeAlta ask their own permissions.
            arguments.Add($"--allowedTools=mcp__{McpServerName}");
        }

        if (showReasoning)
        {
            // Without it the thinking blocks of the model are written empty: the timeline shows their summary.
            arguments.Add("--thinking-display");
            arguments.Add("summarized");
        }

        arguments.AddRange(options.ExtraArguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in InheritedSessionVariables)
        {
            environment[name] = null;
        }

        // The CLI tells this side when a session is idle again, and names CodeAlta in its user agent.
        environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1";
        environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = string.IsNullOrWhiteSpace(options.ClientApp) ? DefaultClientApp : options.ClientApp.Trim();

        return new ClaudeCodeLaunch(executable, arguments, key.WorkingDirectory, environment);
    }

    /// <summary>Maps the reasoning effort of CodeAlta to the effort level of the CLI.</summary>
    public static string? ToEffort(AgentReasoningEffort? effort)
        => effort switch
        {
            AgentReasoningEffort.Minimal or AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            AgentReasoningEffort.Max => "max",
            _ => null,
        };

    /// <summary>The model option of the CLI for a model of the catalog: the default model takes none.</summary>
    public static string? ToModelOption(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) || string.Equals(modelId.Trim(), ClaudeCodeModelCatalog.DefaultModelId, StringComparison.OrdinalIgnoreCase)
            ? null
            : modelId.Trim();

    private static string DefaultClientApp { get; } = CreateDefaultClientApp();

    private static string CreateDefaultClientApp()
    {
        // The version of the application that runs, not of this library: only the applications are versioned.
        var application = Assembly.GetEntryAssembly() ?? typeof(ClaudeCodeLauncher).Assembly;
        var version = application.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = version?.IndexOf('+') ?? -1;
        if (plus > 0)
        {
            version = version![..plus];
        }

        return string.IsNullOrWhiteSpace(version) ? "codealta" : $"codealta/{version}";
    }
}
