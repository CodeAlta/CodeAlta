using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

/// <summary>The Markdown report: one line per task, model and mode, then the comparison of the modes by model.</summary>
internal static class EvalReport
{
    public static string Markdown(IReadOnlyList<EvalRun> runs, IReadOnlyList<EvalTask> tasks)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Code Mode evaluation, {DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine();
        builder.AppendLine("| Task | Model | Mode | Success | Tokens in/out | Cost | Time | Calls | Scripts | Script calls | No tool | Script failures |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in runs.GroupBy(static run => (run.Task, run.Model, run.Mode)))
        {
            var list = group.ToList();
            var failures = list.SelectMany(static run => run.ScriptFailures).GroupBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key} {pair.Sum(static item => item.Value)}");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key.Task} | {group.Key.Model} | {group.Key.Mode} | {list.Count(static run => run.Succeeded)}/{list.Count} | {Average(list, static run => run.InputTokens):0}/{Average(list, static run => run.OutputTokens):0} | {Cost(list)} | {list.Average(static run => run.DurationMs) / 1000:0.0} s | {list.Average(static run => run.ToolCalls.Values.Sum()):0.0} | {list.Average(static run => run.Scripts):0.0} | {list.Average(static run => run.ScriptCalls):0.0} | {list.Count(static run => run.NoTool)} | {string.Join(", ", failures)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Modes compared, by model");
        builder.AppendLine();
        builder.AppendLine("Tasks where Code Mode should help; the sequential control apart.");
        builder.AppendLine();
        builder.AppendLine("| Model | Mode | Success | Tokens in | Tokens out | Time | Control success |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        var helps = tasks.Where(static task => task.CodeModeHelps).Select(static task => task.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in runs.GroupBy(static run => (run.Model, run.Mode)).OrderBy(static group => group.Key.Model, StringComparer.Ordinal))
        {
            var main = group.Where(run => helps.Contains(run.Task)).ToList();
            var control = group.Where(run => !helps.Contains(run.Task)).ToList();
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key.Model} | {group.Key.Mode} | {Rate(main)} | {Average(main, static run => run.InputTokens):0} | {Average(main, static run => run.OutputTokens):0} | {(main.Count == 0 ? 0 : main.Average(static run => run.DurationMs) / 1000):0.0} s | {Rate(control)} |");
        }

        return builder.ToString();
    }

    private static string Rate(IReadOnlyList<EvalRun> runs) => runs.Count == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{runs.Count(static run => run.Succeeded)}/{runs.Count}");

    private static double Average(IReadOnlyList<EvalRun> runs, Func<EvalRun, long?> value)
    {
        var known = runs.Select(value).Where(static item => item is not null).Select(static item => (double)item!.Value).ToArray();
        return known.Length == 0 ? 0 : known.Average();
    }

    private static string Cost(IReadOnlyList<EvalRun> runs)
    {
        var known = runs.Where(static run => run.Cost is not null).ToArray();
        return known.Length == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{known.Average(static run => run.Cost!.Value):0.0000} {known[0].CostCurrency}");
    }
}

/// <summary>A model that reads the failed and the costly runs and says what made the scripts hard to write.</summary>
internal static class EvalJudge
{
    public static async Task<string> RunAsync(string altacli, string model, IReadOnlyList<EvalRun> runs, string output, TimeSpan timeout)
    {
        var costly = runs.Where(static run => run.Mode == "on").OrderByDescending(static run => (run.InputTokens ?? 0) + (run.OutputTokens ?? 0)).Take(3);
        var chosen = runs.Where(static run => !run.Succeeded || run.ScriptFailures.Count > 0).Concat(costly).Distinct().Take(12).ToList();
        if (chosen.Count == 0) return "\n## Judge\n\nNo failed or costly run to read.\n";

        var prompt = new StringBuilder("""
            You review runs of AI coding agents that had a tool `exec`, which runs a JavaScript script that calls their other tools
            (Code Mode). For each run below you get the task, how it ended, and the scripts it sent with what each returned.
            Name the friction: what in the tool description, the function declarations or the error texts led the model astray,
            and the smallest change of those texts that would have avoided it. Group the same friction across runs. Be concrete
            and brief; end with a list of the changes, most useful first.

            """);
        foreach (var run in chosen)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## Run {run.Task} · {run.Model} · {run.Mode} · {(run.Succeeded ? "succeeded" : "failed")} · {run.Status}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"Task: {EvalTasks.All.First(task => task.Id == run.Task).Prompt}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"Final answer: {Bound(run.FinalMessage, 600)}");
            if (run.TranscriptPath is { } path && File.Exists(path)) prompt.AppendLine(Scripts(path));
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

    // The scripts a run sent, and the start of what each returned.
    private static string Scripts(string transcript)
    {
        var builder = new StringBuilder();
        var execs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(transcript))
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("update", out var update)) continue;
            var id = update.TryGetProperty("toolCallId", out var callId) ? callId.GetString() : null;
            if (id is null) continue;
            if (update.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("codealta", out var codealta) && codealta.TryGetProperty("tool", out var tool)
                && tool.GetString() == "exec" && execs.Add(id) && update.TryGetProperty("rawInput", out var input) && input.TryGetProperty("code", out var code))
            {
                builder.AppendLine("Script:").AppendLine("```js").AppendLine(Bound(code.GetString(), 2000)).AppendLine("```");
            }
            else if (execs.Contains(id) && update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var inner) && inner.TryGetProperty("text", out var text))
                    {
                        builder.AppendLine("Returned:").AppendLine("```").AppendLine(Bound(text.GetString(), 1200)).AppendLine("```");
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static string Bound(string? text, int length) => text is null ? "(none)" : text.Length <= length ? text : text[..length] + "…";
}
