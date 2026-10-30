using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

/// <summary>
/// A configuration of the grid: an agent prompt, the tier its session starts on, and what the host does around each turn,
/// such as <c>adaptive@standard</c> or <c>default@quick+escalate+review</c>. The tier <c>router</c> has each turn routed.
/// </summary>
/// <remarks>
/// The policies emulate what CodeAlta could do by itself, without asking the agent:
/// <list type="bullet">
/// <item><c>router</c> (as the tier): a call of the <c>quick</c> model, without tools, reads the request and the requests
/// before it, and picks the tier of the turn;</item>
/// <item><c>escalate</c>: a turn that shows a sign of trouble the host can see (no answer, a refusal to use the tools, its
/// last test run failing) is continued on the next tier, at most up to <c>complex</c>;</item>
/// <item><c>review</c>: a turn that changed code on a tier below <c>complex</c> has its diff read by the <c>complex</c> model,
/// without tools; what it finds is sent to the session as the next prompt, once.</item>
/// </list>
/// With a policy, each user turn starts on its tier again (<c>-m</c>): the host, not the agent, chooses the model.
/// </remarks>
internal sealed record EvalConfiguration(string PromptId, string Tier, IReadOnlySet<string> Policies)
{
    public static readonly IReadOnlySet<string> KnownPolicies = new HashSet<string>(StringComparer.Ordinal) { "escalate", "review" };

    public string Name => $"{PromptId}@{Tier}" + string.Concat(Policies.Order(StringComparer.Ordinal).Select(static policy => "+" + policy));

    public bool Routed => Tier == "router";

    /// <summary>Whether the host chooses the model of each turn.</summary>
    public bool HostDriven => Routed || Policies.Count > 0;

    public static EvalConfiguration Parse(string value)
    {
        var parts = value.Split('+');
        var at = parts[0].LastIndexOf('@');
        if (at <= 0 || at == parts[0].Length - 1) throw new ArgumentException($"A configuration is <prompt-id>@<tier>[+policy...], such as adaptive@standard: '{value}'.", nameof(value));
        var policies = parts.Skip(1).ToHashSet(StringComparer.Ordinal);
        if (policies.FirstOrDefault(policy => !KnownPolicies.Contains(policy)) is { } unknown) throw new ArgumentException($"Unknown policy '{unknown}': escalate, review.", nameof(value));
        return new EvalConfiguration(parts[0][..at], parts[0][(at + 1)..], policies);
    }
}

/// <summary>One request to a model, as the <c>_codealta/operation</c> line of a run gives it.</summary>
internal sealed record EvalRequest
{
    public required int Turn { get; init; }
    public required string SessionId { get; init; }
    /// <summary>
    /// work (the session of the run), child (a session it started: a delegation, a review), or a leg of the host: router,
    /// review (the reviewer), escalation and fix (the turns the host sent to the session after a sign of trouble or a review).
    /// </summary>
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
    /// <summary>The tier the host started the turn on; null when the agent's session chose.</summary>
    public string? Tier { get; init; }
    /// <summary>The signs of trouble the host continued the turn on a stronger tier for.</summary>
    public List<string> Escalations { get; init; } = [];
    /// <summary>What the reviewer found in the change of the turn; null when it was not reviewed, empty when it found nothing.</summary>
    public string? Review { get; init; }
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
internal sealed partial class EvalScenarioRunner(string altacli, string scenariosFolder, string promptsFolder, IReadOnlyList<string> tierSettings,
    IReadOnlyDictionary<string, string> tierModels, TimeSpan timeout)
{
    private static readonly string[] TierOrder = ["quick", "standard", "complex"];

    public async Task<EvalScenarioRun> RunAsync(EvalScenario scenario, EvalConfiguration configuration, int repeat, int attempt, string outputFolder, CancellationToken cancellationToken)
    {
        var project = Path.Combine(Path.GetTempPath(), "codealta-evals", $"{scenario.Id}-{Guid.NewGuid():N}"[..40]);
        var transcripts = Path.Combine(outputFolder, "transcripts", $"{scenario.Id}.{configuration.Name.Replace('@', '.').Replace('+', '.')}.{repeat}" + (attempt > 1 ? $".try{attempt}" : string.Empty));
        Directory.CreateDirectory(transcripts);
        var run = new EvalScenarioRun { Scenario = scenario.Id, Configuration = configuration.Name, Repeat = repeat, Attempt = attempt, TranscriptFolder = transcripts };
        try
        {
            Prepare(scenario, project);
            var sessionId = Guid.CreateVersion7().ToString();
            run.SessionId = sessionId;
            string? lastModel = null;
            var asked = new List<string>();
            for (var index = 0; index < scenario.Turns.Count; index++)
            {
                var turn = scenario.Turns[index];
                var number = index + 1;
                string? tier = configuration.HostDriven ? configuration.Tier : null;
                if (configuration.Routed)
                {
                    var (routed, routing) = await RouteAsync(asked, turn.Prompt, Path.Combine(transcripts, $"turn{number}.router.jsonl"), cancellationToken).ConfigureAwait(false);
                    tier = routed;
                    AddHelperRequests(run, routing, number, "router");
                }

                asked.Add(turn.Prompt);
                (string Tree, Dictionary<string, string> Untracked)? before = configuration.Policies.Contains("review") ? Snapshot(project) : null;
                var reading = await RunTurnAsync(turn.Prompt, Arguments(configuration, sessionId, index == 0, project, tier is null ? null : tierModels[tier]), project,
                    Path.Combine(transcripts, $"turn{number}.jsonl"), cancellationToken).ConfigureAwait(false);
                if (InvalidReason(reading) is { } invalid)
                {
                    run.Invalid = $"turn {number}: {invalid}";
                    return run;
                }

                var duration = reading.DurationMs;
                var cost = reading.Cost;
                var toolCalls = reading.ToolCalls;
                lastModel = AddRequests(run, reading, number, lastModel, "work");

                // Escalation: the host continues a turn that shows trouble on the next tier, as long as there is one.
                var escalations = new List<string>();
                while (configuration.Policies.Contains("escalate") && tier is not null && Next(tier) is { } stronger && Trouble(reading) is { } trouble)
                {
                    escalations.Add($"{tier}→{stronger}: {trouble}");
                    tier = stronger;
                    reading = await RunTurnAsync($"Continue with the request above: {trouble} Check what was done, then finish the request.",
                        Arguments(configuration, sessionId, first: false, project, tierModels[tier]), project, Path.Combine(transcripts, $"turn{number}.escalation{escalations.Count}.jsonl"), cancellationToken).ConfigureAwait(false);
                    if (InvalidReason(reading) is { } failed)
                    {
                        run.Invalid = $"turn {number}, escalation: {failed}";
                        return run;
                    }

                    duration += reading.DurationMs;
                    cost = Add(cost, reading.Cost);
                    toolCalls += reading.ToolCalls;
                    lastModel = AddRequests(run, reading, number, lastModel, "escalation");
                }

                // Review: the complex model reads the change of the turn; what it finds is sent to the session once.
                string? review = null;
                if (before is not null && tier is not null && tier != "complex" && Diff(project, before.Value) is { Length: > 0 } diff)
                {
                    var (findings, reviewing) = await ReviewAsync(turn.Prompt, diff, Path.Combine(transcripts, $"turn{number}.review.jsonl"), cancellationToken).ConfigureAwait(false);
                    AddHelperRequests(run, reviewing, number, "review");
                    review = findings ?? string.Empty;
                    if (findings is not null)
                    {
                        reading = await RunTurnAsync($"A reviewer read your change and found these problems:\n\n{findings}\n\nFix those that are real, keep the tests passing, and answer as you would have.",
                            Arguments(configuration, sessionId, first: false, project, tierModels[tier]), project, Path.Combine(transcripts, $"turn{number}.fix.jsonl"), cancellationToken).ConfigureAwait(false);
                        if (InvalidReason(reading) is { } failed)
                        {
                            run.Invalid = $"turn {number}, fix: {failed}";
                            return run;
                        }

                        duration += reading.DurationMs;
                        cost = Add(cost, reading.Cost);
                        toolCalls += reading.ToolCalls;
                        lastModel = AddRequests(run, reading, number, lastModel, "fix");
                    }
                }

                var check = reading.Status == "completed" ? Check(turn, new EvalTurnOutcome(reading.FinalMessage, project, Path.Combine(scenariosFolder, scenario.Id), number)) : reading.Status;
                run.Turns.Add(new EvalTurnResult
                {
                    Turn = number, Status = reading.Status ?? "none", FailureKind = reading.FailureKind, FailureMessage = reading.FailureMessage, FinalMessage = reading.FinalMessage,
                    StartModel = reading.HeaderModel, Passed = check is null, CheckFailure = check, DurationMs = duration, ProviderCost = cost,
                    ToolCalls = toolCalls, Children = reading.Children.Count, Tier = configuration.HostDriven ? (escalations.Count > 0 ? escalations[0].Split('→')[0] : tier) : null,
                    Escalations = escalations, Review = review,
                });
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

    private IEnumerable<string> Arguments(EvalConfiguration configuration, string sessionId, bool first, string project, string? model)
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
            if (model is null) yield return "--tier=" + configuration.Tier;
        }
        else
        {
            // Without a model of the host: the model it has, the one of its tier or the one the agent chose.
            yield return "--resume=" + sessionId;
        }

        if (model is not null)
        {
            yield return "-m";
            yield return model;
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
    private static string? AddRequests(EvalScenarioRun run, TurnReading reading, int turn, string? lastModel, string role)
    {
        var added = new List<EvalRequest>();
        foreach (var operation in reading.Operations)
        {
            var root = operation.SessionId == reading.RootSessionId;
            var model = operation.Model ?? (root ? lastModel ?? reading.HeaderModel : null);
            var switched = root && lastModel is not null && model is not null && !string.Equals(model, lastModel, StringComparison.Ordinal);
            added.Add(new EvalRequest
            {
                Turn = turn, SessionId = operation.SessionId, Role = root ? role : "child", Provider = reading.Provider, Model = model,
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

    // The requests of a call the host made by itself (the router, the reviewer): another session, never a switch.
    private static void AddHelperRequests(EvalScenarioRun run, TurnReading reading, int turn, string role)
    {
        foreach (var operation in reading.Operations)
        {
            run.Requests.Add(new EvalRequest
            {
                Turn = turn, SessionId = operation.SessionId, Role = role, Provider = reading.Provider, Model = operation.Model ?? reading.HeaderModel,
                Input = operation.Input, CacheRead = operation.CacheRead, CacheWrite = operation.CacheWrite, Output = operation.Output,
            });
        }
    }

    private static string? Next(string tier) => Array.IndexOf(TierOrder, tier) is var index and >= 0 and < 2 ? TierOrder[index + 1] : null;

    private static decimal? Add(decimal? a, decimal? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    // A sign of trouble the host sees in a turn without knowing the task: no answer, a refusal to use the tools, or its last
    // test run failing. Null when the turn looks done.
    private static string? Trouble(TurnReading reading)
    {
        if (reading.Status != "completed") return "the turn ended without an answer.";
        if (string.IsNullOrWhiteSpace(reading.FinalMessage)) return "the turn ended without an answer.";
        if (reading.ToolCalls == 0 && Refusal().IsMatch(reading.FinalMessage)) return "the answer says the tools cannot be used, but they can: use them.";
        if (reading.LastTestExitCode is { } code and not 0) return $"the last test run failed (exit code {code}).";
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(cannot|can't|can not|unable to|do not have|don't have|no access)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Refusal();

    // The router: the quick model, without tools, reads the requests so far and picks the tier of the next one.
    private async Task<(string Tier, TurnReading Reading)> RouteAsync(IReadOnlyList<string> earlier, string request, string transcript, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder("""
            You route the requests of a user to a coding agent among three model tiers, by price: quick (cheap: lookups,
            answers, mechanical edits, renames, docs, simple tests, commits, small features whose behavior is specified),
            standard (ordinary changes with their tests), complex (expensive: design decisions, bugs whose cause is unknown,
            subtle algorithms or invariants, concurrency, changes across many areas). Choose the cheapest tier likely to do the
            request right the first time. Answer with one word: quick, standard or complex.

            """);
        if (earlier.Count > 0)
        {
            prompt.AppendLine("Earlier requests of the session, done:");
            foreach (var item in earlier) prompt.AppendLine("- " + Bound(item.ReplaceLineEndings(" "), 300));
        }

        prompt.AppendLine().AppendLine("The request to route:").AppendLine(request);
        var reading = await RunTurnAsync(prompt.ToString(), HelperArguments(tierModels["quick"]), Path.GetTempPath(), transcript, cancellationToken).ConfigureAwait(false);
        var answer = (reading.FinalMessage ?? string.Empty).ToLowerInvariant();
        var tier = TierOrder.LastOrDefault(candidate => answer.Contains(candidate, StringComparison.Ordinal)) ?? "standard";
        return (tier, reading);
    }

    // The reviewer: the complex model, without tools, reads the request and the diff of the turn.
    private async Task<(string? Findings, TurnReading Reading)> ReviewAsync(string request, string diff, string transcript, CancellationToken cancellationToken)
    {
        var prompt = $"""
            You review a change a coding agent made for a request. Report only what makes the change fail the request or
            break the code that uses it: a requirement or a case the request states that is not met, a bug on the inputs the
            request or the callers give, a cause worked around at one caller instead of fixed where it is. Do not report style,
            inputs the request does not name, or hardening beyond it. If nothing like that is wrong, answer exactly OK.
            Otherwise list the problems, most important first, each in one or two sentences with the file and what to do.

            ## Request
            {request}

            ## Diff
            ```diff
            {Bound(diff, 60_000)}
            ```
            """;
        var reading = await RunTurnAsync(prompt, HelperArguments(tierModels["complex"]), Path.GetTempPath(), transcript, cancellationToken).ConfigureAwait(false);
        var answer = (reading.FinalMessage ?? string.Empty).Trim();
        return (answer.Length == 0 || answer.Trim('.', '*', '`').Equals("OK", StringComparison.OrdinalIgnoreCase) ? null : answer, reading);
    }

    private IEnumerable<string> HelperArguments(string model)
    {
        foreach (var argument in new[] { "run", "--ephemeral", "--format", "jsonl", "--quiet", "--no-mcp", "--no-plugins", "--no-skills", "--disable-tools", "*", "-m", model, "--timeout", "300" })
        {
            yield return argument;
        }

        foreach (var setting in tierSettings)
        {
            yield return "-c";
            yield return setting;
        }

        yield return "-";
    }

    // The state of the project before a turn: its tracked files as a commit object, and its untracked files with their content.
    private static (string Tree, Dictionary<string, string> Untracked) Snapshot(string project)
    {
        var (_, stash) = EvalScenarios.Run(project, "git", ["stash", "create"]);
        var (_, head) = EvalScenarios.Run(project, "git", ["rev-parse", "HEAD"]);
        var tree = stash.Trim().Length > 0 ? stash.Trim() : head.Trim();
        return (tree, Untracked(project));
    }

    // What a turn changed in the code: the diff of the tracked files since the snapshot, and the untracked files it added or
    // changed. Empty when it changed only docs (Markdown) or nothing.
    private static string Diff(string project, (string Tree, Dictionary<string, string> Untracked) before)
    {
        var (_, tracked) = EvalScenarios.Run(project, "git", ["diff", before.Tree, "--", ".", ":(exclude)*.md"]);
        var text = new StringBuilder(tracked);
        foreach (var (path, content) in Untracked(project))
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || (before.Untracked.TryGetValue(path, out var old) && old == content)) continue;
            text.AppendLine(CultureInfo.InvariantCulture, $"--- /dev/null\n+++ b/{path}");
            foreach (var line in content.ReplaceLineEndings("\n").Split('\n')) text.Append('+').AppendLine(line);
        }

        return text.ToString().Trim();
    }

    private static Dictionary<string, string> Untracked(string project)
    {
        var (_, list) = EvalScenarios.Run(project, "git", ["ls-files", "--others", "--exclude-standard"]);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = Path.Combine(project, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && new FileInfo(full).Length < 200_000) files[path] = File.ReadAllText(full);
        }

        return files;
    }

    private static string Bound(string text, int length) => text.Length <= length ? text : text[..length] + "\n[cut]";

    /// <summary>What the JSONL of a turn says.</summary>
    private sealed partial class TurnReading
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
        /// <summary>The exit code of the last test command the session ran (npm test, node --test...); null when it ran none.</summary>
        public int? LastTestExitCode { get; private set; }
        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _testCalls = new(StringComparer.Ordinal);
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
                    case "session/update" when session == RootSessionId && parameters.TryGetProperty("update", out var update):
                        ReadUpdate(update);
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

        // The tool calls of the session, and the exit code of its test commands as their output gives it.
        private void ReadUpdate(JsonElement update)
        {
            var kind = String(update, "sessionUpdate");
            var id = String(update, "toolCallId");
            if (kind == "tool_call")
            {
                ToolCalls++;
                if (id is not null && String(update, "title") is { } title && TestCommand().IsMatch(title)) _testCalls.Add(id);
            }

            if (id is null || !_testCalls.Contains(id) || !update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var item in content.EnumerateArray())
            {
                var text = item.TryGetProperty("content", out var inner) ? String(inner, "text") : null;
                if (text is not null && ExitCodeLine().Match(text) is { Success: true } match) LastTestExitCode = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"\b(npm (run )?test|node --test|npx (jest|vitest|mocha)|pytest|dotnet test)\b")]
        private static partial System.Text.RegularExpressions.Regex TestCommand();

        [System.Text.RegularExpressions.GeneratedRegex(@"^exit_code: (-?\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)]
        private static partial System.Text.RegularExpressions.Regex ExitCodeLine();

        private static string? String(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long Number(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
}