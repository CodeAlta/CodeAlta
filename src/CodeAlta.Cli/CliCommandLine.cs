using XenoAtom.CommandLine;

namespace CodeAlta.Cli;

/// <summary>What the commands of <c>altacli</c> run once their options are parsed.</summary>
internal sealed record CliCommandHandlers(
    Func<CliRunOptions, ValueTask<int>> Run,
    Func<CliAcpOptions, ValueTask<int>> Acp,
    Func<CliProviderOptions, ValueTask<int>> Models,
    Func<CliProviderOptions, ValueTask<int>> AuthCheck);

/// <summary>
/// The command line of <c>altacli</c>: <c>run</c>, <c>acp</c>, <c>alta</c>, <c>models</c> and <c>auth check</c>, with their options.
/// <c>alta</c> passes its arguments through to the <c>alta</c> command of the host, so it is read here
/// (<see cref="TryParseAlta"/>) rather than by the strict parser of the others.
/// </summary>
/// <remarks>
/// The help of each command is the whole reference an agent needs to use it: what it does, its inputs, when it ends, its
/// output, its exit codes, the storage and the configuration, and examples, one fact a line. In the text lines, braces are
/// doubled: single ones name the value of an option.
/// </remarks>
internal static class CliCommandLine
{
    /// <summary>The name of the command that passes its arguments to <c>alta</c>.</summary>
    public const string AltaCommand = "alta";

    private const string _ = "";

    /// <summary>Creates the command tree. A tree is used once: its option callbacks fill the options of one invocation.</summary>
    public static CommandApp Create(CliCommandHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var run = new CliRunOptions();
        var models = new CliProviderOptions();
        var auth = new CliProviderOptions();

        var runCommand = new Command("run", "Run a prompt to its end, without a user")
        {
            new CommandUsage("Usage: {NAME} [options] <prompt words...>"),
            new CommandUsage("Usage: {NAME} [options] - < prompt.md"),
            new CommandUsage("Usage: <command> | {NAME} [options] [prompt words...]"),
            _,
            "Runs one prompt in a session (new, --resume, --session-id), waits until it",
            "has settled, writes the final answer, and exits with a code (below).",
            _,
            "Input:",
            "  The prompt is the words, joined with spaces; `-` alone reads it from stdin.",
            "  Piped stdin and words: stdin is appended in a <stdin>...</stdin> block.",
            "  Piped stdin and no words: stdin is the prompt. A byte order mark is dropped.",
            "  -f attaches a file, a folder or an image by its path.",
            "Run:",
            "  Ends when settled: the session, the sessions it started (sub-agents), its",
            "  queued prompts and its background jobs have nothing left to do.",
            "  Full trust: no permission prompt, no question to a user (the agents are",
            "  told to decide); the alta commands of a user or a window (ask, review,",
            "  reminder, window, ui...) are left out. Run it where files may change.",
            "  Ctrl+C or SIGTERM aborts the run (exit 3); a second one exits at once.",
            "  A new session takes -m, else --tier, else the standard tier, else the",
            "  default provider; a resumed one keeps its model unless -m or --tier.",
            "  The programs it starts get CODEALTA_SESSION_ID, CODEALTA_MODEL",
            "  (provider:model@effort) and AI_AGENT=codealta in their environment.",
            "Output, --format text (default):",
            "  stdout: the final answer alone, once completed; nothing otherwise.",
            "  stderr: progress (`> <tool> <target>`, `[<id>] ` for a sub-agent) and",
            "  errors; -q hides the progress, --verbose adds warnings and the log.",
            "Output, --format jsonl: stdout has one ACP JSON-RPC notification per line:",
            "  1. _codealta/session (no line when the run gets no session): schemaVersion,",
            "     sessionId, created, cwd, provider, providerType (anthropic, codex,",
            "     claude-code...), model, reasoning, agentPrompt, tier, storage,",
            "     disabledFeatures (the parts of the agent prompt left out).",
            "  2. session/update, params {{sessionId, update}}; update.sessionUpdate is",
            "     agent_message_chunk, agent_thought_chunk, tool_call (kind, title,",
            "     rawInput), tool_call_update (status, content), plan or usage_update.",
            "     kind: read, edit, delete, move, search, execute, think, fetch,",
            "     switch_mode or other (MCP and plugin tools).",
            "  3. _codealta/session_started (parentSessionId, title; the sub-agent's lines",
            "     carry its sessionId), _codealta/compaction (phase), _codealta/retry,",
            "     _codealta/fallback (from, to, reason), _codealta/error (kind, message;",
            "     events_dropped: chunks were lost, the whole answer is in finalMessage).",
            "  4. _codealta/result, always last: sessionId, runId, stopReason (end_turn,",
            "     cancelled, null), status, exitCode, finalMessage, abortReason, failure",
            "     {{kind, message}}, usage {{inputTokens, outputTokens, totalTokens,",
            "     cachedReadTokens, cachedWriteTokens, thoughtTokens}}, cost {{amount,",
            "     currency}}, durationMs. Unknown is never 0: usage and cost are null",
            "     when not reported, and a count of usage not reported is left out. A",
            "     cost the provider does not report is estimated in USD from the public",
            "     price of the model (_meta.codealta.estimated: true).",
            "  status: completed, failed, aborted, timeout, provider_not_ready,",
            "  session_in_use, invalid_output, invalid_usage.",
            "  --output-last-message also writes the final answer to a file (UTF-8); when",
            "  it cannot, a completed run fails (exit 1, failure kind output_write).",
            "  --output-schema: the answer must be one JSON value (alone or in a ```json",
            "  block) matching the schema. Checked: type, properties, required,",
            "  additionalProperties, items, enum, const, minimum, maximum,",
            "  exclusiveMinimum, exclusiveMaximum, minLength, maxLength, pattern,",
            "  minItems, maxItems; title, description and other annotations are allowed.",
            "  A schema with another keyword ($ref, allOf, format...) is exit 2. The",
            "  value alone is then the output and finalMessage; a mismatch is exit 7.",
            "Exit codes:",
            "  0 completed",
            "  1 failed: an error of the provider or of a tool, no answer, or the answer",
            "    not written to --output-last-message",
            "  2 invalid usage or configuration, --resume of an unknown session, or a",
            "    required plugin or MCP server that did not start",
            "  3 aborted (signal)          4 --timeout elapsed",
            "    (the aborted run is waited for 30 s at most: a provider that does not",
            "    stop does not hold the exit)",
            "  5 session in use by CodeAlta Desktop, TUI or another altacli",
            "  6 provider not ready: none, a sign-in or a key needed, model not offered",
            "  7 final answer not matching --output-schema",
            _,
            "Session and model:",
            { "C|project=", "The {FOLDER} of a new session (default: cwd)", v => run.Project = v },
            { "m|model=", "The {MODEL}: provider:model or provider:model@effort", v => run.Model = v },
            { "tier=", "The model of a {TIER}: complex, standard or quick", v => run.Tier = v },
            { "reasoning=", "The {EFFORT}: none, minimal, low ... xhigh, max", v => run.Reasoning = v },
            { "prompt-id=", "The agent prompt {ID}: default, plan...", v => run.PromptId = v },
            { "title=", "The {TITLE} of a new session", v => run.Title = v },
            { "resume=", "Resume the session {ID}; it must exist (else exit 2)", v => run.Resume = v },
            { "session-id=", "Open the session {ID}, or create it with this id", v => run.SessionId = v },
            _,
            "Input and output:",
            { "f|file=", "Attach a {PATH}; repeatable", v => run.Files.Add(v!) },
            { "format=", "The output {FORMAT}: text or jsonl", v => run.FormatText = v },
            { "output-last-message=", "Also write the final answer to {FILE}", v => run.OutputLastMessage = v },
            { "output-schema=", "Ask for JSON matching the JSON Schema of {FILE}", v => run.OutputSchema = v },
            { "timeout=", "Abort after {DURATION}: seconds or hh:mm:ss (exit 4)", v => run.Timeout = v },
            { "q|quiet", "No progress on stderr", v => run.Quiet = v is not null },
            { "verbose", "Warnings and the log on stderr too", v => run.Verbose = v is not null },
            _,
        };
        AddTools(runCommand, run, run.DisableTools);
        AddFeatures(runCommand, run,
            "  Off by default: scratchpad, notes, tasks, review, mode-handoff; also",
            "  session-title with --ephemeral.");
        AddStorage(runCommand, run);
        runCommand.Add("<>", "The words of the prompt");
        runCommand.Add(new HelpOption());
        runCommand.Add(_);
        AddExamples(runCommand,
            "altacli run \"Fix the failing test in Parser.Tests\"",
            "git diff | altacli run -m anthropic:claude-opus-5-5@high \"Review this diff\"",
            "altacli run --format jsonl --timeout 600 - < task.md > events.jsonl",
            "altacli run --ephemeral --output-schema s.json \"List the TODOs\" > todos.json");
        runCommand.Add((_, words) =>
        {
            run.Prompt.AddRange(words);
            return handlers.Run(run);
        });

        var acp = new CliAcpOptions();
        var acpCommand = new Command("acp", "Serve CodeAlta as an ACP agent over stdio")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "An agent of the Agent Client Protocol (agentclientprotocol.com), version 1:",
            "JSON-RPC lines, requests on stdin, responses and notifications on stdout.",
            "Log: logs/altacli.log of the state root; errors on stderr.",
            "Editor, e.g. Zed settings.json \"agent_servers\" (add options to args):",
            "  \"CodeAlta\": {{ \"command\": \"altacli\", \"args\": [\"acp\"] }}",
            _,
            "Advertised:",
            "  loadSession; sessionCapabilities list, resume, fork, close; no authMethods.",
            "  prompt: text, resource_link (file:// attached), image, resource (embedded).",
            "  mcpServers: stdio, http, sse; kept in memory, active for their session.",
            "  agentCapabilities._meta.codealta: schemaVersion, notifications (below).",
            "Sessions:",
            "  session/new: cwd absolute; answers sessionId, modes, configOptions.",
            "  modes = agent prompts (default, plan...); configOptions: mode, model",
            "  (provider:model of ready providers), thought_level (efforts of the model).",
            "  set_mode, set_config_option: for the next prompt.",
            "  session/load replays the history (user, agent, thoughts, tool calls).",
            "  session/list: own sessions (not sub-agents), newest first, cwd filter.",
            "  Visible to CodeAlta Desktop/TUI unless --isolated or --profile.",
            "Prompts:",
            "  Answered once settled (sub-agents, jobs): every update comes before.",
            "  stopReason end_turn | cancelled (session/cancel stops the prompt in flight,",
            "  before it reaches the provider if it can; no prompt, no effect); usage;",
            "  _meta.codealta: runId, abortReason, cost, durationMs. One prompt per",
            "  session at a time.",
            "  Full trust: no session/request_permission, no fs/* or terminal/* calls.",
            "  session/update as altacli run --format jsonl (see altacli run --help).",
            "  _codealta/session_started|compaction|retry|fallback|error notifications",
            "  (params.sessionId); sub-agent sessions get no session/update.",
            "Errors (error.data.kind):",
            "  -32000 auth_required: provider not ready; altacli auth check --provider <id>.",
            "  -32603 session_in_use (held elsewhere or open here); required_unavailable",
            "  (the config of the session's folder requires what does not start); a run",
            "  that failed",
            "  (rate_limited, quota_exhausted, unavailable, model_unavailable, other).",
            "  -32600 session_busy; -32602 invalid params, mcp_off; -32002 unknown session.",
            "Exit codes:",
            "  0 stdin closed or a signal (the runs still going are aborted)",
            "  1 the host could not start   2 invalid usage or configuration, or a",
            "  required plugin or MCP server that did not start",
            _,
            "New sessions:",
            { "C|project=", "The {FOLDER} of the project settings (default: cwd)", v => acp.Project = v },
            { "m|model=", "The {MODEL}: provider:model or provider:model@effort", v => acp.Model = v },
            { "tier=", "The model of a {TIER}: complex, standard or quick", v => acp.Tier = v },
            { "reasoning=", "The {EFFORT}: none, minimal, low ... xhigh, max", v => acp.Reasoning = v },
            { "prompt-id=", "The agent prompt {ID}: default, plan...", v => acp.PromptId = v },
            { "verbose", "Warnings and the log on stderr too", v => acp.Verbose = v is not null },
            _,
        };
        AddTools(acpCommand, acp, acp.DisableTools);
        AddFeatures(acpCommand, acp,
            "  Off by default: review (its commands are not offered); also",
            "  session-title with --ephemeral.");
        AddStorage(acpCommand, acp);
        acpCommand.Add(new HelpOption());
        acpCommand.Add(_);
        AddExamples(acpCommand,
            "altacli acp",
            "altacli acp --isolated --tier complex",
            "altacli acp --profile ./ci-profile --disable-tools web");
        acpCommand.Add((_, _) => handlers.Acp(acp));

        var modelsCommand = new Command("models", "List the models of the providers that are ready")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            new CommandUsage("Usage: {NAME} --provider <id> --format jsonl"),
            _,
            "Lists the models of every ready provider, or of --provider, one per line:",
            "  text: `provider:model  Display name`; the reference is what -m takes.",
            "  jsonl: {{provider, model, ref, name, default}}.",
            "  A provider that is not ready is named on stderr, with its state.",
            "Exit codes:",
            "  0 listed   1 failed   2 unknown or turned off --provider, invalid option",
            "  6 the --provider named is not ready",
            _,
            "Options:",
            { "provider=", "Only the models of the provider {ID}", v => models.Provider = v },
            { "format=", "The output {FORMAT}: text or jsonl", v => models.FormatText = v },
            { "offline", "Do not refresh the catalog of models.dev", v => models.Offline = v is not null },
            { "no-plugins", "Start no plugin (nor list their providers)", v => models.NoPlugins = v is not null },
            { "verbose", "Warnings and the log on stderr too", v => models.Verbose = v is not null },
            _,
        };
        AddStorage(modelsCommand, models);
        modelsCommand.Add(new HelpOption());
        modelsCommand.Add(_);
        AddExamples(modelsCommand,
            "altacli models",
            "altacli models --provider anthropic --format jsonl",
            "CODEALTA_HOME=./ci-profile altacli models --offline");
        modelsCommand.Add((_, _) => handlers.Models(models));

        var checkCommand = new Command("check", "Tell whether a provider is ready (exit 0, 6, 2)")
        {
            new CommandUsage("Usage: {NAME} --provider <id> [options]"),
            _,
            "Prepares one provider (its sign-in, its key, its models) and says whether",
            "a session can use it:",
            "  text: `<id>: ready - Ready: <n> model(s).` or `<id>: not ready - <reason>`.",
            "  jsonl: {{provider, ready, availability, message, models, exitCode}}.",
            "Exit codes:",
            "  0 ready   6 not ready: a sign-in or a key needed, turned off, failed",
            "  2 unknown provider, no --provider, invalid option   1 failed",
            _,
            "Options:",
            { "provider=", "The provider {ID}, as in config.toml", v => auth.Provider = v },
            { "format=", "The output {FORMAT}: text or jsonl", v => auth.FormatText = v },
            { "no-plugins", "Start no plugin (their providers are unknown)", v => auth.NoPlugins = v is not null },
            { "verbose", "Warnings and the log on stderr too", v => auth.Verbose = v is not null },
            _,
        };
        AddStorage(checkCommand, auth);
        checkCommand.Add(new HelpOption());
        checkCommand.Add(_);
        AddExamples(checkCommand,
            "altacli auth check --provider anthropic",
            "altacli auth check --provider codex --format jsonl",
            "altacli auth check --provider claude --ignore-user-config \\",
            "    -c providers.claude.type=anthropic -c providers.claude.api_key_env=MY_KEY");
        checkCommand.Add((_, _) => handlers.AuthCheck(auth));

        var authCommand = new Command("auth", "Check that a provider is ready (auth check)")
        {
            new CommandUsage("Usage: {NAME} check --provider <id> [options]"),
            _,
            "Sign-ins are made in CodeAlta Desktop or TUI, or by the CLI of the provider;",
            "keys come from config.toml or from the variable named by api_key_env.",
            _,
            "Commands:",
            checkCommand,
            _,
            new HelpOption(),
        };

        // Read before the parser runs (TryParseAlta): declared here for the help, which CliApplication writes before the
        // help of the alta tool itself.
        var alta = new CliAltaOptions();
        var altaCommand = new Command(AltaCommand, "Run an alta command, as the agents do")
        {
            new CommandUsage("Usage: {NAME} [altacli options] <alta arguments...>"),
            new CommandUsage("Usage: {NAME} <alta command> --help"),
            _,
            "Runs one command of the alta tool of the agents, as a caller that belongs",
            "to no session, and writes its output: JSONL records, alta.result first.",
            "The options of altacli below come first; the alta command starts at the",
            "first other argument, or after `--`. Its --stdin reads standard input.",
            "Offered: version, project, space, session, sandbox, job, skill, tool,",
            "provider, model, prompt, plugin, task, plan, bookmark, issue, notes, and",
            "the commands of plugins (mcp...). Left out: those of a user or a window",
            "(ask, review, reminder, window, ui...). alta mcp server",
            "add/remove/enable/disable fail: the configuration files are not written.",
            "Exit codes: the one of the alta command (0 succeeded); 2 for an invalid",
            "altacli option.",
            _,
            "Options:",
            { "C|project=", "The {FOLDER} the command runs for (default: cwd)", v => alta.Project = v },
            { "offline", "Do not refresh the catalog of models.dev", v => alta.Offline = v is not null },
            { "no-mcp", "Start no MCP server (no mcp command)", v => alta.NoMcp = v is not null },
            { "no-plugins", "Start no plugin (no command of a plugin)", v => alta.NoPlugins = v is not null },
            { "no-skills", "Offer no skill command", v => alta.NoSkills = v is not null },
            { "q|quiet", "No progress on stderr", v => alta.Quiet = v is not null },
            { "verbose", "Warnings and the log on stderr too", v => alta.Verbose = v is not null },
            _,
        };
        AddStorage(altaCommand, alta);
        altaCommand.Add("<>", "The alta arguments");
        altaCommand.Add(new HelpOption());
        altaCommand.Add(_);
        AddExamples(altaCommand,
            "altacli alta session list --state all",
            "altacli alta session report <session-id> --include=result,metrics",
            "altacli alta model list",
            "altacli alta --isolated mcp list");
        altaCommand.Add((_, _) => ValueTask.FromResult(CliExitCodes.InvalidUsage));

        var app = new CommandApp("altacli", config: new CommandConfig { OutputFactory = static _ => DefaultCommandOutput.Instance })
        {
            new CommandUsage("Usage: {NAME} <command> [options]"),
            new CommandUsage("Usage: {NAME} <command> --help"),
            _,
            "CodeAlta without a window: the agents, tools, providers, skills, MCP servers",
            "and plugins of CodeAlta Desktop and TUI, on their ~/.alta profile, in full",
            "trust (no permission prompt, no question to a user).",
            _,
            "Commands:",
            runCommand,
            acpCommand,
            altaCommand,
            modelsCommand,
            authCommand,
            _,
            "Exit codes:",
            "  0 completed   1 failed   2 invalid usage or configuration, or a required",
            "  plugin or MCP server did not start   3 aborted (signal)   4 timeout",
            "  5 session in use   6 provider not ready   7 invalid structured output",
            _,
            "`altacli <command> --help` for the details: input, output, storage, config.",
            "Documentation: https://codealta.github.io/docs/command-line/",
            _,
            new HelpOption(),
            new VersionOption(),
            _,
        };
        AddExamples(app,
            "altacli run \"Fix the failing test\"",
            "altacli run --format jsonl - < task.md");
        return app;
    }

    /// <summary>
    /// Reads the arguments of <c>altacli alta</c>: the common options first, then the <c>alta</c> arguments, from the first
    /// one that is not a common option (or after <c>--</c>).
    /// </summary>
    /// <returns>False with a message when an option lacks its value.</returns>
    public static bool TryParseAlta(IReadOnlyList<string> args, out CliAltaOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new CliAltaOptions();
        error = null;
        var index = 0;
        for (; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--")
            {
                index++;
                break;
            }

            if (!TryTakeCommonOption(options, args, ref index, out var taken, out error))
            {
                return false;
            }

            if (!taken)
            {
                break;
            }
        }

        options.Arguments.AddRange(args.Skip(index));
        return true;
    }

    // One option of the storage and the configuration, with its value when it has one.
    private static bool TryTakeCommonOption(CliCommonOptions options, IReadOnlyList<string> args, ref int index, out bool taken, out string? error)
    {
        error = null;
        taken = true;
        var (name, inline) = Split(args[index]);
        switch (name)
        {
            case "--isolated": options.Isolated = true; return true;
            case "--dev": options.Developer = true; return true;
            case "--ephemeral": options.Ephemeral = true; return true;
            case "--ignore-user-config": options.IgnoreUserConfig = true; return true;
            case "--strict-config": options.StrictConfig = true; return true;
            case "--offline": options.Offline = true; return true;
            case "--no-mcp": options.NoMcp = true; return true;
            case "--no-plugins": options.NoPlugins = true; return true;
            case "--no-skills": options.NoSkills = true; return true;
            case "--verbose": options.Verbose = true; return true;
            case "-q" or "--quiet": options.Quiet = true; return true;
            case "--profile" or "-C" or "--project" or "-c" or "--config":
                string? value = inline;
                if (value is null)
                {
                    if (index + 1 >= args.Count)
                    {
                        error = $"Missing value for option `{name}`.";
                        return false;
                    }

                    value = args[++index];
                }

                if (name == "--profile") options.Profile = value;
                else if (name is "-C" or "--project") options.Project = value;
                else options.Config.Add(value);
                return true;
            default:
                taken = false;
                return true;
        }

        static (string Name, string? Value) Split(string arg)
        {
            var equals = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            return equals < 0 ? (arg, null) : (arg[..equals], arg[(equals + 1)..]);
        }
    }

    // The tools and the plugins of the sessions: run and acp.
    private static void AddTools(Command command, CliCommonOptions options, List<string> disabledTools)
    {
        command.Add("Tools and plugins:");
        command.Add("  --disable-tools: tool names without case, `*` for any characters, comma");
        command.Add("  separated: `web` = webget, web_search, web_fetch; `mcp__*` = the MCP tools;");
        command.Add("  `alta`; `shell_command`. The plugins made for altacli run: Git, Jira, MCP,");
        command.Add("  ACP and the user's plugins that declare it.");
        command.Add("offline", "Do not refresh the catalog of models.dev", v => options.Offline = v is not null);
        command.Add("disable-tools=", "Turn off the tools of {PATTERNS}; repeatable", v => disabledTools.Add(v!));
        command.Add("no-mcp", "Start no MCP server", v => options.NoMcp = v is not null);
        command.Add("no-plugins", "Start no plugin (nor MCP)", v => options.NoPlugins = v is not null);
        command.Add("no-skills", "List and offer no skill to the agents", v => options.NoSkills = v is not null);
        command.Add("code-mode:", "Code Mode {MODE} on|only|off over the settings", v => options.CodeMode = v ?? "on");
        command.Add("sandbox=", "Run the tools in the sandbox {PROFILE} (or off)", v => options.Sandbox = v);
        command.Add(_);
    }

    // The parts of the agent prompt the sessions keep or leave out (CliPromptFeatures): run and acp, with their defaults.
    private static void AddFeatures(Command command, CliCommonOptions options, params string[] defaults)
    {
        command.Add("Agent prompt:");
        command.Add("  --enable, --disable: parts of the agent prompt, comma separated, or `all`:");
        command.Add("  scratchpad (a copy of a long request), notes (sticky notes), tasks");
        command.Add("  (follow-up tasks), review (by the user), sub-agents, mode-handoff (Plan");
        command.Add("  mode, the list of agent prompts), session-title.");
        foreach (var line in defaults) command.Add(line);
        command.Add("enable=", "Keep the prompt parts {NAMES}; repeatable", v => options.EnableFeatures.Add(v!));
        command.Add("disable=", "Leave out the prompt parts {NAMES}; repeatable", v => options.DisableFeatures.Add(v!));
        command.Add(_);
    }

    // The storage modes and the configuration, shared by the commands that compose a host.
    private static void AddStorage(Command command, CliCommonOptions options)
    {
        command.Add("Storage (one of; the default is visible):");
        command.Add("  visible: the sessions of ~/.alta, shared with CodeAlta Desktop and TUI. A");
        command.Add("  session the run writes (new, resumed, or a sub-agent) gets a marker");
        command.Add("  (sessions/markers) that makes them list it, and a lease while it runs");
        command.Add("  (sessions/leases/<id>.lease): Desktop shows it \"Running in altacli\",");
        command.Add("  read-only. A session leased elsewhere is exit 5.");
        command.Add("  Every mode: a private database, no alta.lock, no project registered.");
        command.Add("  Written: sessions/ and logs/altacli.log of the state root; auth/ and");
        command.Add("  cache/ of the profile, by providers. config.toml is never written.");
        command.Add("  --profile: its own config.toml, auth/, skills, sessions; exit 2 if absent.");
        command.Add("isolated", "State root ~/.alta/cli, apart from the apps", v => options.Isolated = v is not null);
        command.Add("profile=", "A separate profile {FOLDER} (or CODEALTA_HOME)", v => options.Profile = v);
        command.Add("ephemeral", "State root in a temporary folder deleted at the end", v => options.Ephemeral = v is not null);
        command.Add("dev", "The sessions of the developer instance, ~/.alta/dev", v => options.Developer = v is not null);
        command.Add(_);
        command.Add("Configuration (read, never written):");
        command.Add("  Precedence: config.toml < CODEALTA_CONFIG_CONTENT (TOML text) < each -c, in");
        command.Add("  order; the project's .alta/config.toml applies as in the apps.");
        command.Add("  -c KEY=VALUE: a dotted TOML key and a TOML value (a bare word is a string):");
        command.Add("    -c chat.default_provider=claude -c plugins.mcp.enabled=false");
        command.Add("  A provider on a machine without CodeAlta set up (CI, container), its key");
        command.Add("  in the variable ANTHROPIC_API_KEY:");
        command.Add("    -c providers.claude.type=anthropic");
        command.Add("    -c providers.claude.api_key_env=ANTHROPIC_API_KEY");
        command.Add("  [plugins.<id>] and [plugins.mcp.servers.<key>] required = true: run and acp");
        command.Add("  exit 2 when it does not start (--no-plugins, --no-mcp: not checked).");
        command.Add("c|config=", "Set {KEY=VALUE} over config.toml; repeatable", v => options.Config.Add(v!));
        command.Add("ignore-user-config", "Do not read config.toml: only -c and the variable", v => options.IgnoreUserConfig = v is not null);
        command.Add("strict-config", "Reject the settings CodeAlta does not know (exit 2)", v => options.StrictConfig = v is not null);
        command.Add(_);
    }

    private static void AddExamples(Command command, params string[] examples)
    {
        command.Add("Examples:");
        foreach (var example in examples) command.Add("  " + example);
    }
}
