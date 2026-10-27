using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace CodeAlta.CodeMode;

/// <summary>
/// One run of a script: an engine of its own, the bridge its prelude calls, and what the run records. The script
/// runs on one thread at a time; the calls it makes run on the thread pool and only settle promises, so the run is
/// the only writer of its engine.
/// </summary>
internal sealed partial class CodeModeRun
{
    private const string PreludeName = "codemode-prelude.js";
    private const string WorkflowPath = "codemode.workflow";

    // The function behind workflow(): not part of the API, so no namespace shows it.
    private static readonly CodeModeFunction WorkflowFunction = new(WorkflowPath) { Parallel = true };

    // The function behind sleep(): a timer of the engine, recorded so that a resume wakes when the run that slept would have.
    private const string SleepPath = "codemode.sleep";
    private static readonly CodeModeFunction SleepFunction = new(SleepPath) { Parallel = true };

    private readonly CodeModeApi _api;
    private readonly CodeModeScriptCache _cache;
    private readonly CodeModeSource _source;
    private readonly ICodeModeCallHandler _handler;
    private readonly CodeModeRunOptions _options;
    private readonly CodeModeLimits _limits;
    private readonly ICodeModeRunObserver? _observer;
    private readonly Stopwatch _clock = new();
    private readonly StepTracker _tracker = new();
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly ConcurrentDictionary<int, PendingCall> _pending = new();
    private readonly CancellationTokenSource _stall = new();
    private readonly CancellationTokenSource _timeout;
    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly List<CodeModeCallRecord> _calls = [];
    private readonly List<CodeModeStepRecord> _steps = [];
    private readonly Dictionary<int, (ActiveStep Step, TimeSpan StartedAt)> _openSteps = [];
    private readonly CodeModeStepSpace _space;
    private readonly System.Runtime.CompilerServices.StrongBox<int> _scopes;
    private readonly Dictionary<string, int> _workflowCalls = new(StringComparer.Ordinal);
    // The time the limit counts: the run's, less the time it waits for the user.
    private readonly Stopwatch _limitClock = new();
    private int _userWaits;
    private readonly List<CodeModeOutputEntry> _output = [];
    private int _outputCharacters;
    private int _droppedOutput;
    private int _callCount;
    private int _inFlight;
    private int _running;
    private int _maxRunning;
    private long _lastActivity;
    private long _lastCheck;
    private long _sliceStart;
    private long _scriptTicks;
    private volatile bool _ended;
    private Engine? _engine;

    public CodeModeRun(CodeModeApi api, CodeModeScriptCache cache, CodeModeSource source, ICodeModeCallHandler handler, CodeModeRunOptions options, ICodeModeRunObserver? observer)
    {
        _api = api;
        _cache = cache;
        _source = source;
        _handler = handler;
        _options = options;
        _limits = options.Limits;
        _observer = observer;
        _slots = new SemaphoreSlim(Math.Max(1, _limits.MaxCallsInFlight));
        _timeout = new CancellationTokenSource(_limits.Timeout);
        // The run exec runs and the scripts its workflow() calls start share their steps, and their replay.
        _space = options.Steps ?? new CodeModeStepSpace(options.Replay);
        _scopes = options.Scopes ?? new System.Runtime.CompilerServices.StrongBox<int>(0);
    }

    public async Task<CodeModeRunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        _clock.Start();
        _limitClock.Start();
        Touch();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token, _stall.Token);
        string? valueJson = null;
        CodeModeRunError? error = null;
        try
        {
            var engine = CreateEngine(run.Token);
            _engine = engine;
            var prelude = _cache.GetOrPrepare("prelude:" + _api.Hash, _api.Prelude, PreludeName);
            engine.Evaluate(in prelude);
            var script = _cache.GetOrPrepare("script:" + _source.Hash + ":" + _source.Name, _source.Wrapped, _source.Name);
            var evaluation = engine.EvaluateAsync(in script, run.Token);
            var watchdog = WatchForStallAsync(evaluation, run.Token);
            JsValue value;
            try
            {
                value = await evaluation.ConfigureAwait(false);
            }
            finally
            {
                _ended = true;
                await watchdog.ConfigureAwait(false);
            }

            valueJson = Serialize(engine, value, out error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ended = true;
            error = Describe(ex, cancellationToken);
        }

        await EndPendingCallsAsync().ConfigureAwait(false);
        _clock.Stop();
        _timeout.Dispose();
        _stall.Dispose();
        var status = error switch
        {
            null => CodeModeRunStatus.Completed,
            { Kind: CodeModeErrorKind.Cancelled } => CodeModeRunStatus.Cancelled,
            _ => CodeModeRunStatus.Failed,
        };

        lock (_gate)
        {
            return new CodeModeRunResult(status, _clock.Elapsed)
            {
                ValueJson = valueJson,
                Error = error,
                Calls = [.. _calls.OrderBy(static call => call.Index)],
                Steps = [.. _steps],
                Output = [.. _output],
                DroppedOutputCharacters = _droppedOutput,
                MaxCallsAtOnce = _maxRunning,
                ScriptTime = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _scriptTicks)),
                Source = _source,
            };
        }
    }

    private Engine CreateEngine(CancellationToken cancellationToken)
    {
        var engine = new Engine(options =>
        {
            options.Strict();
            options.DisableStringCompilation();
            // The CPU time of a slice is measured by the activity constraint (CheckActivity): Jint's own timer is not
            // reset when the script resumes after an await, so it would count awaited time.
            options.MaxStatements(_limits.MaxStatements);
            options.LimitMemory(_limits.MemoryBytes);
            options.LimitRecursion(_limits.MaxRecursionDepth);
            options.RegexTimeoutInterval(_limits.RegexTimeout);
            options.MaxArraySize(_limits.MaxArraySize);
            // Jint 4.17: without the guard, deep recursion ends the process; the promise timeout (10 s by default)
            // would fail any longer await; and only Task interop settles a host promise safely from another thread.
            options.Constraints.StackOverflowGuard = true;
            options.Constraints.PromiseTimeout = Timeout.InfiniteTimeSpan;
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.CancellationToken(cancellationToken);
            options.Culture = CultureInfo.InvariantCulture;
            options.TimeZone = TimeZoneInfo.Local;
            if (_options.Clock is { } clock)
            {
                options.TimeSystem = new ClockTimeSystem(clock, TimeZoneInfo.Local, CultureInfo.InvariantCulture);
            }

            options.Host.JobCallbacks = _tracker;
            options.Constraint(new ActivityConstraint(this));
        });

        engine.SetValue("__host", new ClrFunction(engine, "__host", Call, 2));
        engine.SetValue("__output", new ClrFunction(engine, "__output", Output, 2));
        engine.SetValue("__stepBegin", new ClrFunction(engine, "__stepBegin", StepBegin, 1));
        engine.SetValue("__stepRun", new ClrFunction(engine, "__stepRun", StepRun, 2));
        engine.SetValue("__stepEnd", new ClrFunction(engine, "__stepEnd", StepEnd, 4));
        engine.SetValue("__random", new ClrFunction(engine, "random", (_, _) => _options.Random is { } random ? random() : _random.NextDouble(), 0));
        if (_options.ArgumentsJson is { } arguments)
        {
            engine.SetValue("__args", arguments);
        }

        return engine;
    }

    // The bridge of every function of the API: the prelude passes the path and the arguments as JSON, and receives a
    // promise of the reply envelope, { ok, value } or { ok: false, error }.
    private JsValue Call(JsValue self, JsValue[] arguments)
    {
        Touch();
        var path = arguments.Length > 0 && arguments[0].IsString() ? arguments[0].AsString() : string.Empty;
        var json = arguments.Length > 1 && arguments[1].IsString() ? arguments[1].AsString() : "{}";
        var index = Interlocked.Increment(ref _callCount) - 1;
        // A call a nested script makes outside its steps belongs to the step of the workflow() call that started it.
        var step = _tracker.Current ?? _options.OuterStep;
        var call = new CodeModeCall(index, path, json, _tracker.Current?.Id, CallSite())
        {
            StepKey = step?.Key,
            Scope = _options.Scope,
            Step = step,
            ScriptSegment = path == WorkflowPath ? ScriptSegment(json) : null,
        };
        return JsValue.FromObject(_engine!, RunCallAsync(call));
    }

    // The segment a workflow() call gives the keys of the steps of its script: the name of the script (a library name,
    // or the file name without extension) and how many workflow() calls of that name this script made before it. It is
    // taken here, in the order of the calls on the engine's thread, so that scripts that then run at the same time
    // never swap their keys.
    private string ScriptSegment(string argumentsJson)
    {
        string? name = null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString()
                : root.TryGetProperty("scriptPath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String ? Path.GetFileNameWithoutExtension(pathElement.GetString())
                : null;
        }
        catch (JsonException)
        {
        }

        name = string.IsNullOrEmpty(name) ? "workflow" : name;
        lock (_gate)
        {
            var count = _workflowCalls.GetValueOrDefault(name);
            _workflowCalls[name] = count + 1;
            return CodeModeReplay.KeyOf(name, count);
        }
    }

    private async Task<string> RunCallAsync(CodeModeCall call)
    {
        Interlocked.Increment(ref _inFlight);
        var queuedAt = _clock.Elapsed;
        var function = call.Path == WorkflowPath ? WorkflowFunction : call.Path == SleepPath ? SleepFunction : _api.Find(call.Path);
        if (function is null)
        {
            return Finish(call, queuedAt, queuedAt, CodeModeCallStatus.Failed, CodeModeCallResult.Failure(new("TypeError", $"`{call.Path}` is not a function of this session.")));
        }

        if (call.Index >= _limits.MaxCalls)
        {
            return Finish(call, queuedAt, queuedAt, CodeModeCallStatus.Refused, CodeModeCallResult.Failure(new("RangeError",
                $"The script made {_limits.MaxCalls} calls, the most one run may make. Work on fewer items per run.")
            { Code = "codemode.callLimit" }));
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token, _stall.Token);
        var pending = new PendingCall(cancellation);
        _pending[call.Index] = pending;
        _observer?.CallStarted(call);
        var startedAt = queuedAt;
        try
        {
            await _slots.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!function.Parallel)
                {
                    await _exclusive.WaitAsync(cancellation.Token).ConfigureAwait(false);
                }

                try
                {
                    var running = Interlocked.Increment(ref _running);
                    UpdateMax(running);
                    startedAt = _clock.Elapsed;
                    // The time the user takes to answer is not the script's, nor the time it sleeps: its time limit stops meanwhile.
                    var pauses = function.WaitsForUser || ReferenceEquals(function, SleepFunction);
                    if (pauses)
                    {
                        PauseLimit(true);
                    }

                    try
                    {
                        var result = call.Path == WorkflowPath
                            ? await RunWorkflowAsync(call, cancellation.Token).ConfigureAwait(false)
                            : call.Path == SleepPath ? await SleepAsync(call, cancellation.Token).ConfigureAwait(false)
                            : await Task.Run(() => _handler.InvokeAsync(call, cancellation.Token).AsTask(), cancellation.Token).ConfigureAwait(false);
                        return Finish(call, queuedAt, startedAt, result.Succeeded ? CodeModeCallStatus.Succeeded : CodeModeCallStatus.Failed, result);
                    }
                    finally
                    {
                        if (pauses)
                        {
                            PauseLimit(false);
                        }

                        Interlocked.Decrement(ref _running);
                    }
                }
                finally
                {
                    if (!function.Parallel)
                    {
                        _exclusive.Release();
                    }
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var reason = pending.NotAwaited ? "Not awaited: the script ended before this call did." : "The run was stopped.";
            return Finish(call, queuedAt, startedAt, CodeModeCallStatus.Cancelled, CodeModeCallResult.Failure(new("AbortError", reason)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Finish(call, queuedAt, startedAt, CodeModeCallStatus.Failed, CodeModeCallResult.Failure(new("ToolError", ex.Message)));
        }
        finally
        {
            _pending.TryRemove(call.Index, out _);
        }
    }

    private string Finish(CodeModeCall call, TimeSpan queuedAt, TimeSpan startedAt, CodeModeCallStatus status, CodeModeCallResult result)
    {
        var envelope = Envelope(result, out var invalid);
        if (invalid is not null)
        {
            status = CodeModeCallStatus.Failed;
            result = CodeModeCallResult.Failure(invalid);
        }

        var record = new CodeModeCallRecord(call.Index, call.Path, call.StepId, call.Location, queuedAt, _clock.Elapsed - startedAt, status)
        {
            ErrorMessage = result.Error?.Message,
            Queued = startedAt - queuedAt,
            Scope = call.Scope,
        };
        lock (_gate)
        {
            _calls.Add(record);
        }

        _observer?.CallEnded(record);
        Interlocked.Decrement(ref _inFlight);
        Touch();
        return envelope;
    }

    private static string Envelope(CodeModeCallResult result, out CodeModeCallError? invalid)
    {
        invalid = null;
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            Write(writer, result);
        }
        catch (JsonException ex)
        {
            invalid = new CodeModeCallError("ToolError", $"The tool answered a value that is not JSON: {ex.Message}");
            buffer.Clear();
            using var writer = new Utf8JsonWriter(buffer);
            Write(writer, CodeModeCallResult.Failure(invalid));
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void Write(Utf8JsonWriter writer, CodeModeCallResult result)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", result.Succeeded);
            if (result.Error is { } error)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteString("name", error.Name);
                writer.WriteString("message", error.Message);
                if (error.Code is not null)
                {
                    writer.WriteString("code", error.Code);
                }

                if (error.DataJson is not null)
                {
                    writer.WritePropertyName("data");
                    writer.WriteRawValue(error.DataJson);
                }

                writer.WriteEndObject();
            }
            else if (result.ValueJson is not null)
            {
                writer.WritePropertyName("value");
                writer.WriteRawValue(result.ValueJson);
            }

            writer.WriteEndObject();
        }
    }

    // sleep(duration): a timer, which wakes when the run it resumes recorded it would. It is recorded when it starts and
    // when it wakes: a sleep the run stopped during has no end, and a resume of the run wakes at its time.
    private async Task<CodeModeCallResult> SleepAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        TimeSpan duration;
        string? error = null;
        using (var document = JsonDocument.Parse(call.ArgumentsJson))
        {
            if (!document.RootElement.TryGetProperty("duration", out var value) || !CodeModeSleep.TryParse(value, out duration, out error))
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", error ?? "sleep(duration): the duration is missing."));
            }
        }

        var sleep = _space.Sleep(call.Step?.Key, call.Step?.Id, duration, DateTimeOffset.UtcNow);
        _observer?.SleepStarted(sleep);
        var left = sleep.WakeAt - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left, cancellationToken).ConfigureAwait(false);
        }

        _observer?.SleepEnded(sleep);
        return CodeModeCallResult.Success(null);
    }

    // workflow(name | { scriptPath }, args): the script runs in an engine of its own, with the same functions and the
    // same handler, its calls followed by the same observer; its output joins this run's, and its value or its error
    // is the value or the error of the call.
    private async Task<CodeModeCallResult> RunWorkflowAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        if (_options.ResolveScript is not { } resolve)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "workflow() is not available in this run."));
        }

        if (_options.Depth + 1 > _options.MaxWorkflowDepth)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("RangeError", $"Scripts may start scripts {_options.MaxWorkflowDepth} levels deep at most.") { Code = "codemode.workflowDepth" });
        }

        string? name;
        string? scriptPath;
        string argumentsJson;
        using (var document = JsonDocument.Parse(call.ArgumentsJson))
        {
            var root = document.RootElement;
            name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
            scriptPath = root.TryGetProperty("scriptPath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
            argumentsJson = root.TryGetProperty("args", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object ? argumentsElement.GetRawText() : "{}";
        }

        CodeModeSource source;
        try
        {
            source = await resolve(new CodeModeScriptReference(name, scriptPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("ToolError", ex.Message) { Code = "codemode.workflowNotFound" });
        }

        if (source.Error is { } error)
        {
            var where = error.Location is { } location ? $" (line {location.Line})" : string.Empty;
            return CodeModeCallResult.Failure(new CodeModeCallError(error.Name, $"{source.Name}{where}: {error.Message}") { Code = "codemode.workflowFailed" });
        }

        // A library script says its parameters (meta.params): its arguments are checked before it runs.
        if (source.Descriptor?.ParametersSchemaJson is { } parameters && CheckArguments(parameters, argumentsJson) is { } invalid)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"workflow(\"{name ?? source.Name}\", args): {invalid}") { Code = "codemode.workflowArguments" });
        }

        var nested = new CodeModeRun(_api, _cache, source, _handler, _options with
        {
            ArgumentsJson = argumentsJson,
            Depth = _options.Depth + 1,
            Steps = _space,
            StepPrefix = _options.StepPrefix + (call.ScriptSegment ?? CodeModeReplay.KeyOf(name ?? Path.GetFileNameWithoutExtension(scriptPath ?? source.Name), 0)) + "/",
            Scopes = _scopes,
            Scope = Interlocked.Increment(ref _scopes.Value),
            OuterStep = call.Step,
            ParentRun = this,
        }, _observer);
        var result = await nested.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        // Its steps are steps of this run: they share its keys and its replay.
        lock (_gate)
        {
            _steps.AddRange(result.Steps);
        }

        foreach (var entry in result.Output)
        {
            AppendOutput(entry.Kind, entry.Text, entry.StepId);
        }

        if (result.Status == CodeModeRunStatus.Completed)
        {
            return CodeModeCallResult.Success(result.ValueJson);
        }

        var failure = result.Error!;
        var line = failure.Location is { } at ? $" (line {at.Line})" : string.Empty;
        return CodeModeCallResult.Failure(new CodeModeCallError(failure.Name, $"{source.Name}{line}: {failure.Message}") { Code = "codemode.workflowFailed" });
    }

    // The arguments of a script against the schema of its parameters: missing ones, unknown ones (with the nearest
    // name), and values of the wrong type. Null when they fit.
    internal static string? CheckArguments(string parametersSchemaJson, string argumentsJson)
    {
        using var schema = JsonDocument.Parse(parametersSchemaJson);
        using var arguments = JsonDocument.Parse(argumentsJson);
        if (arguments.RootElement.ValueKind != JsonValueKind.Object) return "the arguments are an object.";
        var root = schema.RootElement;
        var properties = root.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? declared : default;
        if (root.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !arguments.RootElement.TryGetProperty(name.GetString()!, out _))
                {
                    return $"the argument `{name.GetString()}` is missing.";
                }
            }
        }

        foreach (var argument in arguments.RootElement.EnumerateObject())
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(argument.Name, out var parameter))
            {
                var known = properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Select(static property => property.Name).ToArray() : [];
                return known.Length == 0 ? $"the script takes no argument `{argument.Name}`."
                    : $"the script takes no argument `{argument.Name}`: it takes {string.Join(", ", known.Select(static name => $"`{name}`"))}.";
            }

            if (parameter.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && !Fits(type.GetString()!, argument.Value))
            {
                return $"the argument `{argument.Name}` must be {type.GetString()}, not {argument.Value.ValueKind.ToString().ToLowerInvariant()}.";
            }
        }

        return null;

        static bool Fits(string type, JsonElement value) => type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => true,
        };
    }

    private JsValue Output(JsValue self, JsValue[] arguments)
    {
        Touch();
        var kind = arguments.Length > 0 && arguments[0].IsNumber() ? (CodeModeOutputKind)(int)arguments[0].AsNumber() : CodeModeOutputKind.Log;
        var text = arguments.Length > 1 ? arguments[1].ToString() : string.Empty;
        AppendOutput(kind, text, _tracker.Current?.Id);
        return JsValue.Undefined;
    }

    private void AppendOutput(CodeModeOutputKind kind, string text, string? stepId)
    {
        CodeModeOutputEntry? entry = null;
        lock (_gate)
        {
            var room = _limits.MaxLogCharacters - _outputCharacters;
            if (room <= 0)
            {
                _droppedOutput += text.Length;
            }
            else
            {
                if (text.Length > room)
                {
                    _droppedOutput += text.Length - room;
                    text = text[..room];
                }

                _outputCharacters += text.Length;
                entry = new CodeModeOutputEntry(kind, text, stepId);
                _output.Add(entry);
            }
        }

        if (entry is not null)
        {
            _observer?.Output(entry);
        }
    }

    // A step starts: its key counts the steps of its full id (after the path of the scripts that start it) started
    // before it in the run. It runs (a handle for __stepRun and __stepEnd), or a replay gives the value a previous run
    // recorded (the JSON, "" for undefined) and it does not run.
    private JsValue StepBegin(JsValue self, JsValue[] arguments)
    {
        Touch();
        var id = arguments[0].AsString();
        // The first steps of a nested script start in the step of the workflow() call that started it.
        var parent = _tracker.Current ?? _options.OuterStep;
        var begun = _space.Begin(_options.StepPrefix + id, parent?.Index);
        if (begun.Report is not null)
        {
            AppendOutput(CodeModeOutputKind.Warning, begun.Report, null);
        }

        if (begun.Replayed is { } replayed)
        {
            Replay(id, replayed);
            return replayed[0].Recorded.ResultJson ?? string.Empty;
        }

        var handle = begun.Index + 1;
        var step = new ActiveStep(id, begun.Key, begun.Index, parent?.Index);
        lock (_gate)
        {
            _openSteps[handle] = (step, _clock.Elapsed);
        }

        _observer?.StepStarted(id);
        return handle;
    }

    // A replayed step, and the steps it started in the run that recorded it: each is recorded as replayed, so that this
    // run is resumed as that one was.
    private void Replay(string id, IReadOnlyList<CodeModeStepSpace.ReplayedStep> replayed)
    {
        foreach (var (step, index, parent) in replayed)
        {
            var stepId = ReferenceEquals(step, replayed[0].Recorded) ? id : step.Id ?? CodeModeReplay.ParseKey(step.Key).Id;
            var record = new CodeModeStepRecord(stepId, _clock.Elapsed, TimeSpan.Zero, step.Succeeded)
            {
                Key = step.Key,
                Index = index,
                ParentIndex = parent,
                HasResult = step.HasResult,
                ResultJson = step.ResultJson,
                Replayed = true,
            };
            lock (_gate)
            {
                _steps.Add(record);
            }

            _observer?.StepStarted(stepId);
            _observer?.StepEnded(record);
        }
    }

    // Runs the function of a step with the step as the current one: the job callbacks carry it across every await
    // started inside, so each call made there belongs to the step, also in parallel branches.
    private JsValue StepRun(JsValue self, JsValue[] arguments)
    {
        var handle = (int)arguments[0].AsNumber();
        ActiveStep? step;
        lock (_gate)
        {
            step = _openSteps.TryGetValue(handle, out var open) ? open.Step : null;
        }

        var previous = _tracker.Current;
        _tracker.Current = step;
        try
        {
            return arguments[1].Call();
        }
        finally
        {
            _tracker.Current = previous;
        }
    }

    private JsValue StepEnd(JsValue self, JsValue[] arguments)
    {
        Touch();
        var handle = (int)arguments[0].AsNumber();
        var succeeded = arguments[1].IsBoolean() && arguments[1].AsBoolean();
        var message = arguments.Length > 2 && arguments[2].IsString() ? arguments[2].AsString() : null;
        // The value of a step that completed: "" is undefined; a value over the cap is not recorded, so never replayed.
        var json = succeeded && arguments.Length > 3 && arguments[3].IsString() ? arguments[3].AsString() : null;
        var hasResult = json is not null && Encoding.UTF8.GetByteCount(json) <= CodeModeReplay.MaxResultBytes;
        CodeModeStepRecord? record = null;
        lock (_gate)
        {
            if (_openSteps.Remove(handle, out var open))
            {
                record = new CodeModeStepRecord(open.Step.Id, open.StartedAt, _clock.Elapsed - open.StartedAt, succeeded)
                {
                    ErrorMessage = message,
                    Key = open.Step.Key,
                    Index = open.Step.Index,
                    ParentIndex = open.Step.ParentIndex,
                    HasResult = hasResult,
                    ResultJson = hasResult && json!.Length > 0 ? json : null,
                };
                _steps.Add(record);
            }
        }

        if (record is not null)
        {
            _observer?.StepEnded(record);
        }

        return JsValue.Undefined;
    }

    // The innermost frame of the script itself, not of the prelude, is where the call was made.
    private CodeModeSourceLocation? CallSite()
    {
        var stack = _engine?.Advanced.StackTrace;
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }

        foreach (var line in stack.Split('\n'))
        {
            if (line.Contains(_source.Name, StringComparison.Ordinal) && FramePattern().Match(line) is { Success: true } frame)
            {
                return Location(int.Parse(frame.Groups["line"].ValueSpan, CultureInfo.InvariantCulture), int.Parse(frame.Groups["column"].ValueSpan, CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    // The wrapper opens the script on its first line: only the columns of that line move.
    private static CodeModeSourceLocation Location(int line, int column)
        => new(line, line == 1 ? Math.Max(1, column - CodeModeSource.FirstLineOffset) : Math.Max(1, column));

    private async Task WatchForStallAsync(Task evaluation, CancellationToken cancellationToken)
    {
        var delay = _limits.StallDelay;
        var period = TimeSpan.FromMilliseconds(Math.Max(10, delay.TotalMilliseconds / 2));
        while (!_ended && !evaluation.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAny(evaluation, Task.Delay(period, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_ended || evaluation.IsCompleted)
            {
                return;
            }

            // Nothing is in flight and no JavaScript ran for a while, yet the script still waits: no call can resume it.
            if (Volatile.Read(ref _inFlight) == 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= delay)
            {
                await _stall.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task EndPendingCallsAsync()
    {
        var pending = _pending.Values.ToArray();
        foreach (var call in pending)
        {
            call.NotAwaited = !_stall.IsCancellationRequested && !_timeout.IsCancellationRequested;
            try
            {
                await call.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The call ended between the snapshot and its cancellation.
            }
        }

        // Calls end once their handler observes the cancellation; a handler that ignores it does not hold the result.
        var deadline = Stopwatch.GetTimestamp();
        while (!_pending.IsEmpty && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private string? Serialize(Engine engine, JsValue value, out CodeModeRunError? error)
    {
        error = null;
        if (value.IsUndefined())
        {
            return null;
        }

        try
        {
            var json = new Jint.Native.Json.JsonSerializer(engine).Serialize(value);
            return json.IsUndefined() ? null : json.AsString();
        }
        catch (JavaScriptException ex)
        {
            error = new CodeModeRunError(CodeModeErrorKind.Script, "TypeError", $"The value the script returned cannot be written as JSON: {ex.Message}");
            return null;
        }
    }

    private CodeModeRunError Describe(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case PromiseRejectedException rejected:
                return FromJavaScript(rejected.RejectedValue, null);
            case JavaScriptException thrown:
                return FromJavaScript(thrown.Error, Location(thrown.Location.Start.Line, thrown.Location.Start.Column + 1));
            case StatementsCountOverflowException:
                return new(CodeModeErrorKind.Statements, "StatementLimit",
                    $"The script executed more than {_limits.MaxStatements:N0} statements without awaiting a call. Do less work per step, or let tools do it.");
            case MemoryLimitExceededException:
                return new(CodeModeErrorKind.Memory, "MemoryLimit", $"The script allocated more than {_limits.MemoryBytes / (1024 * 1024)} MB at once.");
            case RecursionDepthOverflowException:
                return new(CodeModeErrorKind.Recursion, "RangeError", $"Maximum call stack size exceeded: the script recursed deeper than {_limits.MaxRecursionDepth} calls.");
            case RegexMatchTimeoutException:
                return new(CodeModeErrorKind.Regex, "RegexTimeout", $"A regular expression took more than {_limits.RegexTimeout.TotalMilliseconds:0} ms to match.");
            case TimeoutException:
                return new(CodeModeErrorKind.CpuTime, "CpuTimeLimit",
                    $"The script computed for more than {_limits.CpuTimePerSlice.TotalSeconds:0.#} s without awaiting a call. Awaited time does not count; a busy loop does.");
            case ParseErrorException parse:
                return new(CodeModeErrorKind.Syntax, "SyntaxError", parse.Description) { Location = Location(Math.Max(1, parse.LineNumber), parse.Column + 1) };
            case OperationCanceledException or ExecutionCanceledException:
                if (_stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return new(CodeModeErrorKind.Stalled, "Stalled",
                        "The script waits for something that nothing can resolve: no call is in flight. Check for a promise that is never resolved, or a call that was not awaited.");
                }

                if (_timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return new(CodeModeErrorKind.Timeout, "Timeout", $"The run reached its time limit of {_limits.Timeout}.");
                }

                return new(CodeModeErrorKind.Cancelled, "Cancelled", "The run was stopped.");
            case JintException jint when jint.Message.Contains("call stack", StringComparison.OrdinalIgnoreCase):
                return new(CodeModeErrorKind.Recursion, "RangeError", jint.Message);
            default:
                return new(CodeModeErrorKind.Script, exception.GetType().Name, exception.Message);
        }
    }

    private CodeModeRunError FromJavaScript(JsValue value, CodeModeSourceLocation? location)
    {
        if (value is not ObjectInstance error)
        {
            return new(CodeModeErrorKind.Script, "Error", value.ToString()) { Location = location };
        }

        var name = error.Get("name") is { } n && n.IsString() ? n.AsString() : "Error";
        var message = error.Get("message") is { } m && !m.IsUndefined() ? m.ToString() : value.ToString();
        var stack = error.Get("stack") is { } s && s.IsString() ? s.AsString() : null;
        if (stack is not null)
        {
            foreach (var line in stack.Split('\n'))
            {
                if (line.Contains(_source.Name, StringComparison.Ordinal) && FramePattern().Match(line) is { Success: true } frame)
                {
                    location = Location(int.Parse(frame.Groups["line"].ValueSpan, CultureInfo.InvariantCulture), int.Parse(frame.Groups["column"].ValueSpan, CultureInfo.InvariantCulture));
                    break;
                }
            }
        }

        var kind = message.Contains("Maximum call stack size exceeded", StringComparison.Ordinal) ? CodeModeErrorKind.Recursion : CodeModeErrorKind.Script;
        return new(kind, name, message) { Location = location, Stack = stack };
    }

    private void UpdateMax(int running)
    {
        var current = Volatile.Read(ref _maxRunning);
        while (running > current)
        {
            var seen = Interlocked.CompareExchange(ref _maxRunning, running, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

    // The time limit counts the time of the run less the time it waits for the user: the first wait stops it, the last
    // one to end starts it again with what is left. A script that workflow() started stops the limit of its parent too.
    internal void PauseLimit(bool waiting)
    {
        lock (_gate)
        {
            if (waiting ? _userWaits++ > 0 : --_userWaits > 0)
            {
                return;
            }

            try
            {
                if (waiting)
                {
                    _limitClock.Stop();
                    _timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                else if (_limits.Timeout != Timeout.InfiniteTimeSpan)
                {
                    _limitClock.Start();
                    var left = _limits.Timeout - _limitClock.Elapsed;
                    _timeout.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
                }
            }
            catch (ObjectDisposedException)
            {
                // The run ended while the user was asked.
            }
        }

        _options.ParentRun?.PauseLimit(waiting);
    }

    // Called on the engine's thread while JavaScript runs: the run is busy, and the time between two close checks
    // is the script's own time. A longer gap is time the script waited (an await): it starts a new slice, and a slice
    // longer than the limit is a busy loop.
    private void CheckActivity()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _lastCheck;
        _lastCheck = now;
        if (previous != 0 && now - previous < Stopwatch.Frequency / 200)
        {
            Interlocked.Add(ref _scriptTicks, now - previous);
        }
        else
        {
            _sliceStart = now;
        }

        Interlocked.Exchange(ref _lastActivity, now);
        if (Stopwatch.GetElapsedTime(_sliceStart, now) > _limits.CpuTimePerSlice)
        {
            throw new TimeoutException();
        }
    }

    [GeneratedRegex(@":(?<line>\d+):(?<column>\d+)\)?\s*$")]
    private static partial Regex FramePattern();

    private sealed class PendingCall(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool NotAwaited { get; set; }
    }

    private sealed class ActivityConstraint(CodeModeRun run) : Constraint
    {
        public override bool IsAmortizable => true;

        public override void Check() => run.CheckActivity();

        public override void Reset()
        {
        }
    }

    // The step that runs: its id, its key (lib#0/read#0 in a script workflow() started), its index and the index of its parent.
    internal sealed record ActiveStep(string Id, string Key, int Index, int? ParentIndex);

    // One per run: the engine runs one job at a time, so a plain field carries the current step.
    private sealed class StepTracker : JobCallbackHooks
    {
        public ActiveStep? Current { get; set; }

        protected override object? Capture(Engine engine) => Current;

        protected override object? Enter(Engine engine, object hostDefined)
        {
            var previous = Current;
            Current = (ActiveStep)hostDefined;
            return previous;
        }

        protected override void Exit(Engine engine, object? token) => Current = (ActiveStep?)token;
    }

    private sealed class ClockTimeSystem(Func<DateTimeOffset> clock, TimeZoneInfo timeZone, CultureInfo culture) : DefaultTimeSystem(timeZone, culture)
    {
        public override DateTimeOffset GetUtcNow() => clock().ToUniversalTime();
    }
}
