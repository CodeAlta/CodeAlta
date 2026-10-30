using System.Reflection;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Telemetry;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// What one CLI process of a session is started with. A change of any of these starts another process, which
/// resumes the conversation. <paramref name="Environment"/> is the environment of the session (its Git identity),
/// which the CLI and the commands of its tools inherit. <paramref name="WithoutShell"/> starts it without its shell
/// tools: the session does without <c>shell_command</c>. <paramref name="WithoutWebTools"/> names the web tools of the
/// CLI it is started without (<c>WebSearch</c>, <c>WebFetch</c>): the session does without
/// <see cref="AgentToolPatterns.WebSearchToolName"/> or <see cref="AgentToolPatterns.WebFetchToolName"/>.
/// </summary>
internal sealed record ClaudeCodeLaunchKey(string? WorkingDirectory, string? Model, string? Effort,
    AgentProcessEnvironment? Environment = null, bool WithoutShell = false, string? WithoutWebTools = null, string? ProcessHost = null,
    long? ContextSize = null)
{
    /// <summary>Gets the web tools of the CLI a session does without, separated by commas, or null for none.</summary>
    /// <param name="disabledTools">The tools the session does without.</param>
    public static string? WebToolsOf(IReadOnlySet<string>? disabledTools)
    {
        if (disabledTools is null) return null;
        var search = disabledTools.Contains(AgentToolPatterns.WebSearchToolName);
        var fetch = disabledTools.Contains(AgentToolPatterns.WebFetchToolName);
        return search && fetch ? "WebSearch,WebFetch" : search ? "WebSearch" : fetch ? "WebFetch" : null;
    }
}

/// <summary>
/// Builds the command line and the environment of a CLI process.
/// </summary>
internal static class ClaudeCodeLauncher
{
    /// <summary>The name of the MCP server through which the CLI reaches the tools of CodeAlta.</summary>
    public const string McpServerName = "codealta";

    /// <summary>The prefix the CLI gives the tools of that server.</summary>
    public const string McpToolPrefix = "mcp__" + McpServerName + "__";

    // The tools that start agents inside the CLI: a subagent, by its name and by the one older versions gave it,
    // and the agents of a workflow script.
    private const string SubagentTools = "Agent,Task,Workflow";

    /// <summary>
    /// The tools of the CLI that run commands, as version 2.1.309 names them: the shell, the PowerShell of Windows,
    /// and Monitor, which runs a command in the background and reads its output. A session that does without
    /// <c>shell_command</c> is started without them.
    /// </summary>
    public const string ShellTools = "Bash,PowerShell,Monitor";

    /// <summary>
    /// The other tools a CLI without its shell is started without, beside its subagents: a worktree it would create
    /// runs the hooks of git, a skill may declare hooks, and a message to another session of Claude Code on this
    /// computer would have it run what this one does not.
    /// </summary>
    public const string ToolsWithoutShell = "EnterWorktree,Skill,SendMessage,RemoteTrigger";

    /// <summary>
    /// The permission mode a CLI without its shell runs in: one in which it asks this side for every edit, which the
    /// write guard of the session checks. The modes that accept edits by themselves, or decide alone, ask instead.
    /// </summary>
    /// <param name="permissionMode">The mode of the session, or <see langword="null" /> for the one of the settings.</param>
    /// <param name="withoutShell">Whether the CLI runs without its shell.</param>
    public static string? PermissionMode(string? permissionMode, bool withoutShell)
        => withoutShell && permissionMode is "acceptEdits" or "auto" or "bypassPermissions" ? "default" : permissionMode;

    // The server is served by this process over the control protocol: nothing is listening anywhere.
    private const string McpConfig = "{\"mcpServers\":{\"" + McpServerName + "\":{\"type\":\"sdk\",\"name\":\"" + McpServerName + "\"}}}";

    // Variables that describe the Claude Code session CodeAlta itself may have been started from. They are not
    // the user's configuration: authentication and provider variables are inherited untouched, but for the API key
    // the caller decides about (ClaudeCodeApiKey).
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

    /// <summary>
    /// Creates the command line and the environment of a CLI process.
    /// </summary>
    /// <param name="executable">The CLI.</param>
    /// <param name="options">The options of the provider.</param>
    /// <param name="key">What the process is started with.</param>
    /// <param name="newSessionId">The identifier of the conversation that starts, when none is resumed.</param>
    /// <param name="resumeSessionId">The conversation to resume, or <see langword="null" />.</param>
    /// <param name="withTools">Whether the CLI is given the tools of CodeAlta.</param>
    /// <param name="showReasoning">Whether the CLI writes the summaries of the thinking of the model.</param>
    /// <param name="delegatesToSessions">Whether what the model delegates goes to child sessions of CodeAlta: the CLI then starts without its own subagent tool.</param>
    /// <param name="withoutApiKey">Whether the CLI is started without the API key of the environment, to sign in with the user's login.</param>
    /// <param name="permissionMode">The permission mode of the session, or <see langword="null" /> for the one of the provider.</param>
    /// <param name="forkSession">Whether the resumed conversation is continued as another one, which the CLI names: the resumed one stays as it is.</param>
    /// <param name="resumeAt">The entry of the transcript the resumed conversation is cut after, or <see langword="null" /> to resume all of it.</param>
    public static ClaudeCodeLaunch Create(
        string executable,
        ClaudeCodeModelProviderRuntimeOptions options,
        ClaudeCodeLaunchKey key,
        string? newSessionId,
        string? resumeSessionId,
        bool withTools,
        bool showReasoning = false,
        bool delegatesToSessions = false,
        bool withoutApiKey = false,
        string? permissionMode = null,
        bool forkSession = false,
        string? resumeAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(key);

        // The mode is not part of the key: a running CLI is switched to another one without a restart.
        permissionMode = PermissionMode(string.IsNullOrWhiteSpace(permissionMode) ? options.PermissionMode : permissionMode?.Trim(), key.WithoutShell);

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

        if (!string.IsNullOrWhiteSpace(permissionMode))
        {
            arguments.Add("--permission-mode");
            arguments.Add(permissionMode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(key.Model))
        {
            arguments.Add("--model");
            // The size of context of the session: 1M runs the [1m] form of the model, which a model of 1M by default ignores.
            arguments.Add(key.ContextSize == ClaudeCodeModelCatalog.ExtendedContextSize && !key.Model.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase)
                ? key.Model + "[1m]" : key.Model);
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
            if (forkSession)
            {
                // The session of CodeAlta was forked from another one: the CLI copies the conversation of that one
                // under an identifier it reports, and leaves its transcript untouched.
                arguments.Add("--fork-session");
            }

            if (!string.IsNullOrWhiteSpace(resumeAt))
            {
                // Messages up to and including that entry are kept. The option is not in the help of the CLI; it is
                // the one the Agent SDK gives for its resumeSessionAt. Without --fork-session the entries after it
                // stay in the transcript, off the conversation, from the next prompt on.
                arguments.Add("--resume-session-at");
                arguments.Add(resumeAt.Trim());
            }
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

        // A subagent of the CLI works where the user does not see it: no session, no timeline, nothing to steer or
        // stop. The model has the sessions of CodeAlta for that, and is not offered both. A session without the
        // shell runs no command either.
        var disallowed = new List<string>();
        if (delegatesToSessions || key.WithoutShell) disallowed.Add(SubagentTools);
        if (key.WithoutShell)
        {
            disallowed.Add(ShellTools);
            disallowed.Add(ToolsWithoutShell);
            // Restricted mode leaves out every tool that runs code, ignores the settings files of the user and of the
            // project (their hooks and their helpers run commands), keeps the file tools in the working folders and
            // refuses to bypass permissions. Only the MCP server of CodeAlta is loaded, not those of `.mcp.json`.
            arguments.Add("--restricted");
            arguments.Add("--strict-mcp-config");
        }

        if (key.WithoutWebTools is { Length: > 0 } webTools) disallowed.Add(webTools);
        if (disallowed.Count > 0)
        {
            arguments.Add($"--disallowedTools={string.Join(',', disallowed)}");
        }

        if (showReasoning)
        {
            // Without it the thinking blocks of the model are written empty: the timeline shows their summary.
            arguments.Add("--thinking-display");
            arguments.Add("summarized");
        }

        arguments.AddRange(options.ExtraArguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        // The identity of the session first: what CodeAlta sets below for the CLI itself is not given away.
        key.Environment?.ApplyTo(environment);
        foreach (var name in InheritedSessionVariables)
        {
            environment[name] = null;
        }

        // The traces of CodeAlta cover the runs of the CLI: it does not report them a second time.
        OpenTelemetryEnvironment.HideFromAgent(environment);

        if (withoutApiKey)
        {
            environment[ClaudeCodeApiKey.Variable] = null;
        }

        // The variables the user gave the provider (a token of its account), over those inherited.
        foreach (var (name, value) in options.EnvironmentVariables)
        {
            environment[name] = value;
        }

        // The account of the provider: its login, settings, memory and transcripts are in that folder.
        if (options.ResolveConfigDirectory() is { } configDirectory)
        {
            environment[ClaudeCodeProjectFolders.ConfigDirectoryVariable] = configDirectory;
        }

        // A session of 200K: the CLI keeps a model of 1M tokens to it.
        if (key.ContextSize == ClaudeCodeModelCatalog.StandardContextSize)
        {
            environment[DisableExtendedContextVariable] = "1";
        }

        // The CLI tells this side when a session is idle again, and names CodeAlta in its user agent.
        environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1";
        // A start the CLI refuses for a known reason (a conversation a background session runs, ...) ends with a result
        // that names it, not with its error output alone.
        environment[StartupFailureResultsVariable] = "1";
        environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = string.IsNullOrWhiteSpace(options.ClientApp) ? DefaultClientApp : options.ClientApp.Trim();

        return new ClaudeCodeLaunch(executable, arguments, key.WorkingDirectory, environment);
    }

    /// <summary>The variable that makes the CLI write a result naming why it refused to start.</summary>
    public const string StartupFailureResultsVariable = "CLAUDE_CODE_STARTUP_FAILURE_RESULTS";

    /// <summary>The variable that keeps the models of 1M tokens to 200K in the CLI.</summary>
    public const string DisableExtendedContextVariable = "CLAUDE_CODE_DISABLE_1M_CONTEXT";

    // The hosts of Anthropic the CLI reaches: the API, and the sign-in and refresh of a subscription.
    private static readonly string[] VendorHosts = ["api.anthropic.com", "console.anthropic.com", "platform.claude.com", "claude.ai", "*.claude.ai"];

    // What the account folder holds that the CLI runs or follows later, outside the sandbox too: settings (hooks, the
    // status line, a helper of the API key), instructions, commands, agents, skills, plugins and hook scripts. The
    // sign-in, the transcripts and the state of the CLI stay writable.
    private static readonly string[] ExecutableConfiguration =
        ["settings.json", "settings.local.json", "CLAUDE.md", "commands", "agents", "skills", "plugins", "hooks", "output-styles"];

    /// <summary>
    /// Describes a CLI process for a host that starts it in a sandbox, whose home folder and environment are its own:
    /// the folder of the executable to read, the account folder to write (named explicitly, as the home of the sandbox
    /// has none), the hosts of Anthropic, and the variables of this process that configure the CLI.
    /// </summary>
    public static AgentProcessLaunch ForHost(ClaudeCodeLaunch launch, string executable, ClaudeCodeModelProviderRuntimeOptions options, bool withoutApiKey)
    {
        var environment = new Dictionary<string, string?>(launch.Environment, StringComparer.Ordinal);
        var configRoot = options.ResolveConfigRoot();
        environment[ClaudeCodeProjectFolders.ConfigDirectoryVariable] = configRoot;
        // No update, telemetry or error report: none of them is a host of the sandbox, and an update cannot be written.
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        environment["DISABLE_AUTOUPDATER"] = "1";
        var readOnly = new List<string>();
        AddFolderOf(readOnly, executable);
        var arguments = launch.Arguments.ToList();
        if (OperatingSystem.IsWindows())
        {
            // The shell tools of the CLI do not run in the container of Windows: the bash of Git for Windows (msys2)
            // creates a named object it refuses, and its PowerShell tool reports a failure for every command. The
            // commands run with the shell_command of CodeAlta, in the worker of the session (CommandsThroughCodeAlta).
            var disallowed = arguments.FindIndex(static argument => argument.StartsWith("--disallowedTools=", StringComparison.Ordinal));
            var tools = disallowed < 0 ? [] : arguments[disallowed].Split('=', 2)[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            tools.AddRange(ShellTools.Split(',').Where(tool => !tools.Contains(tool, StringComparer.Ordinal)));
            if (disallowed < 0) arguments.Add($"--disallowedTools={string.Join(',', tools)}");
            else arguments[disallowed] = $"--disallowedTools={string.Join(',', tools)}";
        }
        // The variables that choose the account, the endpoint and the model of the CLI, which the sandbox would not pass
        // otherwise; those the launch sets or removes stay as it says.
        // The variables of the provider pass too, with the values the launch gives them: a sandbox would drop those named
        // like a secret (CLAUDE_CODE_OAUTH_TOKEN) otherwise.
        var passed = System.Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(static name => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
            .Where(name => !environment.ContainsKey(name) && !(withoutApiKey && string.Equals(name, ClaudeCodeApiKey.Variable, StringComparison.OrdinalIgnoreCase)))
            .Concat(options.EnvironmentVariables.Keys.Where(name => environment.TryGetValue(name, out var value) && value is not null))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new AgentProcessLaunch(launch.FileName, arguments, launch.WorkingDirectory, environment)
        {
            ReadOnlyFolders = readOnly,
            ReadWriteFolders = [configRoot],
            ProtectedPaths = [.. ExecutableConfiguration.Select(entry => Path.Combine(configRoot, entry))],
            Hosts = VendorHosts,
            PassedVariables = passed,
        };

        static void AddFolderOf(List<string> folders, string file)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } folder) folders.Add(folder);
            // A CLI installed by a package manager is often a link to the folder of its version.
            try
            {
                if (File.ResolveLinkTarget(file, returnFinalTarget: true) is { } target && Path.GetDirectoryName(target.FullName) is { } real) folders.Add(real);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Gets whether the commands of a CLI started by a host run with the <c>shell_command</c> of CodeAlta instead of its
    /// own shell tools: in the sandbox of Windows, where those do not run.
    /// </summary>
    public static bool CommandsThroughCodeAlta(IAgentProcessHost? host) => host is not null && OperatingSystem.IsWindows();

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

    /// <summary>The name and the version of the application that runs, as <c>codealta/1.2.3</c>.</summary>
    internal static string DefaultClientApp { get; } = CreateDefaultClientApp();

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
