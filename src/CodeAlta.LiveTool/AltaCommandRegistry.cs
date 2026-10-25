using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAlta.Agent;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

/// <summary>
/// Builds fresh <c>alta</c> command trees and dispatches in-process invocations.
/// </summary>
public sealed class AltaCommandRegistry
{
    private readonly IReadOnlyList<IAltaCommandContributor> _contributors;
    private readonly ConcurrentQueue<AltaCommandRetentionLifetime> _retainedInvocations = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AltaCommandRegistry"/> class.
    /// </summary>
    /// <param name="contributors">Command contributors. When omitted, built-in commands are registered.</param>
    public AltaCommandRegistry(IEnumerable<IAltaCommandContributor>? contributors = null)
    {
        _contributors = (contributors ?? [new BuiltInAltaCommandContributor(), new PluginAltaCommandContributor()]).ToArray();
        Options = AltaCommandRegistryOptions.Default;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AltaCommandRegistry"/> class with the built-in and plugin
    /// commands that <paramref name="options"/> allow.
    /// </summary>
    /// <param name="options">The options of the registry, such as <see cref="AltaCommandRegistryOptions.Cli"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public AltaCommandRegistry(AltaCommandRegistryOptions options)
        : this()
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>Gets the options of the registry.</summary>
    public AltaCommandRegistryOptions Options { get; }

    /// <summary>Gets policies contributed by the registered command contributors.</summary>
    /// <param name="context">The invocation context used to build policies.</param>
    public IReadOnlyList<AltaCommandPolicy> GetPolicies(AltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contributionContext = new AltaCommandContributionContext { Invocation = context };
        return _contributors
            .SelectMany(contributor => contributor is BuiltInAltaCommandContributor
                ? contributor.GetCommandPolicies(contributionContext).Where(policy => Options.AllowsBuiltInRoot(RootOf(policy.Path)))
                : contributor.GetCommandPolicies(contributionContext))
            .OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RootOf(string path)
    {
        var space = path.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? path : path[..space];
    }

    /// <summary>Creates a fresh <see cref="CommandApp"/> for one invocation.</summary>
    /// <param name="context">The invocation context captured by command actions.</param>
    /// <exception cref="InvalidOperationException">Commands require an owned <see cref="InvokeAsync"/> invocation when retention admission is registered.</exception>
    public CommandApp CreateCommandApp(AltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RetentionLifetime is null && context.Services.Get<ISessionRetentionAdmission>() is not null)
            throw new InvalidOperationException("Retention admission requires AltaCommandRegistry.InvokeAsync.");
        var contributionContext = new AltaCommandContributionContext { Invocation = context };
        var app = new CommandApp(
            "alta",
            "Inspect and control the current CodeAlta host through finite JSONL commands.",
            new CommandConfig
            {
                StrictOptionParsing = true,
            })
        {
            new CommandUsage("Usage: {NAME} [options] <command> [command-options]"),
            new HelpOption(),
        };

        foreach (var contributor in _contributors)
        {
            if (contributor is not BuiltInAltaCommandContributor and not PluginAltaCommandContributor)
            {
                context.ClaimUnknownCommandWork();
            }
            var builtIn = contributor is BuiltInAltaCommandContributor;
            foreach (var node in contributor.CreateCommandLineNodes(contributionContext))
            {
                if (builtIn && node is Command { Name: var root } && !Options.AllowsBuiltInRoot(root)) continue;
                if (node is Command command) ObserveCommandOriginals(command, context);
                app.Add(node);
            }
        }

        // An allow-list leaves out the guidance of the roots it removes; without one, the guidance is complete.
        AddRootGuidance(app, Options.AllowedBuiltInRoots is null ? static _ => true : app.SubCommands.ContainsKey);

        return app;
    }

    private static void ObserveCommandOriginals(Command command, AltaCommandContext context)
    {
        if (command.Action is { } action)
        {
            command.Action = async (run, args) =>
            {
                try { return await context.ObserveOriginalAsync(action(run, args).AsTask()).ConfigureAwait(false); }
                catch (Exception failure)
                {
                    context.RetentionLifetime?.ObserveFailure(failure);
                    throw;
                }
            };
        }
        foreach (var child in command.SubCommands.Values) ObserveCommandOriginals(child, context);
    }

    // Each example is written when the tree has its root command.
    private static readonly (string Root, string Line)[] RootGuidanceExamples =
    [
        ("project", "  `alta project current`; `alta project list`"),
        ("space", "  `alta space list`; `alta space show <space>` for the spaces that group the projects"),
        ("ask", "  `alta ask --stdin` to ask the user structured questions, then yield"),
        ("notes", "  `alta notes get`; `alta notes set --stdin`; `alta notes clear`"),
        ("session", "  `alta session current` to get the calling agent session id"),
        ("session", "  `alta session list --project <project> --state all --limit 20`"),
        ("session", "  `alta session result <session-id>`; `alta session metrics <session-id>`"),
        ("session", "  `alta session report <session-id-1> <session-id-2> --include=result,metrics`"),
        ("session", "  `alta session status <session-id>`"),
        ("session", "  `alta session tail <session-id> --last 10 --include assistant`"),
        ("session", "  `alta session create --project <project> --reasoning low`"),
        ("session", "  `alta session create --project <project> --same-model-as <session-id>`"),
        ("prompt", "  `alta prompt list`; `alta prompt create <prompt-id> --scope global --stdin`"),
        ("session", "  `alta session send <session-id> --prompt-id <prompt-id> --stdin`"),
        ("session", "  `alta session send <session-id> --stdin`"),
        ("session", "  `alta session steer <session-id> --message \"...\"`"),
        ("session", "  `alta session request <session-id> --reply-requested --stdin`"),
        ("session", "  `alta session fork <session-id> --before <turn>`; see `alta session turns`"),
        ("reminder", "  `alta reminder create --duration 60 --content \"...\"`; `alta reminder list`"),
        ("job", "  `alta job start --command \"...\"` to run a long command in the background and be sent its result; `alta job list`; `alta job output <job-id>`"),
        ("tool", "  `alta tool status`; `alta tool capability list`"),
    ];

    private static void AddRootGuidance(CommandApp app, Func<string, bool> hasRoot)
    {
        app.Add("");
        app.Add("Guidance: non-help commands return JSONL headed by `alta.result`; help is plain text.");
        app.Add("Use small limits for snapshots. Common examples:");
        foreach (var (root, line) in RootGuidanceExamples)
        {
            if (hasRoot(root)) app.Add(line);
        }

        if (hasRoot("session")) app.Add("Coordinate: delegate project-folder work to project sessions; use `session request`/`message` for peer-agent notes and `session steer` only for active runs.");
        app.Add("Discover: `alta <command> --help` or `alta <command> <subcommand> --help`.");
    }

    /// <summary>Dispatches one in-process invocation.</summary>
    /// <param name="args">Command arguments excluding the executable name.</param>
    /// <param name="context">The command context.</param>
    public async ValueTask<AltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        AltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        using var retentionLifetime = new AltaCommandRetentionLifetime(_retainedInvocations.Enqueue);
        context = context with { RetentionLifetime = retentionLifetime };
        var stopwatch = Stopwatch.StartNew();
        int commandExitCode;
        try
        {
            var parseApp = CreateCommandApp(context);
            var parseResult = parseApp.Parse(args, new CommandRunConfig { Out = TextWriter.Null, Error = TextWriter.Null });
            if (parseResult.HasErrors)
            {
                foreach (var error in parseResult.Errors)
                {
                    AltaJsonlWriter.WriteError(
                        context.Stderr,
                        context.CorrelationId,
                        "usage.invalid",
                        AltaExitCodes.Usage,
                        error.Message,
                        parseResult.ResolvedCommandPath,
                        BuildUsageHint(parseResult.ResolvedCommandPath),
                        ExtractSuggestions(error.Message));
                }

                return CaptureResult(
                    AltaExitCodes.Usage,
                    context,
                    stopwatch.Elapsed,
                    isHelp: false,
                    error: parseResult.Errors[0].Message);
            }

            if (parseResult.HelpRequested)
            {
                var helpApp = CreateCommandApp(context);
                var exitCode = await helpApp.RunAsync(
                        args,
                        new CommandRunConfig { Out = context.Stdout, Error = context.Stderr })
                    .ConfigureAwait(false);
                return CaptureResult(exitCode, context, stopwatch.Elapsed, isHelp: true, error: null);
            }

            // A sandboxed session runs only the commands that keep it in its sandbox: they all run here, outside it.
            if (!AltaSandboxGate.Allows(parseResult.ResolvedCommandPath)
                && await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is { } sandbox)
            {
                var what = $"run `{parseResult.ResolvedCommandPath}`";
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "sandbox.refused", AltaExitCodes.PolicyDenied, AltaSandboxGate.Refusal(sandbox, what));
                return CaptureResult(AltaExitCodes.PolicyDenied, context, stopwatch.Elapsed, isHelp: false, error: what);
            }

            var runApp = CreateCommandApp(context);
            commandExitCode = await runApp.RunAsync(
                    args,
                    new CommandRunConfig { Out = context.Stdout, Error = context.Stderr })
                .ConfigureAwait(false);
            if (commandExitCode == 1 && HasJsonlError(context.Stderr))
            {
                // Built-in handlers return stable exit codes directly. Library action failures default to 1.
                commandExitCode = AltaExitCodes.Failure;
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            commandExitCode = AltaExitCodes.TimeoutOrCancellation;
            AltaJsonlWriter.WriteError(
                context.Stderr,
                context.CorrelationId,
                "runtime.cancelled",
                commandExitCode,
                "The alta command was cancelled.");
        }
        catch (Exception ex) when (CodeAlta.Orchestration.Runtime.SessionRuntimeService.FindSessionInUse(ex) is { } inUse)
        {
            // Another process (the command line, another instance) runs the session: nothing was changed.
            retentionLifetime.ObserveFailure(ex);
            commandExitCode = AltaExitCodes.Unsupported;
            AltaJsonlWriter.WriteError(
                context.Stderr,
                context.CorrelationId,
                "session.inUse",
                commandExitCode,
                inUse.Message);
        }
        catch (Exception ex)
        {
            retentionLifetime.ObserveFailure(ex);
            commandExitCode = AltaExitCodes.Failure;
            AltaJsonlWriter.WriteError(
                context.Stderr,
                context.CorrelationId,
                "runtime.failed",
                commandExitCode,
                ex.Message);
        }

        var firstError = ReadNonEmptyLines(context.Stderr.ToString() ?? string.Empty)
            .FirstOrDefault(line => line.Contains("\"type\":\"alta.error\"", StringComparison.Ordinal));
        return CaptureResult(commandExitCode, context, stopwatch.Elapsed, isHelp: false, error: firstError);
    }

    private static AltaCommandResult CaptureResult(
        int exitCode,
        AltaCommandContext context,
        TimeSpan duration,
        bool isHelp,
        string? error)
        => new()
        {
            ExitCode = exitCode,
            Stdout = context.Stdout.ToString() ?? string.Empty,
            Stderr = context.Stderr.ToString() ?? string.Empty,
            IsHelp = isHelp,
            Truncated = false,
            CorrelationId = context.CorrelationId,
            Duration = duration,
            MaxOutputRecords = context.MaxOutputRecords,
            MaxOutputBytes = context.MaxOutputBytes,
            Error = error,
        };

    private static IEnumerable<string> ReadNonEmptyLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static bool HasJsonlError(TextWriter writer)
        => (writer.ToString() ?? string.Empty).Contains("\"type\":\"alta.error\"", StringComparison.Ordinal);

    private static string BuildUsageHint(string commandPath)
    {
        var normalized = string.IsNullOrWhiteSpace(commandPath) ? "alta" : commandPath.Trim();
        return $"Use `{normalized} --help` for usage.";
    }

    private static IReadOnlyList<string>? ExtractSuggestions(string message)
    {
        const string marker = "Did you mean: ";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var suggestions = message[(index + marker.Length)..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return suggestions.Length == 0 ? null : suggestions;
    }
}
