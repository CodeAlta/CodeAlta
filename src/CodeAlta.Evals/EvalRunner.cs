using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

/// <summary>One run of a task by a model in a mode, as its JSONL stream and its project tell it.</summary>
internal sealed record EvalRun
{
    public required string Task { get; init; }
    public required string Family { get; init; }
    public required string Model { get; init; }
    public required string Mode { get; init; }
    public required int Repeat { get; init; }
    public bool Succeeded { get; init; }
    public string Status { get; init; } = "unknown";
    public string? FailureKind { get; init; }
    /// <summary>Why the run failed, as the run's result says it, for the judge.</summary>
    public string? FailureMessage { get; init; }
    public string? FinalMessage { get; init; }
    /// <summary>The calls of the model, by tool, and the calls scripts made.</summary>
    public Dictionary<string, int> ToolCalls { get; init; } = new(StringComparer.Ordinal);
    public int ScriptCalls { get; init; }
    public int Scripts { get; init; }
    /// <summary>The sessions the run started (with <c>agent()</c>, <c>alta session create</c> or a library script), whose events share its stream.</summary>
    public int ChildSessions { get; init; }
    /// <summary>The scripts that failed, by kind: syntax, typescript, node-api, unknown-function, wrong-arguments, unawaited, timeout, stalled, other.</summary>
    public Dictionary<string, int> ScriptFailures { get; init; } = new(StringComparer.Ordinal);
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? Cost { get; init; }
    public string? CostCurrency { get; init; }
    public long DurationMs { get; init; }
    /// <summary>The model answered without calling a tool: it may have answered from memory.</summary>
    public bool NoTool => ToolCalls.Count == 0;
    /// <summary>Where the JSONL of the run is kept, for the judge and for reading by hand.</summary>
    public string? TranscriptPath { get; init; }
}

/// <summary>Runs one task with <c>altacli run --ephemeral --format jsonl</c>, in a copy of its project, and reads what it did.</summary>
internal sealed class EvalRunner(string altacli, EvalContext context, TimeSpan timeout)
{
    public async Task<EvalRun> RunAsync(EvalTask task, string model, string mode, int repeat, string outputFolder, CancellationToken cancellationToken)
    {
        var project = Path.Combine(Path.GetTempPath(), "codealta-evals", $"{task.Id}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(project);
        EvalTasks.Write(project, task.Files(context));
        var transcript = Path.Combine(outputFolder, "transcripts", $"{task.Id}.{Safe(model)}.{mode}.{repeat}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        try
        {
            var start = new ProcessStartInfo(altacli)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = project,
            };
            foreach (var argument in Arguments(model, mode, project)) start.ArgumentList.Add(argument);
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{altacli} did not start.");
            await process.StandardInput.WriteAsync(task.Prompt).ConfigureAwait(false);
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var reading = new JsonlReading();
            await using (var writer = new StreamWriter(transcript, false, new UTF8Encoding(false)))
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    reading.Read(line);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await errors.ConfigureAwait(false);
            if (reading.Status is null && stderr.Length > 0) await File.WriteAllTextAsync(Path.ChangeExtension(transcript, ".stderr.txt"), stderr, cancellationToken).ConfigureAwait(false);
            var succeeded = reading.Status == "completed" && Check(task, new EvalOutcome(reading.FinalMessage, project));
            return new EvalRun
            {
                Task = task.Id, Family = task.Family, Model = model, Mode = mode, Repeat = repeat,
                Succeeded = succeeded, Status = reading.Status ?? $"exit {process.ExitCode}", FailureKind = reading.FailureKind, FailureMessage = reading.FailureMessage,
                FinalMessage = reading.FinalMessage, ToolCalls = reading.ToolCalls, ScriptCalls = reading.ScriptCalls, Scripts = reading.Scripts, ChildSessions = reading.ChildSessions,
                ScriptFailures = reading.ScriptFailures, InputTokens = reading.InputTokens, OutputTokens = reading.OutputTokens,
                Cost = reading.Cost, CostCurrency = reading.CostCurrency, DurationMs = reading.DurationMs ?? clock.ElapsedMilliseconds,
                TranscriptPath = transcript,
            };
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

    private IEnumerable<string> Arguments(string model, string mode, string project)
    {
        yield return "run";
        yield return "--ephemeral";
        yield return "--format";
        yield return "jsonl";
        yield return "--quiet";
        yield return "-C";
        yield return project;
        yield return "-m";
        yield return model;
        yield return "--timeout";
        yield return ((int)timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--code-mode=" + mode;
        yield return "-";
    }

    private static bool Check(EvalTask task, EvalOutcome outcome)
    {
        try
        {
            return task.Check(outcome);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Safe(string value) => string.Concat(value.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));

    // What the JSONL of a run says: its tool calls, the result of each script, the final report.
    private sealed class JsonlReading
    {
        private readonly HashSet<string> _execCalls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _judged = new(StringComparer.Ordinal);
        private readonly HashSet<string> _children = new(StringComparer.Ordinal);
        // The session of the run: the first one of the stream. The events of the sessions it starts come in the same stream.
        private string? _root;

        public Dictionary<string, int> ToolCalls { get; } = new(StringComparer.Ordinal);
        public int ChildSessions => _children.Count;
        public Dictionary<string, int> ScriptFailures { get; } = new(StringComparer.Ordinal);
        public int ScriptCalls { get; private set; }
        public int Scripts => _execCalls.Count;
        public string? Status { get; private set; }
        public string? FailureKind { get; private set; }
        public string? FailureMessage { get; private set; }
        public string? FinalMessage { get; private set; }
        public long? InputTokens { get; private set; }
        public long? OutputTokens { get; private set; }
        public decimal? Cost { get; private set; }
        public string? CostCurrency { get; private set; }
        public long? DurationMs { get; private set; }

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
                switch (method.GetString())
                {
                    case "session/update" when parameters.TryGetProperty("update", out var update):
                        var session = String(parameters, "sessionId");
                        _root ??= session;
                        if (session is null || session == _root) ReadUpdate(update);
                        else _children.Add(session);
                        break;
                    case "_codealta/result":
                        ReadResult(parameters);
                        break;
                }
            }
        }

        private void ReadUpdate(JsonElement update)
        {
            var kind = String(update, "sessionUpdate");
            var id = String(update, "toolCallId");
            if (id is null) return;
            if (kind == "tool_call" && _seen.Add(id))
            {
                var name = update.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("codealta", out var codealta) ? String(codealta, "tool") : null;
                name ??= String(update, "title") ?? "tool";
                if (id.Contains('/', StringComparison.Ordinal))
                {
                    ScriptCalls++;
                }
                else
                {
                    ToolCalls[name] = ToolCalls.GetValueOrDefault(name) + 1;
                    if (name == "exec") _execCalls.Add(id);
                }
            }

            // The result of a script is the text of its output: its first lines say how it ended.
            if (_execCalls.Contains(id) && !_judged.Contains(id) && update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var text = item.TryGetProperty("content", out var inner) ? String(inner, "text") : null;
                    if (text is null || !text.StartsWith("Script ", StringComparison.Ordinal)) continue;
                    _judged.Add(id);
                    if (FailureOf(text) is { } failure) ScriptFailures[failure] = ScriptFailures.GetValueOrDefault(failure) + 1;
                }
            }
        }

        private void ReadResult(JsonElement result)
        {
            Status = String(result, "status");
            FinalMessage = String(result, "finalMessage");
            if (result.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
            {
                FailureKind = String(failure, "kind");
                FailureMessage = String(failure, "message");
            }
            if (result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                InputTokens = Number(usage, "inputTokens");
                OutputTokens = Number(usage, "outputTokens");
            }

            if (result.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("amount", out var amount) && amount.TryGetDecimal(out var value))
            {
                Cost = value;
                CostCurrency = String(cost, "currency");
            }

            DurationMs = Number(result, "durationMs");
        }

        // The kind of failure of a script, from the text the model read.
        internal static string? FailureOf(string text)
        {
            if (text.StartsWith("Script completed", StringComparison.Ordinal) || text.StartsWith("Script running", StringComparison.Ordinal)) return null;
            if (text.StartsWith("Script stopped", StringComparison.Ordinal)) return "stopped";
            var message = text.Split('\n').Skip(1).FirstOrDefault() ?? string.Empty;
            if (message.Contains("TypeScript", StringComparison.Ordinal)) return "typescript";
            if (message.StartsWith("SyntaxError", StringComparison.Ordinal)) return message.Contains("\\n", StringComparison.Ordinal) ? "literal-newline" : "syntax";
            if (message.Contains("not Node.js", StringComparison.Ordinal) || message.Contains("require is not defined", StringComparison.Ordinal)) return "node-api";
            if (message.Contains("is not a function", StringComparison.Ordinal) || message.Contains("is not a tool", StringComparison.Ordinal)
                || message.Contains("is not defined", StringComparison.Ordinal)) return "unknown-function";
            if (message.Contains("Did you mean", StringComparison.Ordinal) || message.Contains("missing `", StringComparison.Ordinal)
                || message.Contains("args must be", StringComparison.Ordinal)) return "wrong-arguments";
            if (text.Contains("Not awaited", StringComparison.Ordinal)) return "unawaited";
            if (message.Contains("stalled", StringComparison.OrdinalIgnoreCase)) return "stalled";
            if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.StartsWith("TimeoutError", StringComparison.Ordinal)) return "timeout";
            return "other";
        }

        private static string? String(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long? Number(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
    }
}
