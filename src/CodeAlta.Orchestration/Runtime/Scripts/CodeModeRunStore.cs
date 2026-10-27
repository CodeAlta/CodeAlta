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
}

/// <summary>
/// The durable record of the runs of scripts, for a resume after a restart and the reruns over recorded results: under
/// <c>codemode/runs/&lt;run-id&gt;/</c> of the state root, <c>run.json</c>, <c>source.js</c>, <c>steps.jsonl</c> (one line per
/// step that ended, with its result) and <c>calls.jsonl</c> (the start and the end of each call that is not idempotent).
/// A run writes as it goes, in order, off the thread of its script. The store keeps the 100 most recent runs that ended,
/// for 14 days at most. A host whose state is thrown away (<c>altacli</c>) keeps none.
/// </summary>
public sealed class CodeModeRunStore
{
    private const string RunFile = "run.json";
    private const string SourceFile = "source.js";
    private const string StepsFile = "steps.jsonl";
    private const string CallsFile = "calls.jsonl";
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
            };
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
                var endedAt = new[] { RunFile, StepsFile, CallsFile }.Select(name => Path.Combine(folder, name)).Where(File.Exists)
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

    /// <summary>Removes the runs that ended beyond the retention: past the most recent ones it keeps, or too old.</summary>
    /// <returns>How many runs it removed.</returns>
    public int Prune()
    {
        var ended = new List<(string Folder, DateTimeOffset EndedAt)>();
        foreach (var folder in Folders())
        {
            try
            {
                if (ReadInfo(folder) is { EndedAt: { } endedAt })
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
            writer.WriteEndObject();
        }

        // Written whole, then moved over the previous one: a reader never sees half of it.
        var path = Path.Combine(folder, RunFile);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

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
        public Task EndAsync(string status)
        {
            Enqueue(() =>
            {
                _info = _info with { EndedAt = _store._time.GetUtcNow(), Status = status };
                if (Directory.Exists(_folder))
                {
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
