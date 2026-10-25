using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Cli;

/// <summary>
/// <c>altacli run</c>: one prompt, run to its end by the headless driver in a host of <see cref="CliHost"/>, projected to
/// standard error (text) or standard output (<c>--format jsonl</c>), and an exit code that tells how it ended.
/// </summary>
internal sealed class CliRunCommand(CliStreams streams, CliOutput output, CliEnvironment environment, CancellationToken processToken)
{
    // How long the events of a run that settled are waited for, so that the result is written after them.
    private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(5);

    public async ValueTask<int> RunAsync(CliRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryParseFormat(options.FormatText, out var format))
        {
            return Invalid(null, $"'{options.FormatText}' is not a format: use text or jsonl.");
        }

        var jsonl = format == CliFormat.Jsonl ? new CliJsonlWriter(output) : null;
        if (!TryReadRequest(options, out var request, out var timeout, out var schema, out var error)) return Invalid(jsonl, error!);
        var (prompt, promptError) = await CliPrompt.ReadAsync(options.Prompt, streams, processToken).ConfigureAwait(false);
        if (prompt is null) return Invalid(jsonl, promptError!);
        var (attachments, fileError) = CliPrompt.Attach(options.Files, environment.CurrentDirectory);
        if (attachments is null) return Invalid(jsonl, fileError!);
        if (schema is { } outputSchema) prompt += CliStructuredOutput.Instruction(outputSchema);
        if (!CliStorage.TryResolve(options, environment, out var storage, out error)) return Invalid(jsonl, error!);
        if (!CliConfig.TryCreate(options, environment, storage!.GlobalRoot, out var config, out error)) return Invalid(jsonl, error!);

        var ownsLogging = environment.InitializeLogging && CliLogging.Initialize(storage.StateRoot, output, options.Verbose);
        try
        {
            await using var host = await CliHost.CreateAsync(new CliHostRequest
            {
                Storage = storage,
                Config = config!,
                ProjectFolder = request!.ProjectFolder!,
                StartPlugins = !options.NoPlugins,
                StartMcp = !options.NoMcp,
                ListsSkills = !options.NoSkills,
                Offline = options.Offline,
                ChecksRequired = true,
            }, environment, processToken).ConfigureAwait(false);
            environment.HostCreated?.Invoke(host.Host);
            return await RunAsync(host, request, new HeadlessPrompt(prompt) { Attachments = attachments }, timeout, schema, options, jsonl).ConfigureAwait(false);
        }
        catch (CliStartupException exception)
        {
            // A required plugin or MCP server did not start: the run does not go on with a weaker harness.
            output.Error("altacli: " + exception.Message);
            jsonl?.WriteResult(new CliRunReport("invalid_usage", CliExitCodes.InvalidUsage) { FailureKind = "required_unavailable", FailureMessage = exception.Message });
            return CliExitCodes.InvalidUsage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !processToken.IsCancellationRequested)
        {
            // A host that could not start (a provider, a plugin, a folder) or a run that broke: the stream still ends with its result.
            if (ownsLogging) CliLogging.ReportFailure(exception, "The run failed.");
            output.Error("altacli: " + exception.Message);
            jsonl?.WriteResult(new CliRunReport("failed", CliExitCodes.Failed) { FailureKind = "other", FailureMessage = exception.Message });
            return CliExitCodes.Failed;
        }
        finally
        {
            if (ownsLogging) CliLogging.Shutdown();
        }
    }

    private async Task<int> RunAsync(CliHost host, HeadlessSessionRequest request, HeadlessPrompt prompt, TimeSpan? timeout, JsonElement? schema,
        CliRunOptions options, CliJsonlWriter? jsonl)
    {
        // The sink of the projection: the lines of JSONL, or the progress of text mode once the session is known.
        IAcpEventSink? sink = jsonl;
        CliSessionMarkers? markers = null;
        AcpEventProjector? projector = null;
        var driverOptions = new HeadlessSessionDriverOptions
        {
            Observer = async (item, cancellationToken) =>
            {
                // The root and the sessions it starts, once their journal exists.
                if (Volatile.Read(ref markers) is { } pending && !pending.IsWritten(item.Event.SessionId))
                {
                    await pending.TryWriteAsync(item.Event.SessionId, cancellationToken).ConfigureAwait(false);
                }

                if (Volatile.Read(ref projector) is { } projection) await projection.ProjectAsync(item, cancellationToken).ConfigureAwait(false);
            },
            AbortGrace = environment.AbortGrace ?? new HeadlessSessionDriverOptions().AbortGrace,
        };
        await using var driver = new HeadlessSessionDriver(host.Host, driverOptions);
        var opened = await driver.OpenAsync(request, processToken).ConfigureAwait(false);
        if (opened.Session is not { } session)
        {
            var (status, code) = opened.Status switch
            {
                HeadlessOpenStatus.ProviderNotReady => ("provider_not_ready", CliExitCodes.ProviderNotReady),
                HeadlessOpenStatus.SessionInUse => ("session_in_use", CliExitCodes.SessionInUse),
                _ => ("invalid_usage", CliExitCodes.InvalidUsage),
            };
            output.Error("altacli: " + (opened.Message ?? status));
            jsonl?.WriteResult(new CliRunReport(status, code)
            {
                SessionId = request.ResumeSessionId ?? request.SessionId,
                FailureKind = status,
                FailureMessage = opened.Message,
            });
            return code;
        }

        if (host.Storage.WritesMarkers)
        {
            var created = new CliSessionMarkers(host.Host.CatalogOptions, host.Host.RuntimeService, session.SessionId);
            await created.TryWriteAsync(session.SessionId, processToken).ConfigureAwait(false);
            Volatile.Write(ref markers, created);
        }

        jsonl?.WriteHeader(new CliSessionHeader(session.SessionId, session.Created, session.Descriptor.WorkingDirectory ?? request.ProjectFolder!,
            session.ProviderKey, session.ModelId, ReasoningName(session.ReasoningEffort), session.AgentPromptId,
            session.Tier is { } tier ? ModelTiers.NameOf(tier) : null, host.Storage.Name));
        if (jsonl is null && !options.Quiet) sink = new CliProgressWriter(output, session.SessionId);
        if (sink is not null)
        {
            Volatile.Write(ref projector, new AcpEventProjector(sink, (sessionId, cancellationToken) =>
                new ValueTask<CodeAlta.Catalog.SessionViewDescriptor?>(host.Host.RuntimeService.TryGetResumableSessionAsync(sessionId, cancellationToken))));
        }

        // A signal (or the token of a caller in a test) aborts the run with the reason "signal"; a second signal ends the process.
        // The abort is recorded with its reason first; the token then makes the driver wait (a bounded grace) for the tree to stop.
        using var signalled = new CancellationTokenSource();
        var aborting = 0;
        Task? aborter = null;
        var abort = () =>
        {
            if (Interlocked.Exchange(ref aborting, 1) != 0) return;
            aborter = Task.Run(async () =>
            {
                try
                {
                    await driver.CancelAsync(session, "signal").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                }
                finally
                {
                    try { signalled.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            });
        };
        using var signals = environment.HandleSignals ? new CliSignals(abort, () =>
        {
            output.Error("altacli: a second signal: exiting at once.");
            output.Flush();
            Environment.Exit(CliExitCodes.Aborted);
        }) : null;
        using var fromCaller = processToken.Register(abort);
        var result = await driver.RunAsync(session, prompt, timeout, signalled.Token).ConfigureAwait(false);
        fromCaller.Dispose();
        signals?.Dispose();
        if (Volatile.Read(ref aborter) is { } aborted) await aborted.ConfigureAwait(false);
        if (Volatile.Read(ref aborting) != 0 && result.Status is not HeadlessRunStatus.Timeout)
        {
            // The abort may land as the run ends: the run of a signal is reported as aborted all the same.
            result = result with { Status = HeadlessRunStatus.Aborted, AbortReason = "signal", FailureMessage = "The run was aborted: signal." };
        }

        if (markers is not null) await markers.TryWriteTreeAsync(CancellationToken.None).ConfigureAwait(false);
        using (var flushLimit = new CancellationTokenSource(FlushLimit))
        {
            try
            {
                await driver.FlushEventsAsync(flushLimit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (flushLimit.IsCancellationRequested)
            {
                // The stream dropped the marker of the flush: the result is written without waiting more.
            }
        }

        Volatile.Write(ref projector, null);
        var report = Report(result);
        if (report.Status == "completed" && schema is { } outputSchema)
        {
            var (json, errors) = CliStructuredOutput.Check(result.FinalMessage, outputSchema);
            report = json is not null
                ? report with { FinalMessage = json }
                : report with
                {
                    Status = "invalid_output",
                    ExitCode = CliExitCodes.InvalidOutput,
                    FailureKind = "invalid_output",
                    FailureMessage = "The final answer does not match --output-schema: " + string.Join(" ", errors),
                };
        }

        if (options.OutputLastMessage is { } lastMessagePath && report.FinalMessage is { } lastMessage)
        {
            try
            {
                var path = Path.GetFullPath(lastMessagePath, environment.CurrentDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, lastMessage, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // What the caller reads is missing: a run that completed fails, one that did not keeps its own failure.
                var message = $"The final answer was not written to '{lastMessagePath}': {exception.Message}";
                if (report.Status == "completed") report = report with { Status = "failed", ExitCode = CliExitCodes.Failed, FailureKind = "output_write", FailureMessage = message };
                else output.Error("altacli: " + message);
            }
        }

        if (report.Status != "completed") output.Error($"altacli: {report.Status}: {report.FailureMessage ?? report.AbortReason ?? "the run did not complete."}");
        if (jsonl is not null) jsonl.WriteResult(report);
        else if (report.Status == "completed" && report.FinalMessage is { } answer) output.WriteText(answer);
        return report.ExitCode;
    }

    private static CliRunReport Report(HeadlessRunResult result)
    {
        var (status, code) = result.Status switch
        {
            HeadlessRunStatus.Completed => ("completed", CliExitCodes.Completed),
            HeadlessRunStatus.Aborted => ("aborted", CliExitCodes.Aborted),
            HeadlessRunStatus.Timeout => ("timeout", CliExitCodes.Timeout),
            HeadlessRunStatus.ProviderNotReady => ("provider_not_ready", CliExitCodes.ProviderNotReady),
            HeadlessRunStatus.SessionInUse => ("session_in_use", CliExitCodes.SessionInUse),
            _ => ("failed", CliExitCodes.Failed),
        };
        return new CliRunReport(status, code)
        {
            SessionId = result.SessionId,
            RunId = result.RunId?.Value,
            FinalMessage = result.FinalMessage,
            AbortReason = result.AbortReason,
            FailureKind = result.Status == HeadlessRunStatus.Completed ? null
                : result.FailureKind is { } kind ? AcpEventProjector.FailureKindName(kind)
                : result.Status == HeadlessRunStatus.Failed && result.FailureMessage is not null ? "other" : null,
            FailureMessage = result.Status == HeadlessRunStatus.Completed ? null : result.FailureMessage,
            Usage = result.Usage,
            Duration = result.Duration,
        };
    }

    // What the options ask for, checked before anything is started.
    private bool TryReadRequest(CliRunOptions options, out HeadlessSessionRequest? request, out TimeSpan? timeout, out JsonElement? schema, out string? error)
    {
        request = null;
        timeout = null;
        schema = null;
        ModelTier? tier = null;
        if (options.Tier is not null && (tier = ModelTiers.Parse(options.Tier)) is null)
        {
            error = $"'{options.Tier}' is not a tier: use complex, standard or quick.";
            return false;
        }

        AgentReasoningEffort? reasoning = null;
        if (options.Reasoning is not null && !AltaModelRef.TryParseReasoning(options.Reasoning, out reasoning))
        {
            error = $"'{options.Reasoning}' is not a reasoning effort: use none, minimal, low, medium, high, xhigh or max.";
            return false;
        }

        if (options.Timeout is not null)
        {
            if (!TryParseDuration(options.Timeout, out var parsed))
            {
                error = $"'{options.Timeout}' is not a duration: give seconds (90) or hh:mm:ss (00:01:30).";
                return false;
            }

            timeout = parsed;
        }

        var folder = Path.GetFullPath(options.Project ?? environment.CurrentDirectory, environment.CurrentDirectory);
        if (!Directory.Exists(folder))
        {
            error = $"The project folder '{folder}' does not exist.";
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

        if (options.OutputSchema is { } schemaPath)
        {
            var (loaded, schemaError) = CliStructuredOutput.Load(Path.GetFullPath(schemaPath, environment.CurrentDirectory));
            if (schemaError is not null)
            {
                error = schemaError;
                return false;
            }

            schema = loaded;
        }

        request = new HeadlessSessionRequest
        {
            ProjectFolder = folder,
            ModelRef = options.Model,
            Tier = tier,
            Reasoning = reasoning,
            AgentPromptId = options.PromptId,
            Title = options.Title,
            ResumeSessionId = options.Resume,
            SessionId = options.SessionId,
            DisabledToolPatterns = patterns,
        };
        error = null;
        return true;
    }

    private int Invalid(CliJsonlWriter? jsonl, string message)
    {
        output.Error("altacli: " + message);
        jsonl?.WriteResult(new CliRunReport("invalid_usage", CliExitCodes.InvalidUsage) { FailureKind = "invalid_usage", FailureMessage = message });
        return CliExitCodes.InvalidUsage;
    }

    internal static bool TryParseFormat(string? text, out CliFormat format)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "text":
                format = CliFormat.Text;
                return true;
            case "jsonl":
                format = CliFormat.Jsonl;
                return true;
            default:
                format = CliFormat.Text;
                return false;
        }
    }

    /// <summary>Parses a duration: seconds (<c>90</c>, <c>2.5</c>) or <c>hh:mm:ss</c>; it must be positive.</summary>
    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds is <= 0 or > 864000 || double.IsNaN(seconds)) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return text.Contains(':', StringComparison.Ordinal)
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) && duration > TimeSpan.Zero;
    }

    internal static string? ReasoningName(AgentReasoningEffort? effort) => effort?.ToString().ToLowerInvariant();
}
