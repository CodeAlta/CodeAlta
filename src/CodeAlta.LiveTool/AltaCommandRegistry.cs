using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

/// <summary>
/// Builds fresh <c>alta</c> command trees and dispatches in-process invocations.
/// </summary>
public sealed class AltaCommandRegistry
{
    private readonly IReadOnlyList<IAltaCommandContributor> _contributors;

    /// <summary>
    /// Initializes a new instance of the <see cref="AltaCommandRegistry"/> class.
    /// </summary>
    /// <param name="contributors">Command contributors. When omitted, built-in commands are registered.</param>
    public AltaCommandRegistry(IEnumerable<IAltaCommandContributor>? contributors = null)
    {
        _contributors = (contributors ?? [new BuiltInAltaCommandContributor()]).ToArray();
    }

    /// <summary>Gets policies contributed by the registered command contributors.</summary>
    /// <param name="context">The invocation context used to build policies.</param>
    public IReadOnlyList<AltaCommandPolicy> GetPolicies(AltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contributionContext = new AltaCommandContributionContext { Invocation = context };
        return _contributors
            .SelectMany(contributor => contributor.GetCommandPolicies(contributionContext))
            .OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Creates a fresh <see cref="CommandApp"/> for one invocation.</summary>
    /// <param name="context">The invocation context captured by command actions.</param>
    public CommandApp CreateCommandApp(AltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contributionContext = new AltaCommandContributionContext { Invocation = context };
        var app = new CommandApp(
            "alta",
            "Inspect and control the current CodeAlta host through finite JSONL commands.",
            new CommandConfig
            {
                StrictOptionParsing = true,
            })
        {
            new CommandUsage("Usage: {NAME} <command> [options]"),
            "",
            "Run `alta <group> --help` for narrower help. Non-help commands write JSONL.",
            new HelpOption(),
        };

        foreach (var contributor in _contributors)
        {
            foreach (var node in contributor.CreateCommandLineNodes(contributionContext))
            {
                app.Add(node);
            }
        }

        return app;
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
            return CaptureResult(exitCode, context, isHelp: true, error: null);
        }

        int commandExitCode;
        try
        {
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
        catch (Exception ex)
        {
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
        return CaptureResult(commandExitCode, context, isHelp: false, error: firstError);
    }

    private static AltaCommandResult CaptureResult(
        int exitCode,
        AltaCommandContext context,
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
