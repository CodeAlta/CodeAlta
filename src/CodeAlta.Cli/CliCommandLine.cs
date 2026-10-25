using XenoAtom.CommandLine;

namespace CodeAlta.Cli;

/// <summary>What the commands of <c>altacli</c> run once their options are parsed.</summary>
internal sealed record CliCommandHandlers(
    Func<CliRunOptions, ValueTask<int>> Run,
    Func<CliProviderOptions, ValueTask<int>> Models,
    Func<CliProviderOptions, ValueTask<int>> AuthCheck);

/// <summary>
/// The command line of <c>altacli</c>: <c>run</c>, <c>alta</c>, <c>models</c> and <c>auth check</c>, with their options.
/// <c>alta</c> passes its arguments through to the <c>alta</c> command of the host, so it is read here
/// (<see cref="TryParseAlta"/>) rather than by the strict parser of the others.
/// </summary>
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
            new CommandUsage("Usage: {NAME} [options] [prompt...]"),
            _,
            "Runs one prompt in a session (new, resumed or named) until the session",
            "and the sessions it started have settled, prints the final answer on",
            "standard output, and exits with a code that tells how the run ended.",
            "Progress and errors go to standard error.",
            _,
            "The prompt is the words given, joined with spaces; `-` alone reads it",
            "from standard input. When standard input is piped and words are given",
            "too, what it holds is appended to them in a <stdin>...</stdin> block.",
            "A byte order mark of standard input is honored and dropped.",
            _,
            "Session and model:",
            { "C|project=", "The {FOLDER} a new session works in; the current folder by default", v => run.Project = v },
            { "m|model=", "The model, as provider:model or provider:model@reasoning", v => run.Model = v },
            { "tier=", "Take the model from a tier: {complex|standard|quick}", v => run.Tier = v },
            { "reasoning=", "The reasoning {EFFORT}, over the one of the model or the tier", v => run.Reasoning = v },
            { "prompt-id=", "The agent prompt: {ID} (default, plan...)", v => run.PromptId = v },
            { "title=", "The title of a new session", v => run.Title = v },
            { "resume=", "Resume the session {ID}, which must exist", v => run.Resume = v },
            { "session-id=", "Open the session {ID}, or create it with this id", v => run.SessionId = v },
            _,
            "Input and output:",
            { "f|file=", "Attach a {FILE} (images included); repeat it for several", v => run.Files.Add(v!) },
            { "format=", "The output: {text|jsonl}; jsonl writes ACP notifications, one per line", v => run.FormatText = v },
            { "output-last-message=", "Also write the final answer to a {FILE}, in UTF-8", v => run.OutputLastMessage = v },
            { "output-schema=", "Ask for a final answer that is a JSON value matching the JSON Schema of a {FILE}", v => run.OutputSchema = v },
            { "timeout=", "Abort the run after a {DURATION}: seconds, or hh:mm:ss", v => run.Timeout = v },
            { "q|quiet", "No progress on standard error", v => run.Quiet = v is not null },
            { "verbose", "Warnings and the log on standard error too", v => run.Verbose = v is not null },
            _,
            "Network and tools:",
            { "offline", "Do not refresh the catalog of models.dev", v => run.Offline = v is not null },
            { "disable-tools=", "Turn tools off by {PATTERN[,PATTERN]} (`web` for every tool that reaches the web); repeatable", v => run.DisableTools.Add(v!) },
            { "no-mcp", "Start no MCP server", v => run.NoMcp = v is not null },
            { "no-plugins", "Start no plugin", v => run.NoPlugins = v is not null },
            { "no-skills", "List no skill to the agents, and offer no skill command", v => run.NoSkills = v is not null },
            _,
        };
        AddStorage(runCommand, run);
        runCommand.Add("<>", "The words of the prompt");
        runCommand.Add(new HelpOption());
        runCommand.Add((_, words) =>
        {
            run.Prompt.AddRange(words);
            return handlers.Run(run);
        });

        var modelsCommand = new Command("models", "List the models of the providers that are ready")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            { "provider=", "Only the models of the provider {ID}", v => models.Provider = v },
            { "format=", "The output: {text|jsonl}", v => models.FormatText = v },
            { "offline", "Do not refresh the catalog of models.dev", v => models.Offline = v is not null },
            { "no-plugins", "Start no plugin (the providers of plugins are then not listed)", v => models.NoPlugins = v is not null },
            { "verbose", "Warnings and the log on standard error too", v => models.Verbose = v is not null },
            _,
        };
        AddStorage(modelsCommand, models);
        modelsCommand.Add(new HelpOption());
        modelsCommand.Add((_, _) => handlers.Models(models));

        var checkCommand = new Command("check", "Tell whether a provider is ready: exit code 0 when it is, 6 when it is not, 2 when it is unknown")
        {
            new CommandUsage("Usage: {NAME} --provider <id> [options]"),
            _,
            { "provider=", "The provider {ID}", v => auth.Provider = v },
            { "format=", "The output: {text|jsonl}", v => auth.FormatText = v },
            { "no-plugins", "Start no plugin (the providers of plugins are then unknown)", v => auth.NoPlugins = v is not null },
            { "verbose", "Warnings and the log on standard error too", v => auth.Verbose = v is not null },
            _,
        };
        AddStorage(checkCommand, auth);
        checkCommand.Add(new HelpOption());
        checkCommand.Add((_, _) => handlers.AuthCheck(auth));

        var authCommand = new Command("auth", "Check the sign-in of the providers")
        {
            new CommandUsage("Usage: {NAME} check --provider <id>"),
            checkCommand,
            new HelpOption(),
        };

        // Read before the parser runs (TryParseAlta): declared here for the help.
        var altaCommand = new Command(AltaCommand, "Run an `alta` command, as the agents do (`altacli alta --help` lists them)")
        {
            new CommandUsage("Usage: {NAME} [options] <alta arguments...>"),
            _,
            "The options of the storage and the configuration (those of run) come",
            "first; the arguments from the first one that is not one of them are the",
            "alta command. Its --stdin reads standard input. The commands that need",
            "a user or a window (ask, review, window...) are not offered.",
            { "<>", "The alta arguments" },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(CliExitCodes.InvalidUsage),
        };

        var app = new CommandApp("altacli", config: new CommandConfig { OutputFactory = static _ => DefaultCommandOutput.Instance })
        {
            new CommandUsage("Usage: {NAME} <command> [options]"),
            _,
            "CodeAlta CLI runs the CodeAlta harness without a window: sessions,",
            "tools, providers, plugins and the alta tool, with full trust and nobody",
            "to ask. It shares the ~/.alta profile with CodeAlta Desktop and TUI.",
            _,
            "Commands:",
            runCommand,
            altaCommand,
            modelsCommand,
            authCommand,
            _,
            "Exit codes: 0 completed, 1 failed, 2 invalid usage or configuration,",
            "3 aborted (signal), 4 timeout, 5 session in use, 6 provider not ready,",
            "7 invalid structured output.",
            _,
            "Documentation: https://codealta.github.io",
            _,
            new HelpOption(),
            new VersionOption(),
        };
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

    // The storage modes and the configuration, shared by the commands that compose a host.
    private static void AddStorage(Command command, CliCommonOptions options)
    {
        command.Add("Storage:");
        command.Add("isolated", "Keep the sessions in ~/.alta/cli, apart from CodeAlta Desktop and TUI", v => options.Isolated = v is not null);
        command.Add("dev", "Use the sessions of the developer instance (alta --dev): ~/.alta/dev", v => options.Developer = v is not null);
        command.Add("profile=", "Use the separate profile {FOLDER} (or CODEALTA_HOME); it must exist", v => options.Profile = v);
        command.Add("ephemeral", "Persist nothing: the sessions go to a temporary folder deleted at the end", v => options.Ephemeral = v is not null);
        command.Add(_);
        command.Add("Configuration:");
        command.Add("ignore-user-config", "Do not read config.toml: only -c and CODEALTA_CONFIG_CONTENT", v => options.IgnoreUserConfig = v is not null);
        command.Add("c|config=", "Override a setting: {KEY=VALUE} in TOML (providers.x.api_key_env=\"MY_KEY\"); repeatable", v => options.Config.Add(v!));
        command.Add("strict-config", "Reject the settings CodeAlta does not know", v => options.StrictConfig = v is not null);
        command.Add(_);
    }
}
