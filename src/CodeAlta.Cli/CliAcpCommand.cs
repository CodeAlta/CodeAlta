using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.LiveTool;

namespace CodeAlta.Cli;

/// <summary>
/// <c>altacli acp</c>: CodeAlta as an agent of the Agent Client Protocol over standard input and output, for an editor or
/// another harness (<see cref="CliAcpAgent"/>). Standard output carries the protocol alone; the log goes to its file and the
/// errors to standard error. The command ends when the client closes standard input (or at a signal): the runs still going
/// are aborted, the sessions closed, and the exit code is 0.
/// </summary>
internal sealed class CliAcpCommand(CliStreams streams, CliOutput output, CliEnvironment environment, CancellationToken processToken)
{
    public async ValueTask<int> RunAsync(CliAcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (streams.StdinStream is not { } input) return Invalid("acp reads its client on standard input, which this run has not.");
        if (!TryReadDefaults(options, out var defaults, out var error)) return Invalid(error!);
        if (!CliPromptFeatures.TryResolve(CliPromptFeatures.AcpDefaults, options.Ephemeral, options.EnableFeatures, options.DisableFeatures,
                out var disabledFeatures, out error)) return Invalid(error!);
        if (options.CodeMode is { } codeMode && !CodeAlta.Catalog.CodeAltaConfigStore.CodeModeModes.Contains(codeMode))
            return Invalid($"'{codeMode}' is not a mode of Code Mode: use on, only or off.");
        var folder = Path.GetFullPath(options.Project ?? environment.CurrentDirectory, environment.CurrentDirectory);
        if (!Directory.Exists(folder)) return Invalid($"The project folder '{folder}' does not exist.");
        if (!CliStorage.TryResolve(options, environment, out var storage, out error)) return Invalid(error!);
        if (!CliConfig.TryCreate(options, environment, storage!.GlobalRoot, out var config, out error)) return Invalid(error!);

        var ownsLogging = environment.InitializeLogging && CliLogging.Initialize(storage.StateRoot, output, options.Verbose);
        try
        {
            await using var host = await CliHost.CreateAsync(new CliHostRequest
            {
                Storage = storage,
                Config = config!,
                ProjectFolder = folder,
                StartPlugins = !options.NoPlugins,
                StartMcp = !options.NoMcp,
                ListsSkills = !options.NoSkills,
                CodeMode = options.CodeMode,
                Offline = options.Offline,
                ChecksRequired = true,
                DisabledPromptFeatures = disabledFeatures,
                // A user works in the editor: the sessions are not told that nobody is there.
                Headless = false,
            }, environment, processToken).ConfigureAwait(false);
            environment.HostCreated?.Invoke(host.Host);
            CliAcpAgent? agent = null;
            try
            {
                await using var server = new AcpAgentServer(input, streams.Stdout, client => agent = new CliAcpAgent(client, host, defaults!, environment.AbortGrace)
                {
                    PromptReading = environment.AcpPromptReading,
                });
                // A signal (or the token of a caller in a test) ends the connection as the end of standard input does; a second signal ends the process.
                Action stop = () => _ = Task.Run(async () => await server.DisposeAsync().ConfigureAwait(false));
                using var signals = environment.HandleSignals ? new CliSignals(stop, () =>
                {
                    output.Error("altacli: a second signal: exiting at once.");
                    output.Flush();
                    Environment.Exit(CliExitCodes.Aborted);
                }) : null;
                using var fromCaller = processToken.Register(stop);
                await server.Completion.ConfigureAwait(false);
            }
            finally
            {
                if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            }

            return CliExitCodes.Completed;
        }
        catch (CliStartupException exception)
        {
            // A required plugin or MCP server did not start: the client sees the process end before it answers.
            output.Error("altacli: " + exception.Message);
            return CliExitCodes.InvalidUsage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !processToken.IsCancellationRequested)
        {
            // A host that could not start (a provider, a plugin, a folder): the client sees the process end.
            if (ownsLogging) CliLogging.ReportFailure(exception, "The ACP server failed.");
            output.Error("altacli: " + exception.Message);
            return CliExitCodes.Failed;
        }
        finally
        {
            if (ownsLogging) CliLogging.Shutdown();
        }
    }

    // What the new sessions start with, checked before anything is started.
    private static bool TryReadDefaults(CliAcpOptions options, out CliAcpDefaults? defaults, out string? error)
    {
        defaults = null;
        ModelTier? tier = null;
        if (options.Tier is not null && (tier = ModelTiers.Parse(options.Tier)) is null)
        {
            error = $"'{options.Tier}' is not a tier: use complex, standard or quick.";
            return false;
        }

        if (options.Model is not null && tier is not null)
        {
            error = "Name either a model or a tier, not both.";
            return false;
        }

        AgentReasoningEffort? reasoning = null;
        if (options.Reasoning is not null && !AltaModelRef.TryParseReasoning(options.Reasoning, out reasoning))
        {
            error = $"'{options.Reasoning}' is not a reasoning effort: use none, minimal, low, medium, high, xhigh or max.";
            return false;
        }

        var patterns = new List<string>();
        foreach (var pattern in options.DisableTools.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (AgentToolPatterns.Validate(pattern) is { } invalid)
            {
                error = $"--disable-tools {pattern}: {invalid}";
                return false;
            }

            patterns.Add(pattern);
        }

        defaults = new CliAcpDefaults
        {
            ModelRef = options.Model,
            Tier = tier,
            Reasoning = reasoning,
            AgentPromptId = options.PromptId,
            DisabledToolPatterns = patterns,
            Sandbox = options.Sandbox,
            SessionOptions = options.NoSubSessions ? new AgentSessionOptions { SubSessions = false } : null,
        };
        error = null;
        return true;
    }

    private int Invalid(string message)
    {
        output.Error("altacli: " + message);
        return CliExitCodes.InvalidUsage;
    }
}
