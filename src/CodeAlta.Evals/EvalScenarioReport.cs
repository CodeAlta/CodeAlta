using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

/// <summary>The public price of a model, per million tokens, from models.dev as CodeAlta has it.</summary>
internal sealed record EvalPrice(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

/// <summary>
/// The prices of the models of a run, read once from <c>altacli alta model show</c>: every request of the grid is priced
/// from this snapshot, written in the report, so that the configurations compare on the same prices.
/// </summary>
internal sealed class EvalPrices(string altacli)
{
    private readonly Dictionary<string, EvalPrice?> _prices = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, EvalPrice?> Snapshot => _prices;

    public EvalPrice? Get(string? provider, string? model)
    {
        if (provider is null || model is null) return null;
        var key = $"{provider}:{model}";
        lock (_prices)
        {
            if (_prices.TryGetValue(key, out var known)) return known ?? SameModel(model);
            var price = Read(key);
            _prices[key] = price;
            // A child session on another provider is read with the provider of the run: its model is known by its id.
            return price ?? SameModel(model);
        }
    }

    private EvalPrice? SameModel(string model)
        => _prices.FirstOrDefault(pair => pair.Value is not null && pair.Key.EndsWith(":" + model, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The price of a request: what was not cached at the input price, the cache at its prices, the output.</summary>
    public static decimal? PriceOf(EvalRequest request, EvalPrice? price)
    {
        if (price is null) return null;
        var uncached = Math.Max(0, request.Input - request.CacheRead - request.CacheWrite);
        return (uncached * price.Input + request.CacheRead * (price.CacheRead ?? price.Input) + request.CacheWrite * (price.CacheWrite ?? price.Input)
            + request.Output * price.Output) / 1_000_000m;
    }

    private EvalPrice? Read(string modelRef)
    {
        var start = new ProcessStartInfo(altacli) { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "alta", "--isolated", "model", "show", "--model-ref", modelRef }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("capabilities", out var capabilities)) continue;
                var input = Decimal(capabilities, "inputCostPerMillionTokens");
                var outputPrice = Decimal(capabilities, "outputCostPerMillionTokens");
                if (input is null || outputPrice is null) return null;
                return new EvalPrice(input.Value, outputPrice.Value, Decimal(capabilities, "cacheReadCostPerMillionTokens"), Decimal(capabilities, "cacheWriteCostPerMillionTokens"));
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static decimal? Decimal(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
}

/// <summary>The Markdown report of the scenarios: per scenario and configuration, then per configuration, with every leg counted.</summary>
internal static class EvalScenarioReport
{
    /// <summary>The configuration the others are compared with.</summary>
    public const string Baseline = "default@complex";

    public static string Markdown(IReadOnlyList<EvalScenarioRun> runs, IReadOnlyList<string> tiers, EvalPrices prices, int invalid)
    {
        var valid = runs.Where(static run => run.Invalid is null).ToList();
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Adaptive mode evaluation, {DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine();
        builder.AppendLine("Tiers: " + string.Join(", ", tiers.Select(static tier => $"`{tier}`")) + ".");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runs: {valid.Count} valid, {invalid} invalid run again or left out. Every request is priced from the snapshot below, children and hand-off turns included.");
        builder.AppendLine();
        builder.AppendLine("| Model | Input | Cache read | Cache write | Output |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var (model, price) in prices.Snapshot.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(price is null
                ? $"| {model} | – | – | – | – |"
                : string.Create(CultureInfo.InvariantCulture, $"| {model} | ${price.Input} | {Dollars(price.CacheRead)} | {Dollars(price.CacheWrite)} | ${price.Output} |"));
        }

        builder.AppendLine();
        builder.AppendLine("## Per scenario and configuration");
        builder.AppendLine();
        builder.AppendLine("Averages per run. Success: runs whose turns all passed. Hand-off: the requests of a turn before it switched. Children: delegations and reviews.");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Configuration | Success | Turns passed | Cost | Provider cost | Requests | Cache read | Cache write | Switches | Hand-off cost | Children cost | Host legs cost | Escalations | Reviews flagged | Time |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in valid.GroupBy(static run => (run.Scenario, run.Configuration)).OrderBy(static group => group.Key.Scenario, StringComparer.Ordinal).ThenBy(static group => Order(group.Key.Configuration), StringComparer.Ordinal))
        {
            var list = group.ToList();
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key.Scenario} | {group.Key.Configuration} | {list.Count(static run => run.Succeeded)}/{list.Count} | {list.Average(static run => run.Turns.Count(static turn => turn.Passed)):0.0}/{list.Max(static run => run.Turns.Count)} "
                + $"| {Dollars(Average(list, Cost))} | {Dollars(Average(list, static run => Sum(run.Turns.Select(static turn => turn.ProviderCost))))} | {list.Average(static run => run.Requests.Count):0} "
                + $"| {Kilo(list.Average(static run => run.Requests.Sum(static request => request.CacheRead)))} | {Kilo(list.Average(static run => run.Requests.Sum(static request => request.CacheWrite)))} "
                + $"| {list.Average(static run => run.Switches):0.0} | {Dollars(Average(list, static run => Cost(run, static request => request.HandOff)))} | {Dollars(Average(list, static run => Cost(run, static request => request.Role == "child")))} "
                + $"| {Dollars(Average(list, static run => Cost(run, static request => HostLeg(request.Role))))} | {list.Average(static run => run.Turns.Sum(static turn => turn.Escalations.Count)):0.0} "
                + $"| {list.Average(static run => run.Turns.Count(static turn => !string.IsNullOrEmpty(turn.Review))):0.0} | {list.Average(static run => run.Turns.Sum(static turn => turn.DurationMs)) / 1000:0} s |");
        }

        builder.AppendLine();
        builder.AppendLine("## Per configuration");
        builder.AppendLine();
        var baseline = valid.Where(static run => run.Configuration == Baseline).ToList();
        var baselineCost = ScenarioCost(baseline);
        builder.AppendLine(CultureInfo.InvariantCulture, $"Cost: the sum over the scenarios of the average cost of a run. The bar to ship: pass every scenario that `{Baseline}` passes at least once, for at most 60% of its cost.");
        builder.AppendLine();
        builder.AppendLine("| Configuration | Success | Cost | Of the baseline | Switches per run | Hand-off cost | Children cost | Scenarios short of the baseline | Bar |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in valid.GroupBy(static run => run.Configuration).OrderBy(static group => Order(group.Key), StringComparer.Ordinal))
        {
            var list = group.ToList();
            var cost = ScenarioCost(list);
            var share = baselineCost is > 0 && cost is not null ? cost / baselineCost : null;
            var shortOf = baseline.Where(static run => run.Succeeded).Select(static run => run.Scenario).Distinct()
                .Where(scenario => !list.Any(run => run.Scenario == scenario && run.Succeeded)).ToArray();
            var bar = group.Key == Baseline || baseline.Count == 0 ? "–" : shortOf.Length == 0 && share <= 0.6m ? "**meets**" : "misses";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {list.Count(static run => run.Succeeded)}/{list.Count} | {Dollars(cost)} | {(share is null ? "–" : share.Value.ToString("P0", CultureInfo.InvariantCulture))} "
                + $"| {list.Average(static run => run.Switches):0.0} | {Dollars(list.Sum(static run => Cost(run, static request => request.HandOff)) / list.Count)} "
                + $"| {Dollars(list.Sum(static run => Cost(run, static request => request.Role == "child")) / list.Count)} | {(shortOf.Length == 0 ? "none" : string.Join(", ", shortOf))} | {bar} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Models used");
        builder.AppendLine();
        builder.AppendLine("| Configuration | Requests by model | Cost by model |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var group in valid.GroupBy(static run => run.Configuration).OrderBy(static group => Order(group.Key), StringComparer.Ordinal))
        {
            var requests = group.SelectMany(static run => run.Requests).ToList();
            var byModel = requests.GroupBy(static request => request.Model ?? "?").OrderByDescending(static models => models.Count()).ToList();
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {string.Join(", ", byModel.Select(models => $"{models.Key} {(double)models.Count() / requests.Count:P0}"))} | {string.Join(", ", byModel.Select(static models => $"{models.Key} {Dollars(Sum(models.Select(static request => request.Price)))}"))} |");
        }

        var failed = valid.SelectMany(static run => run.Turns.Where(static turn => !turn.Passed).Select(turn => (run, turn))).ToList();
        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failed turns");
            builder.AppendLine();
            builder.AppendLine("| Scenario | Configuration | Run | Turn | Status | Why |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var (run, turn) in failed)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"| {run.Scenario} | {run.Configuration} | {run.Repeat} | {turn.Turn} | {turn.Status} | {Cell(turn.CheckFailure ?? turn.FailureMessage)} |");
            }
        }

        var invalidRuns = runs.Where(static run => run.Invalid is not null).ToList();
        if (invalidRuns.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Invalid runs");
            builder.AppendLine();
            foreach (var run in invalidRuns) builder.AppendLine(CultureInfo.InvariantCulture, $"- {run.Scenario} · {run.Configuration} · {run.Repeat} (attempt {run.Attempt}): {Cell(run.Invalid)}");
        }

        return builder.ToString();
    }

    // The legs the host adds around the turns of the agent: its router, its reviewer, and the turns it sends after them.
    private static bool HostLeg(string role) => role is "router" or "review" or "acceptance" or "escalation" or "fix";

    /// <summary>The priced cost of a run: every request of every leg.</summary>
    public static decimal? Cost(EvalScenarioRun run) => Sum(run.Requests.Select(static request => request.Price));

    private static decimal? Cost(EvalScenarioRun run, Func<EvalRequest, bool> leg) => Sum(run.Requests.Where(leg).Select(static request => request.Price)) ?? 0;

    // The sum over the scenarios of the average cost of a run of the scenario.
    private static decimal? ScenarioCost(IReadOnlyList<EvalScenarioRun> runs)
        => runs.Count == 0 ? null : Sum(runs.GroupBy(static run => run.Scenario).Select(static group => Average(group.ToList(), Cost)));

    private static decimal? Average(IReadOnlyList<EvalScenarioRun> runs, Func<EvalScenarioRun, decimal?> value)
    {
        var known = runs.Select(value).Where(static item => item is not null).Select(static item => item!.Value).ToArray();
        return known.Length == 0 ? null : known.Average();
    }

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        decimal? sum = null;
        foreach (var value in values)
        {
            if (value is { } known) sum = (sum ?? 0) + known;
        }

        return sum;
    }

    // The baseline first, then Default on standard, then the variants by name.
    private static string Order(string configuration) => configuration switch
    {
        Baseline => "0",
        "default@standard" => "1",
        _ => "2" + configuration,
    };

    private static string Dollars(decimal? value) => value is null ? "–" : value.Value.ToString(value.Value < 0.1m ? "$0.0000" : "$0.000", CultureInfo.InvariantCulture);

    private static string Kilo(double value) => string.Create(CultureInfo.InvariantCulture, $"{value / 1000:0}k");

    private static string Cell(string? text)
    {
        if (text is null) return "–";
        var line = text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

/// <summary>A model that reads the runs of the Adaptive configurations and names each wrong choice of a model.</summary>
internal static class EvalScenarioJudge
{
    public static async Task<string> RunAsync(string altacli, string model, IReadOnlyList<EvalScenarioRun> runs, IReadOnlyList<string> tiers, string output, TimeSpan timeout)
    {
        // The runs that choose models: the Adaptive prompts, and the policies of the host.
        var chosen = runs.Where(static run => run.Invalid is null && (run.Configuration.StartsWith("adaptive", StringComparison.Ordinal)
            || run.Configuration.Contains('+', StringComparison.Ordinal) || run.Configuration.EndsWith("@router", StringComparison.Ordinal))).ToList();
        if (chosen.Count == 0) return "\n## Judge\n\nNo Adaptive run to read.\n";
        var prompt = new StringBuilder($"""
            You review runs of an AI coding agent in an "Adaptive" mode: before each request it should choose the cheapest model
            tier that does the request well, switch its own session to another tier (a hand-off, which re-reads the whole
            conversation at the full price of the new model), or give a part to a child session on another tier. The tiers were:
            {string.Join(", ", tiers)}. Each run below is a scenario of several user requests in one session; for each turn you get
            the request, the models of its requests in order (`model ×count`, `→` a switch, `child` a child session), whether the
            turn passed its checks (tests the agent did not see included) and what the turn cost.

            Name each wrong choice: a tier too strong for the request, too weak (a failure, or a long struggle), a switch that did
            not pay back, a missed hand-off, a review that missed a defect. Then say which rules of the mode would avoid them. The
            rules must be general (about kinds of requests and signals of difficulty), never about these scenarios. Be concrete
            and brief; end with the rules to change, most useful first.

            """);
        foreach (var run in chosen)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## {run.Scenario} · {run.Configuration} · run {run.Repeat} · {(run.Succeeded ? "succeeded" : "failed")} · {Money(EvalScenarioReport.Cost(run))}");
            var scenario = EvalScenarios.All.First(item => item.Id == run.Scenario);
            foreach (var turn in run.Turns)
            {
                var requests = run.Requests.Where(request => request.Turn == turn.Turn).ToList();
                prompt.AppendLine(CultureInfo.InvariantCulture, $"### Turn {turn.Turn}: {(turn.Passed ? "passed" : "FAILED: " + (turn.CheckFailure ?? turn.Status))} · {Money(requests.Sum(static request => request.Price ?? 0))}");
                prompt.AppendLine("Request: " + scenario.Turns[turn.Turn - 1].Prompt.ReplaceLineEndings(" "));
                prompt.AppendLine("Models: " + Sequence(requests));
                prompt.AppendLine("Answer: " + Bound(turn.FinalMessage, 400));
            }

            prompt.AppendLine();
        }

        var file = Path.Combine(output, "judge-prompt.md");
        await File.WriteAllTextAsync(file, prompt.ToString()).ConfigureAwait(false);
        var start = new ProcessStartInfo(altacli) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "run", "--ephemeral", "--quiet", "--no-mcp", "-m", model, "--timeout", ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture), "--disable-tools", "*", "-" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(prompt.ToString()).ConfigureAwait(false);
        process.StandardInput.Close();
        var answer = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return $"\n## Judge ({model})\n\n{answer.Trim()}\n";
    }

    // The models of the requests in order, the runs of one model counted: gpt-6-luna ×9 → gpt-6.1-sol ×12, child gpt-6-luna ×4.
    internal static string Sequence(IReadOnlyList<EvalRequest> requests)
    {
        var parts = new List<string>();
        string? current = null;
        var count = 0;
        foreach (var request in requests.Where(static request => request.Role is "work" or "escalation" or "fix"))
        {
            if (request.Model == current)
            {
                count++;
                continue;
            }

            if (current is not null) parts.Add($"{current} ×{count}");
            current = request.Model;
            count = 1;
        }

        if (current is not null) parts.Add($"{current} ×{count}");
        var text = string.Join(" → ", parts);
        foreach (var child in requests.Where(static request => request.Role is "child" or "router" or "review" or "acceptance").GroupBy(static request => (request.SessionId, request.Model, request.Role)))
        {
            text += $", {child.Key.Role} {child.Key.Model} ×{child.Count()}";
        }

        return text.Length == 0 ? "(none)" : text;
    }

    private static string Money(decimal? value) => value is null ? "cost unknown" : value.Value.ToString("$0.0000", CultureInfo.InvariantCulture);

    private static string Bound(string? text, int length) => text is null ? "(none)" : text.Length <= length ? text.ReplaceLineEndings(" ") : text[..length].ReplaceLineEndings(" ") + "…";
}