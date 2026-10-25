using XenoAtom.CommandLine;

namespace CodeAlta.Cli;

/// <summary>
/// The application <c>altacli</c>: reads the command line and runs one command to its end, with its standard streams and
/// its environment given, so that a test runs it in its own process; <c>Program</c> gives it those of the process.
/// </summary>
internal static class CliApplication
{
    /// <summary>Runs a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="streams">The standard streams.</param>
    /// <param name="environment">The environment.</param>
    /// <param name="cancellationToken">Aborts a run, as a signal does (reason <c>signal</c>, exit code 3).</param>
    /// <returns>The exit code (<see cref="CliExitCodes"/>).</returns>
    public static async Task<int> RunAsync(string[] args, CliStreams streams, CliEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(environment);
        var output = new CliOutput(streams);
        try
        {
            var commands = new CliHostCommands(streams, output, environment, cancellationToken);
            // alta passes its arguments through: altacli alta --help is the help of alta.
            if (args.Length > 0 && args[0] == CliCommandLine.AltaCommand)
            {
                if (!CliCommandLine.TryParseAlta(args[1..], out var alta, out var altaError))
                {
                    output.Error("altacli: " + altaError);
                    return CliExitCodes.InvalidUsage;
                }

                return await commands.AltaAsync(alta).ConfigureAwait(false);
            }

            if (args.Length == 0)
            {
                await WriteHelpAsync(output, streams).ConfigureAwait(false);
                return CliExitCodes.InvalidUsage;
            }

            var handlers = new CliCommandHandlers(
                options => new CliRunCommand(streams, output, environment, cancellationToken).RunAsync(options),
                options => new CliAcpCommand(streams, output, environment, cancellationToken).RunAsync(options),
                commands.ModelsAsync,
                commands.AuthCheckAsync);
            // Parsed first by a tree of its own (its callbacks fill options that are thrown away): an invalid command line
            // has its exit code, 2, and a run in JSONL its result.
            var parse = CliCommandLine.Create(handlers).Parse(args, new CommandRunConfig { Out = TextWriter.Null, Error = TextWriter.Null });
            if (parse.HasErrors)
            {
                foreach (var error in parse.Errors) output.Error("altacli: " + error.Message);
                output.Error("Run altacli --help for the commands, or altacli <command> --help for their options.");
                if (args[0] == "run" && AsksForJsonl(args))
                {
                    new CliJsonlWriter(output).WriteResult(new CliRunReport("invalid_usage", CliExitCodes.InvalidUsage)
                    {
                        FailureKind = "invalid_usage",
                        FailureMessage = string.Join(" ", parse.Errors.Select(static error => error.Message)),
                    });
                }

                return CliExitCodes.InvalidUsage;
            }

            var help = new StringWriter();
            var code = await CliCommandLine.Create(handlers).RunAsync(args, new CommandRunConfig { Out = help, Error = streams.Stderr }).ConfigureAwait(false);
            if (help.GetStringBuilder().Length > 0) output.WriteText(help.ToString());
            return code;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.Error("altacli: aborted.");
            return CliExitCodes.Aborted;
        }
        catch (Exception exception)
        {
            if (environment.InitializeLogging) CliLogging.ReportFailure(exception, "The command failed.");
            output.Error("altacli: " + exception.Message);
            return CliExitCodes.Failed;
        }
        finally
        {
            output.Flush();
        }
    }

    private static async Task WriteHelpAsync(CliOutput output, CliStreams streams)
    {
        var help = new StringWriter();
        await CliCommandLine.Create(new CliCommandHandlers(
                static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0)))
            .RunAsync(["--help"], new CommandRunConfig { Out = help, Error = streams.Stderr }).ConfigureAwait(false);
        output.WriteText(help.ToString());
    }

    private static bool AsksForJsonl(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--format=jsonl" || (args[index] == "--format" && index + 1 < args.Length && args[index + 1] == "jsonl")) return true;
        }

        return false;
    }
}
