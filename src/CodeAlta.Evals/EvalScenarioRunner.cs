using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

/// <summary>A configuration of the grid: an agent prompt and the tier its session starts on, such as <c>adaptive@standard</c>.</summary>
internal sealed record EvalConfiguration(string PromptId, string Tier)
{
    public string Name => $"{PromptId}@{Tier}";

    public static EvalConfiguration Parse(string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) throw new ArgumentException($"A configuration is <prompt-id>@<tier>, such as adaptive@standard: '{value}'.", nameof(value));
        return new EvalConfiguration(value[..at], value[(at + 1)..]);
    }
}

/// <summary>One request to a model, as the <c>_codealta/operation</c> line of a run gives it.</summary>
internal sealed record EvalRequest
{
    public required int Turn { get; init; }
    public required string SessionId { get; init; }
    /// <summary>work (the session of the run), or child (a session it started: a delegation, a review).</summary>
    public required string Role { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public long Input { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long Output { get; init; }
    /// <summary>The first request of the session of the run on a model other than its request before.</summary>
    public bool AfterSwitch { get; init; }
    /// <summary>A request of the session of the run, in a turn that switched, before the switch: the hand-off leg.</summary>
    public bool HandOff { get; set; }
    /// <summary>The price of the request from the snapshot of the report, in US dollars; null when the model has no price.</summary>
    public decimal? Price { get; set; }
}

/// <summary>A turn of a run: how it ended, what its check said.</summary>
internal sealed record EvalTurnResult
{
    public required int Turn { get; init; }
    public required string Status { get; init; }
    public string? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public string? FinalMessage { get; init; }
    /// <summary>The model the turn started on, from the header of its run.</summary>
    public string? StartModel { get; init; }
    public bool Passed { get; init; }
    public string? CheckFailure { get; init; }
    public long DurationMs { get; init; }
    /// <summary>The cost the provider gave for the run of the turn (or the estimate of altacli), as a check of the priced one.</summary>
    public decimal? ProviderCost { get; init; }
    public int ToolCalls { get; init; }
    public int Children { get; init; }
}

/// <summary>A run of a scenario in a configuration: its turns and every request of every leg.</summary>
internal sealed record EvalScenarioRun
{
    public required string Scenario { get; init; }
    public required string Configuration { get; init; }
    public required int Repeat { get; init; }
    public string? SessionId { get; set; }
    public List<EvalTurnResult> Turns { get; init; } = [];
    public List<EvalRequest> Requests { get; init; } = [];
    /// <summary>A run that failed for the harness or the provider (an outage, a limit), not for the mode: it is run again.</summary>
    public string? Invalid { get; set; }
    public int Attempt { get; set; } = 1;
    public string? TranscriptFolder { get; init; }
    public bool Succeeded => Invalid is null && Turns.Count > 0 && Turns.All(static turn => turn.Passed);
    public int Switches => Requests.Count(static request => request.AfterSwitch);
}

/// <summary>
/// Runs a scenario: a fixture project in a git repository, then each turn with <c>altacli run --isolated --format jsonl</c>,
/// the first creating the session on the tier of the configuration and the next ones resuming it on the model it has, the
/// one the agent may have chosen. The tiers are given with <c>-c</c>, so that the settings of the user stay untouched.
/// </summary>
internal sealed class EvalScenarioRunner(string altacli, string scenariosFolder, string promptsFolder, IReadOnlyList<string> tierSettings, TimeSpan timeout)
{
    public async Task<EvalScenarioRun> RunAsync(EvalScenario scenario, EvalConfiguration configuration, int repeat, int attempt, string outputFolder, CancellationToken cancellationToken)
    {
        var project = Path.Combine(Path.GetTempPath(), "codealta-evals", $"{scenario.Id}-{Guid.NewGuid():N}"[..40]);
        var transcripts = Path.Combine(outputFolder, "transcripts", $"{scenario.Id}.{configuration.PromptId}.{configuration.Tier}.{repeat}" + (attempt > 1 ? $".try{attempt}" : string.Empty));
        Directory.CreateDirectory(transcripts);
        var run = new EvalScenarioRun { Scenario = scenario.Id, Configuration = configuration.Name, Repeat = repeat, Attempt = attempt, TranscriptFolder = transcripts };
        try
        {
            Prepare(scenario, project);
            var sessionId = Guid.CreateVersion7().ToString();
            run.SessionId = sessionId;
            string? lastModel = null;
            for (var index = 0; index < scenario.Turns.Count; index++)
            {
                var turn = scenario.Turns[index];
                var transcript = Path.Combine(transcripts, $"turn{index + 1}.jsonl");
                var reading = await RunTurnAsync(turn.Prompt, Arguments(configuration, sessionId, index == 0, project), project, transcript, cancellationToken).ConfigureAwait(false);
                if (InvalidReason(reading) is { } invalid)
                {
                    run.Invalid = $"turn {index + 1}: {invalid}";
                    return run;
                }

                var check = reading.Status == "completed" ? Check(turn, new EvalTurnOutcome(reading.FinalMessage, project, Path.Combine(scenariosFolder, scenario.Id), index + 1)) : reading.Status;
                run.Turns.Add(new EvalTurnResult
                {
                    Turn = index + 1, Status = reading.Status ?? "none", FailureKind = reading.FailureKind, FailureMessage = reading.FailureMessage, FinalMessage = reading.FinalMessage,
                    StartModel = reading.HeaderModel, Passed = check is null, CheckFailure = check, DurationMs = reading.DurationMs, ProviderCost = reading.Cost,
                    ToolCalls = reading.ToolCalls, Children = reading.Children.Count,
                });
                lastModel = AddRequests(run, reading, index + 1, lastModel);
            }

            return run;
        }
        finally
        {
            try
            {
                Directory.Delete(project, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // The fixture, the agent prompts of the repository, and a first commit, so that a turn can commit its work.
    private void Prepare(EvalScenario scenario, string project)
    {
        Directory.CreateDirectory(project);
        EvalScenarios.WriteProject(scenario, scenariosFolder, project);
        var prompts = Path.Combine(project, ".alta", "prompts", "agents");
        Directory.CreateDirectory(prompts);
        foreach (var file in Directory.EnumerateFiles(promptsFolder, "*.prompt.md")) File.Copy(file, Path.Combine(prompts, Path.GetFileName(file)), overwrite: true);
        foreach (var arguments in new[]
        {
            new[] { "init", "-q", "-b", "main" }, ["config", "user.name", "Eval"], ["config", "user.email", "eval@example.com"], ["config", "core.autocrlf", "false"],
            ["add", "-A"], ["commit", "-q", "-m", "Initial version"],
        })
        {
            var (code, output) = EvalScenarios.Run(project, "git", arguments);
            if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
        }
    }

    private IEnumerable<string> Arguments(EvalConfiguration configuration, string sessionId, bool first, string project)
    {
        yield return "run";
        yield return "--isolated";
        yield return "--format";
        yield return "jsonl";
        yield return "--quiet";
        yield return "-C";
        yield return project;
        if (first)
        {
            yield return "--session-id=" + sessionId;
            yield return "--tier=" + configuration.Tier;
        }
        else
        {
            // The model it has: the one of its tier, or the one the agent chose.
            yield return "--resume=" + sessionId;
        }

        yield return "--prompt-id=" + configuration.PromptId;
        yield return "--timeout=" + ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        foreach (var setting in tierSettings)
        {
            yield return "-c";
            yield return setting;
        }

        yield return "-";
    }

    private async Task<TurnReading> RunTurnAsync(string prompt, IEnumerable<string> arguments, string project, string transcript, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(altacli)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = project,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{altacli} did not start.");
        await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
        process.StandardInput.Close();
        // A process the run started and left behind (a test that never ends) can keep the output of altacli open after it
        // exited: the reads stop a few seconds after the exit, whatever is still open.
        using var output = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = process.WaitForExitAsync(cancellationToken);
        _ = exited.ContinueWith(_ => output.CancelAfter(TimeSpan.FromSeconds(10)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        var errors = process.StandardError.ReadToEndAsync(output.Token);
        var reading = new TurnReading();
        await using (var writer = new StreamWriter(transcript, false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(output.Token).ConfigureAwait(false) is { } line)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    reading.Read(line);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        await exited.ConfigureAwait(false);
        string stderr;
        try
        {
            stderr = await errors.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stderr = string.Empty;
        }

        if (stderr.Length > 0) await File.WriteAllTextAsync(Path.ChangeExtension(transcript, ".stderr.txt"), stderr, cancellationToken).ConfigureAwait(false);
        reading.ExitCode = process.ExitCode;
        if (reading.DurationMs == 0) reading.DurationMs = clock.ElapsedMilliseconds;
        return reading;
    }

    // A failure of the harness or of the provider, not of the mode.
    private static string? InvalidReason(TurnReading reading)
    {
        if (reading.Status is null) return $"no result (exit {reading.ExitCode})";
        if (reading.Status is "provider_not_ready" or "session_in_use" or "invalid_usage" or "invalid_output") return reading.Status;
        if (reading.Status == "failed" && reading.FailureKind is "quota_exhausted" or "rate_limited" or "unavailable" or "auth" or "model_unavailable")
        {
            return $"{reading.FailureKind}: {reading.FailureMessage}";
        }

        return null;
    }

    private static string? Check(EvalTurn turn, EvalTurnOutcome outcome)
    {
        try
        {
            return turn.Check(outcome);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    // The requests of a turn, in order: a switch is a request of the session of the run on another model than its request
    // before; in a turn that switched, the requests before the switch are its hand-off leg.
    private static string? AddRequests(EvalScenarioRun run, TurnReading reading, int turn, string? lastModel)
    {
        var added = new List<EvalRequest>();
        foreach (var operation in reading.Operations)
        {
            var root = operation.SessionId == reading.RootSessionId;
            var model = operation.Model ?? (root ? lastModel ?? reading.HeaderModel : null);
            var switched = root && lastModel is not null && model is not null && !string.Equals(model, lastModel, StringComparison.Ordinal);
            added.Add(new EvalRequest
            {
                Turn = turn, SessionId = operation.SessionId, Role = root ? "work" : "child", Provider = reading.Provider, Model = model,
                Input = operation.Input, CacheRead = operation.CacheRead, CacheWrite = operation.CacheWrite, Output = operation.Output, AfterSwitch = switched,
            });
            if (root && model is not null) lastModel = model;
        }

        var lastSwitch = added.FindLastIndex(static request => request.AfterSwitch);
        for (var index = 0; index < lastSwitch; index++)
        {
            if (added[index].Role == "work") added[index].HandOff = true;
        }

        run.Requests.AddRange(added);
        return lastModel;
    }

    /// <summary>What the JSONL of a turn says.</summary>
    private sealed class TurnReading
    {
        public string? RootSessionId { get; private set; }
        public string? Provider { get; private set; }
        public string? HeaderModel { get; private set; }
        public string? Status { get; private set; }
        public string? FailureKind { get; private set; }
        public string? FailureMessage { get; private set; }
        public string? FinalMessage { get; private set; }
        public decimal? Cost { get; private set; }
        public long DurationMs { get; set; }
        public int ExitCode { get; set; }
        public int ToolCalls { get; private set; }
        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);
        public List<(string SessionId, string? Model, long Input, long CacheRead, long CacheWrite, long Output)> Operations { get; } = [];

        public void Read(string line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters)) return;
                var session = String(parameters, "sessionId");
                switch (method.GetString())
                {
                    case "_codealta/session":
                        RootSessionId = session;
                        Provider = String(parameters, "provider");
                        HeaderModel = String(parameters, "model");
                        break;
                    case "_codealta/session_started" when session is not null:
                        Children.Add(session);
                        break;
                    case "_codealta/operation" when session is not null:
                        Operations.Add((session, String(parameters, "model"), Number(parameters, "inputTokens"), Number(parameters, "cachedReadTokens"),
                            Number(parameters, "cachedWriteTokens"), Number(parameters, "outputTokens")));
                        break;
                    case "session/update" when session == RootSessionId && parameters.TryGetProperty("update", out var update) && String(update, "sessionUpdate") == "tool_call":
                        ToolCalls++;
                        break;
                    case "_codealta/result":
                        Status = String(parameters, "status");
                        FinalMessage = String(parameters, "finalMessage");
                        if (parameters.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
                        {
                            FailureKind = String(failure, "kind");
                            FailureMessage = String(failure, "message");
                        }

                        if (parameters.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("amount", out var amount) && amount.TryGetDecimal(out var value))
                        {
                            Cost = value;
                        }

                        DurationMs = Number(parameters, "durationMs");
                        break;
                }
            }
        }

        private static string? String(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long Number(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
}
