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
    private readonly Dictionary<int, (string Id, TimeSpan StartedAt)> _openSteps = [];
    private readonly List<CodeModeOutputEntry> _output = [];
    private int _outputCharacters;
    private int _droppedOutput;
    private int _callCount;
    private int _stepCount;
    private int _inFlight;
    private int _running;
    private int _maxRunning;
    private long _lastActivity;
    private long _lastCheck;
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
    }

    public async Task<CodeModeRunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        _clock.Start();
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
            options.TimeoutInterval(_limits.CpuTimePerSlice);
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
        engine.SetValue("__stepEnd", new ClrFunction(engine, "__stepEnd", StepEnd, 3));
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
        var call = new CodeModeCall(index, path, json, _tracker.Current, CallSite());
        return JsValue.FromObject(_engine!, RunCallAsync(call));
    }

    private async Task<string> RunCallAsync(CodeModeCall call)
    {
        Interlocked.Increment(ref _inFlight);
        var queuedAt = _clock.Elapsed;
        var function = call.Path == WorkflowPath ? WorkflowFunction : _api.Find(call.Path);
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
                    try
                    {
                        var result = call.Path == WorkflowPath
                            ? await RunWorkflowAsync(call, cancellation.Token).ConfigureAwait(false)
                            : await Task.Run(() => _handler.InvokeAsync(call, cancellation.Token).AsTask(), cancellation.Token).ConfigureAwait(false);
                        return Finish(call, queuedAt, startedAt, result.Succeeded ? CodeModeCallStatus.Succeeded : CodeModeCallStatus.Failed, result);
                    }
                    finally
                    {
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

        var nested = new CodeModeRun(_api, _cache, source, _handler, _options with { ArgumentsJson = argumentsJson, Depth = _options.Depth + 1 }, _observer);
        var result = await nested.ExecuteAsync(cancellationToken).ConfigureAwait(false);
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

    private JsValue Output(JsValue self, JsValue[] arguments)
    {
        Touch();
        var kind = arguments.Length > 0 && arguments[0].IsNumber() ? (CodeModeOutputKind)(int)arguments[0].AsNumber() : CodeModeOutputKind.Log;
        var text = arguments.Length > 1 ? arguments[1].ToString() : string.Empty;
        AppendOutput(kind, text, _tracker.Current);
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

    private JsValue StepBegin(JsValue self, JsValue[] arguments)
    {
        Touch();
        var id = arguments[0].AsString();
        var handle = Interlocked.Increment(ref _stepCount);
        lock (_gate)
        {
            _openSteps[handle] = (id, _clock.Elapsed);
        }

        _observer?.StepStarted(id);
        return handle;
    }

    // Runs the function of a step with the step as the current one: the job callbacks carry it across every await
    // started inside, so each call made there belongs to the step, also in parallel branches.
    private JsValue StepRun(JsValue self, JsValue[] arguments)
    {
        var previous = _tracker.Current;
        _tracker.Current = arguments[0].AsString();
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
        CodeModeStepRecord? record = null;
        lock (_gate)
        {
            if (_openSteps.Remove(handle, out var open))
            {
                record = new CodeModeStepRecord(open.Id, open.StartedAt, _clock.Elapsed - open.StartedAt, succeeded) { ErrorMessage = message };
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

    // Called on the engine's thread while JavaScript runs: the run is busy, and the time between two close checks
    // is the script's own time.
    private void CheckActivity()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _lastCheck;
        _lastCheck = now;
        if (previous != 0 && now - previous < Stopwatch.Frequency / 200)
        {
            Interlocked.Add(ref _scriptTicks, now - previous);
        }

        Interlocked.Exchange(ref _lastActivity, now);
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

    // One per run: the engine runs one job at a time, so a plain field carries the current step.
    private sealed class StepTracker : JobCallbackHooks
    {
        public string? Current { get; set; }

        protected override object? Capture(Engine engine) => Current;

        protected override object? Enter(Engine engine, object hostDefined)
        {
            var previous = Current;
            Current = (string)hostDefined;
            return previous;
        }

        protected override void Exit(Engine engine, object? token) => Current = (string?)token;
    }

    private sealed class ClockTimeSystem(Func<DateTimeOffset> clock, TimeZoneInfo timeZone, CultureInfo culture) : DefaultTimeSystem(timeZone, culture)
    {
        public override DateTimeOffset GetUtcNow() => clock().ToUniversalTime();
    }
}
