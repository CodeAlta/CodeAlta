using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>What a run keeps for a resume when it starts.</summary>
/// <param name="Source">The source of the script, as <c>exec</c> was given it or as the run it resumes kept it.</param>
public sealed record CodeModeRunStart(string Source)
{
    /// <summary>Gets the arguments of the script as a JSON object, or null.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>Gets the run this one resumes or reruns, or null.</summary>
    public string? ResumesRunId { get; init; }

    /// <summary>
    /// Gets whether the host started the run to run a script of the library for the user (<see cref="CodeModeScriptRunPrompt"/>):
    /// its start says so (<see cref="AgentWorkflowEvent.ForUser"/>), and its card shows its value as Markdown.
    /// </summary>
    public bool ForUser { get; init; }

    /// <summary>
    /// Gets whether a call of a function may run again with the same effect, by its path (<c>tools.read_file</c>). The calls
    /// of the others are recorded as they start and end, so that a resume knows those that were in flight. Null records every call.
    /// </summary>
    public Func<string, bool>? IsIdempotent { get; init; }
}

/// <summary>A call that was in flight when a run stopped, of a function that is not idempotent.</summary>
/// <param name="Path">The function, such as <c>tools.shell_command</c>.</param>
/// <param name="StepKey">The key of its step, or null for a call outside any step.</param>
/// <param name="Arguments">Its arguments, shortened.</param>
public sealed record CodeModeStoredCall(string Path, string? StepKey, string Arguments);

/// <summary>A run as the store kept it: what a resume needs.</summary>
/// <param name="Id">The id of the run.</param>
/// <param name="SessionId">The session that ran it.</param>
public sealed record CodeModeStoredRun(string Id, string SessionId)
{
    /// <summary>Gets its title, or null.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the <c>exec</c> call that ran it, or null.</summary>
    public string? CallId { get; init; }

    /// <summary>Gets the hash of the body of its source (<see cref="CodeModeSource.BodyHashOf"/>).</summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>Gets its source.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets its arguments as a JSON object, or null.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>Gets the run it resumed, or null.</summary>
    public string? ResumesRunId { get; init; }

    /// <summary>Gets when it started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets when it ended, or null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets how it ended: <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>interrupted</c>; <c>running</c> without an end.</summary>
    public string Status { get; init; } = "running";

    /// <summary>Gets the steps that ended, in the order they started.</summary>
    public IReadOnlyList<CodeModeReplayStep> Steps { get; init; } = [];

    /// <summary>Gets the calls that started and did not end, of functions that are not idempotent.</summary>
    public IReadOnlyList<CodeModeStoredCall> InFlightCalls { get; init; } = [];

    /// <summary>Gets the wake times of the sleeps the run started, by their key (<see cref="CodeModeSleep.KeyOf"/>).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Sleeps { get; init; } = new Dictionary<string, DateTimeOffset>();

    /// <summary>Gets when the run wakes, when it slept as it stopped: the latest wake time of its sleeps that did not end; null otherwise.</summary>
    public DateTimeOffset? SleepingUntil { get; init; }

    /// <summary>Gets when CodeAlta resumed the run by itself, at the end of the sleep it stopped in; null when it did not.</summary>
    public DateTimeOffset? AutoResumedAt { get; init; }
}

/// <summary>
/// The durable record of the runs of scripts, for a resume after a restart and the reruns over recorded results: under
/// <c>codemode/runs/&lt;run-id&gt;/</c> of the state root, <c>run.json</c>, <c>source.js</c>, <c>steps.jsonl</c> (one line per
/// step that ended, with its result), <c>calls.jsonl</c> (the start and the end of each call that is not idempotent) and
/// <c>result.txt</c> (what its session was given when it ended). It is also the index of the runs: listing them reads
/// only their <c>run.json</c>, never a journal.
/// A run writes as it goes, in order, off the thread of its script. The store keeps the 100 most recent runs that ended,
/// for 14 days at most, and a run that waits to resume by itself after a sleep until it did. A host whose state is thrown
/// away (<c>altacli</c>) keeps none.
/// </summary>
public sealed class CodeModeRunStore
{
    private const string RunFile = "run.json";
    private const string SourceFile = "source.js";
    private const string StepsFile = "steps.jsonl";
    private const string CallsFile = "calls.jsonl";
    private const string SleepsFile = "sleeps.jsonl";
    private const string ResultFile = "result.txt";
    private const int MaxArgumentCharacters = 160;

    private readonly int _maxEndedRuns;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _time;

    /// <summary>Creates the store of a folder.</summary>
    /// <param name="root">The folder of the runs.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public CodeModeRunStore(string root)
        : this(root, 100, TimeSpan.FromDays(14), TimeProvider.System)
    {
    }

    /// <summary>Creates the store of a folder with its own retention.</summary>
    /// <param name="root">The folder of the runs.</param>
    /// <param name="maxEndedRuns">The most runs that ended it keeps.</param>
    /// <param name="maxAge">How long it keeps a run that ended.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEndedRuns"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    public CodeModeRunStore(string root, int maxEndedRuns, TimeSpan maxAge, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEndedRuns);
        ArgumentNullException.ThrowIfNull(time);
        Root = Path.GetFullPath(root);
        _maxEndedRuns = maxEndedRuns;
        _maxAge = maxAge;
        _time = time;
    }

    /// <summary>Gets the folder of the runs.</summary>
    public string Root { get; }

    /// <summary>Gives the folder of the runs of an instance, under its state root.</summary>
    /// <param name="catalog">The roots of the instance.</param>
    /// <returns>The folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public static string RootOf(CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Path.Combine(catalog.StateRoot, "codemode", "runs");
    }

    /// <summary>Gets whether a text can be the id of a run: letters, digits, <c>-</c> and <c>_</c>, at most 64.</summary>
    /// <param name="value">The text.</param>
    /// <returns>True when it can.</returns>
    public static bool IsRunId(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Reads a run.</summary>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The run, or null when the store has none of that id or cannot read it.</returns>
    public CodeModeStoredRun? TryLoad(string runId)
    {
        if (!IsRunId(runId))
        {
            return null;
        }

        var folder = Path.Combine(Root, runId);
        try
        {
            if (ReadInfo(folder) is not { } info)
            {
                return null;
            }

            var sourcePath = Path.Combine(folder, SourceFile);
            return info with
            {
                Source = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : string.Empty,
                Steps = ReadSteps(Path.Combine(folder, StepsFile)),
                InFlightCalls = ReadInFlightCalls(Path.Combine(folder, CallsFile)),
                Sleeps = ReadSleeps(Path.Combine(folder, SleepsFile), out var sleepingUntil),
                SleepingUntil = sleepingUntil,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lists the runs the store keeps, the most recent first, from their <c>run.json</c> alone.</summary>
    /// <returns>The runs, without their source and their steps.</returns>
    public IReadOnlyList<CodeModeStoredRun> List()
    {
        var runs = new List<CodeModeStoredRun>();
        foreach (var folder in Folders())
        {
            try
            {
                if (ReadInfo(folder) is { } info)
                {
                    runs.Add(info);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. runs.OrderByDescending(static run => run.StartedAt)];
    }

    /// <summary>Reads what the session of a run was given when the run ended.</summary>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The text, or null for a run that has not ended, is unknown, or ended before the store kept results.</returns>
    public string? TryReadResult(string runId)
    {
        if (!IsRunId(runId))
        {
            return null;
        }

        var path = Path.Combine(Root, runId, ResultFile);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks the runs that have no end as interrupted: they ran when CodeAlta ended. Called at startup, before a script runs.
    /// </summary>
    /// <param name="running">Says whether a run runs in this process, which is left as it is.</param>
    /// <returns>The runs it marked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="running"/> is null.</exception>
    public IReadOnlyList<CodeModeStoredRun> MarkInterrupted(Func<string, bool> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        var marked = new List<CodeModeStoredRun>();
        foreach (var folder in Folders())
        {
            try
            {
                if (ReadInfo(folder) is not { EndedAt: null } info || running(info.Id))
                {
                    continue;
                }

                // It ended when it last wrote.
                var endedAt = new[] { RunFile, StepsFile, CallsFile, SleepsFile }.Select(name => Path.Combine(folder, name)).Where(File.Exists)
                    .Select(static path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)).DefaultIfEmpty(_time.GetUtcNow()).Max();
                var ended = info with { EndedAt = endedAt, Status = "interrupted" };
                WriteInfo(folder, ended);
                marked.Add(ended);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return marked;
    }

    /// <summary>
    /// Removes the runs that ended beyond the retention: past the most recent ones it keeps, or too old. A run that waits to
    /// resume by itself after a sleep (<see cref="CodeModeResume.AutoResumeTime"/>) is kept until it resumed or no longer qualifies.
    /// </summary>
    /// <returns>How many runs it removed.</returns>
    public int Prune()
    {
        // A run that waits to resume by itself after its sleep is kept, whatever its age and rank, until it resumed or no
        // longer qualifies; it does not take the place of another run meanwhile.
        var waking = ListSleepingInterrupted().Where(run => CodeModeResume.AutoResumeTime(this, run, out _) is not null)
            .Select(static run => run.Id).ToHashSet(StringComparer.Ordinal);
        var ended = new List<(string Folder, DateTimeOffset EndedAt)>();
        foreach (var folder in Folders())
        {
            try
            {
                if (ReadInfo(folder) is { EndedAt: { } endedAt } info && !waking.Contains(info.Id))
                {
                    ended.Add((folder, endedAt));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var oldest = _time.GetUtcNow() - _maxAge;
        var removed = 0;
        foreach (var (folder, endedAt) in ended.OrderByDescending(static run => run.EndedAt).Select((run, rank) => (run, rank))
            .Where(item => item.rank >= _maxEndedRuns || item.run.EndedAt < oldest).Select(static item => item.run))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    // The writer of a run that starts: run.json and source.js now, the rest as the run goes.
    internal Writer Begin(string runId, string sessionId, string? title, string? callId, CodeModeRunStart start)
    {
        var folder = Path.Combine(Root, runId);
        var info = new CodeModeStoredRun(runId, sessionId)
        {
            Title = title,
            CallId = callId,
            SourceHash = CodeModeSource.BodyHashOf(start.Source),
            ArgumentsJson = start.ArgumentsJson,
            ResumesRunId = start.ResumesRunId,
            StartedAt = _time.GetUtcNow(),
        };
        var writer = new Writer(this, folder, info, start.IsIdempotent);
        writer.Enqueue(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, SourceFile), start.Source);
            WriteInfo(folder, info);
        });
        return writer;
    }

    private IEnumerable<string> Folders()
    {
        try
        {
            return Directory.Exists(Root) ? Directory.GetDirectories(Root) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CodeModeStoredRun? ReadInfo(string folder)
    {
        var path = Path.Combine(folder, RunFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (Text(root, "id") is not { } id || Text(root, "sessionId") is not { } sessionId)
            {
                return null;
            }

            return new CodeModeStoredRun(id, sessionId)
            {
                Title = Text(root, "title"),
                CallId = Text(root, "callId"),
                SourceHash = Text(root, "sourceHash") ?? string.Empty,
                ArgumentsJson = Text(root, "argumentsJson"),
                ResumesRunId = Text(root, "resumes"),
                StartedAt = Time(root, "startedAt") ?? DateTimeOffset.MinValue,
                EndedAt = Time(root, "endedAt"),
                Status = Text(root, "status") ?? "running",
                AutoResumedAt = Time(root, "autoResumedAt"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteInfo(string folder, CodeModeStoredRun info)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", info.Id);
            writer.WriteString("sessionId", info.SessionId);
            WriteOptional(writer, "title", info.Title);
            WriteOptional(writer, "callId", info.CallId);
            writer.WriteString("sourceHash", info.SourceHash);
            WriteOptional(writer, "argumentsJson", info.ArgumentsJson);
            WriteOptional(writer, "resumes", info.ResumesRunId);
            writer.WriteString("startedAt", info.StartedAt);
            if (info.EndedAt is { } endedAt)
            {
                writer.WriteString("endedAt", endedAt);
            }

            writer.WriteString("status", info.Status);
            if (info.AutoResumedAt is { } resumedAt)
            {
                writer.WriteString("autoResumedAt", resumedAt);
            }
            writer.WriteEndObject();
        }

        // Written whole, then moved over the previous one: a reader never sees half of it.
        var path = Path.Combine(folder, RunFile);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        // On Windows a file being read cannot be replaced: a list of the runs that reads it as the run ends would leave
        // the run without its end. A reader holds it for a moment only.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(attempt * 5);
            }
        }
    }

    // How many times the run file is moved into place before its write fails: about one second in all.
    private const int MoveAttempts = 20;

    private static List<CodeModeReplayStep> ReadSteps(string path)
    {
        var steps = new Dictionary<int, CodeModeReplayStep>();
        foreach (var line in Lines(path))
        {
            if (Parse(line) is not { } root)
            {
                continue;
            }

            if (Number(root, "index") is not { } index || Text(root, "key") is not { } key)
            {
                continue;
            }

            steps[index] = new CodeModeReplayStep(index, key, root.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.True)
            {
                Id = Text(root, "id"),
                ParentIndex = Number(root, "parent"),
                HasResult = root.TryGetProperty("hasResult", out var hasResult) && hasResult.ValueKind == JsonValueKind.True,
                ResultJson = Text(root, "result"),
            };
        }

        return [.. steps.Values.OrderBy(static step => step.Index)];
    }

    /// <summary>
    /// Lists the interrupted runs that resume by themselves: they stopped while they slept, and CodeAlta has not resumed them
    /// yet, by itself or because a run resumes them.
    /// </summary>
    /// <returns>The runs, the soonest to wake first.</returns>
    public IReadOnlyList<CodeModeStoredRun> ListSleepingInterrupted()
    {
        var infos = new List<CodeModeStoredRun>();
        foreach (var folder in Folders())
        {
            try
            {
                if (ReadInfo(folder) is { } info)
                {
                    infos.Add(info);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var resumed = infos.Select(static info => info.ResumesRunId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. infos.Where(info => info.Status == "interrupted" && info.AutoResumedAt is null && !resumed.Contains(info.Id))
            .Select(info => TryLoad(info.Id)).OfType<CodeModeStoredRun>().Where(static run => run.SleepingUntil is not null)
            .OrderBy(static run => run.SleepingUntil)];
    }

    /// <summary>Records that CodeAlta resumed a run by itself, so that it never does it twice.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>True when it was recorded now; false when the run is unknown or was already resumed.</returns>
    public bool MarkAutoResumed(string runId)
    {
        if (!IsRunId(runId))
        {
            return false;
        }

        var folder = Path.Combine(Root, runId);
        try
        {
            if (ReadInfo(folder) is not { AutoResumedAt: null } info)
            {
                return false;
            }

            WriteInfo(folder, info with { AutoResumedAt = _time.GetUtcNow() });
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The sleeps of a run: a line when one starts, with its wake time, and one when it wakes.
    private static Dictionary<string, DateTimeOffset> ReadSleeps(string path, out DateTimeOffset? sleepingUntil)
    {
        var sleeps = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var open = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in Lines(path))
        {
            if (Parse(line) is not { } root || Text(root, "key") is not { } key)
            {
                continue;
            }

            if (Time(root, "wakeAt") is { } wakeAt)
            {
                sleeps[key] = wakeAt;
                open[key] = wakeAt;
            }
            else if (root.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True)
            {
                open.Remove(key);
            }
        }

        sleepingUntil = open.Count == 0 ? null : open.Values.Max();
        return sleeps;
    }

    private static List<CodeModeStoredCall> ReadInFlightCalls(string path)
    {
        var started = new Dictionary<string, CodeModeStoredCall>(StringComparer.Ordinal);
        foreach (var line in Lines(path))
        {
            if (Parse(line) is not { } root || Text(root, "call") is not { } call)
            {
                continue;
            }

            if (Text(root, "event") == "started")
            {
                started[call] = new CodeModeStoredCall(Text(root, "path") ?? "?", Text(root, "stepKey"), Text(root, "args") ?? "{}");
            }
            else
            {
                started.Remove(call);
            }
        }

        return [.. started.Values];
    }

    // The lines of a file written line by line: the last one may be cut by the end of the process.
    private static IEnumerable<string> Lines(string path)
        => File.Exists(path) ? File.ReadLines(path).Where(static line => line.Length > 0) : [];

    private static JsonElement? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static DateTimeOffset? Time(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time) ? time : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static string Line(Action<Utf8JsonWriter> write)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static string Shorten(string argumentsJson)
    {
        var text = argumentsJson.ReplaceLineEndings(" ");
        return text.Length <= MaxArgumentCharacters ? text : text[..MaxArgumentCharacters] + "…";
    }

    /// <summary>
    /// What a run writes as it goes: a queue of writes, done in order on the thread pool, so that the script never waits for
    /// the disk. A write that fails is lost, never the run.
    /// </summary>
    internal sealed class Writer
    {
        private readonly CodeModeRunStore _store;
        private readonly string _folder;
        private readonly Func<string, bool>? _isIdempotent;
        private readonly Lock _gate = new();
        private CodeModeStoredRun _info;
        private Task _tail = Task.CompletedTask;

        public Writer(CodeModeRunStore store, string folder, CodeModeStoredRun info, Func<string, bool>? isIdempotent)
        {
            _store = store;
            _folder = folder;
            _info = info;
            _isIdempotent = isIdempotent;
        }

        public void Enqueue(Action write)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(_ =>
                {
                    try
                    {
                        write();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        public void StepEnded(CodeModeStepRecord record)
        {
            // A record without a key cannot be matched by a replay.
            if (record.Key is null)
            {
                return;
            }

            Enqueue(() => File.AppendAllText(Path.Combine(_folder, StepsFile), Line(writer =>
            {
                writer.WriteNumber("index", record.Index);
                writer.WriteString("key", record.Key);
                writer.WriteString("id", record.StepId);
                if (record.ParentIndex is { } parent)
                {
                    writer.WriteNumber("parent", parent);
                }

                writer.WriteBoolean("succeeded", record.Succeeded);
                writer.WriteBoolean("hasResult", record.HasResult);
                WriteOptional(writer, "result", record.ResultJson);
                writer.WriteNumber("durationMs", (long)record.Duration.TotalMilliseconds);
                if (record.Replayed)
                {
                    writer.WriteBoolean("replayed", true);
                }
            })));
        }

        public void CallStarted(CodeModeCall call)
        {
            if (_isIdempotent?.Invoke(call.Path) == true)
            {
                return;
            }

            Enqueue(() => File.AppendAllText(Path.Combine(_folder, CallsFile), Line(writer =>
            {
                writer.WriteString("event", "started");
                writer.WriteString("call", CallKey(call.Scope, call.Index));
                writer.WriteString("path", call.Path);
                WriteOptional(writer, "stepKey", call.StepKey);
                writer.WriteString("args", Shorten(call.ArgumentsJson));
            })));
        }

        public void SleepStarted(CodeModeSleep sleep)
            => Enqueue(() => File.AppendAllText(Path.Combine(_folder, SleepsFile), Line(writer =>
            {
                writer.WriteString("key", sleep.Key);
                writer.WriteString("wakeAt", sleep.WakeAt);
            })));

        public void SleepEnded(CodeModeSleep sleep)
            => Enqueue(() => File.AppendAllText(Path.Combine(_folder, SleepsFile), Line(writer =>
            {
                writer.WriteString("key", sleep.Key);
                writer.WriteBoolean("ended", true);
            })));

        public void CallEnded(CodeModeCallRecord record)
        {
            if (_isIdempotent?.Invoke(record.Path) == true)
            {
                return;
            }

            Enqueue(() => File.AppendAllText(Path.Combine(_folder, CallsFile), Line(writer =>
            {
                writer.WriteString("event", "ended");
                writer.WriteString("call", CallKey(record.Scope, record.Index));
                writer.WriteString("status", record.Status.ToString().ToLowerInvariant());
            })));
        }

        // What was asked so far: a task that completes once it is written.
        public Task FlushAsync()
        {
            lock (_gate)
            {
                return _tail;
            }
        }

        // The end of the run, then the runs past the retention go: a task that completes once both are written.
        public Task EndAsync(string status) => EndAsync(status, null);

        // The end of the run with what its session was given, then the runs past the retention go.
        public Task EndAsync(string status, string? result)
        {
            Enqueue(() =>
            {
                _info = _info with { EndedAt = _store._time.GetUtcNow(), Status = status };
                if (Directory.Exists(_folder))
                {
                    if (result is not null)
                    {
                        File.WriteAllText(Path.Combine(_folder, ResultFile), result);
                    }

                    WriteInfo(_folder, _info);
                }

                _store.Prune();
            });
            lock (_gate)
            {
                return _tail;
            }
        }

        private static string CallKey(int scope, int index) => string.Create(CultureInfo.InvariantCulture, $"{scope}:{index}");
    }
}
