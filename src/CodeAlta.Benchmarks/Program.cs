using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Benchmarks;

// Streams through the real runtime of a headless host (sessions, journals, the SQLite cache, the display
// projection and plugin observers) for many sessions at once, and reports what it costs: CPU time, allocations,
// collections, lock contention and thread-pool work. See doc/runtime.md, "Measuring the runtime".
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        BenchmarkOptions options;
        try
        {
            options = BenchmarkOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(BenchmarkOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return 0;
        }

        var results = new List<BenchmarkResult>();
        for (var repeat = 0; repeat < options.Repeat; repeat++)
        {
            results.Add(await RunOnceAsync(options, repeat).ConfigureAwait(false));
        }

        var report = BenchmarkReport.Create(options, results);
        if (options.JsonPath is { } jsonPath)
        {
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.BenchmarkReport)).ConfigureAwait(false);
        }

        Console.WriteLine(report.ToText());
        return report.Results.All(static result => result.Completed) ? 0 : 1;
    }

    private static async Task<BenchmarkResult> RunOnceAsync(BenchmarkOptions options, int repeat)
    {
        var root = Path.Combine(LinkFreeTempPath(), $"CodeAlta.Bench.{Guid.NewGuid():N}");
        var globalRoot = Path.Combine(root, "global");
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        try
        {
            var workload = new StreamingWorkload
            {
                StepsPerTurn = options.Steps,
                DeltasPerStep = options.Deltas,
                CharactersPerDelta = options.DeltaCharacters,
                ToolOutputLines = options.ToolLines,
                DeltasPerSecond = options.Rate,
                ScriptCalls = options.ScriptCalls,
            };
            var counters = new PluginCounters();
            var providerId = new ModelProviderId("bench");
            await using var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = globalRoot,
                    CurrentProjectPath = projectRoot,
                    IsHeadless = true,
                    HasInteractiveUi = false,
                    StartPlugins = options.QuietPlugins + options.ObservingPlugins > 0,
                    PluginBuiltIns = BenchPlugins.Create(options.QuietPlugins, options.ObservingPlugins, counters),
                    ConfigureModelProviders = registry =>
                    {
                        var descriptor = new ModelProviderDescriptor(providerId, "Bench") { DefaultModelId = "bench-model" };
                        registry.RegisterOrReplace(descriptor, () => new BenchProviderRuntime(descriptor, workload));
                    },
                },
                CancellationToken.None).ConfigureAwait(false);

            // Code Mode: the runs of the benchmark, without a limit that would refuse one; their results are not delivered.
            await using var scripts = new CodeAlta.Orchestration.Runtime.Scripts.CodeModeRunRegistry(null, maxPerSession: 64, maxPerHost: 100_000);
            IReadOnlyList<AgentToolDefinition> codeModeTools = [];
            if (options.ScriptCalls > 0)
            {
                var registry = new AltaCommandRegistry();
                var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(host.CatalogOptions).Add(registry));
                codeModeTools = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { WorkingDirectory = projectRoot },
                    new CodeModeToolOptions { Runs = scripts, Cache = new CodeModeScriptCache() });
            }

            var executionOptions = new SessionExecutionOptions
            {
                ProviderId = providerId,
                ProviderKey = providerId.Value,
                WorkingDirectory = projectRoot,
                ProjectRoots = [projectRoot],
                Tools = [workload.CreateTool(), .. codeModeTools],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowForSession)),
            };

            var sessions = new List<SessionViewDescriptor>();
            for (var index = 0; index < options.Sessions; index++)
            {
                sessions.Add(await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, executionOptions,
                    $"Bench session {index}", CancellationToken.None).ConfigureAwait(false));
            }

            using var stop = new CancellationTokenSource();
            var idle = new IdleWatcher(host.RuntimeService);
            var watcher = idle.RunAsync(stop.Token);
            var observers = new List<DisplayObserver>();
            for (var index = 0; index < Math.Min(options.Observers, sessions.Count); index++)
            {
                observers.Add(DisplayObserver.Start(host.RuntimeService.Display, sessions[index].SessionId, stop.Token));
            }

            var streamReader = options.StreamReader ? RuntimeStreamReader.Start(host.RuntimeService, stop.Token) : null;

            // One turn of every session first: what the JIT, the journal files and the database set up is not measured.
            await RunTurnsAsync(host.RuntimeService, sessions, executionOptions, workload, idle, turns: 1, options.Timeout).ConfigureAwait(false);
            await WaitForScriptsAsync(scripts, options.Timeout).ConfigureAwait(false);
            var scriptsBefore = scripts.List().Count;
            var toolCallsBefore = Interlocked.Read(ref workload.ToolCalls);

            var before = MetricsSnapshot.Capture();
            var sampler = ThreadPoolSampler.Start(stop.Token);
            var deltasBefore = Interlocked.Read(ref workload.DeltasStreamed);
            var toolLinesBefore = Interlocked.Read(ref workload.ToolOutputLinesStreamed);
            var pluginEventsBefore = Interlocked.Read(ref counters.Events);
            var pluginDeltasBefore = Interlocked.Read(ref counters.Deltas);
            var stopwatch = Stopwatch.StartNew();
            var completed = await RunTurnsAsync(host.RuntimeService, sessions, executionOptions, workload, idle, options.Turns, options.Timeout).ConfigureAwait(false)
                && await WaitForScriptsAsync(scripts, options.Timeout).ConfigureAwait(false);
            stopwatch.Stop();
            var after = MetricsSnapshot.Capture();
            var threadPool = await sampler.StopAsync().ConfigureAwait(false);
            var retainedBytes = GC.GetTotalMemory(forceFullCollection: true);

            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(observers.Select(static observer => observer.Completion)).ConfigureAwait(false);
            if (streamReader is not null) await streamReader.Completion.ConfigureAwait(false);
            await watcher.ConfigureAwait(false);

            return new BenchmarkResult
            {
                Repeat = repeat,
                Completed = completed,
                WallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                CpuMilliseconds = (after.CpuTime - before.CpuTime).TotalMilliseconds,
                AllocatedBytes = after.AllocatedBytes - before.AllocatedBytes,
                Gen0Collections = after.Gen0 - before.Gen0,
                Gen1Collections = after.Gen1 - before.Gen1,
                Gen2Collections = after.Gen2 - before.Gen2,
                GcPauseMilliseconds = (after.GcPause - before.GcPause).TotalMilliseconds,
                LockContentions = after.LockContentions - before.LockContentions,
                ThreadPoolWorkItems = after.CompletedWorkItems - before.CompletedWorkItems,
                MaxThreadPoolThreads = threadPool.MaxThreads,
                MaxPendingWorkItems = threadPool.MaxPending,
                RetainedManagedBytes = retainedBytes,
                Deltas = Interlocked.Read(ref workload.DeltasStreamed) - deltasBefore,
                ToolOutputLines = Interlocked.Read(ref workload.ToolOutputLinesStreamed) - toolLinesBefore,
                PluginEvents = Interlocked.Read(ref counters.Events) - pluginEventsBefore,
                PluginDeltas = Interlocked.Read(ref counters.Deltas) - pluginDeltasBefore,
                DisplayReplacements = observers.Sum(static observer => observer.Replacements),
                RuntimeStreamEvents = streamReader?.Events ?? 0,
                DroppedRuntimeEvents = host.RuntimeService.DroppedRuntimeEventCount,
                Scripts = scripts.List().Count - scriptsBefore,
                ToolCalls = Interlocked.Read(ref workload.ToolCalls) - toolCallsBefore,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<bool> RunTurnsAsync(SessionRuntimeService runtime, IReadOnlyList<SessionViewDescriptor> sessions,
        SessionExecutionOptions executionOptions, StreamingWorkload workload, IdleWatcher idle, int turns, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await Task.WhenAll(sessions.Select(session => Task.Run(async () =>
            {
                for (var turn = 0; turn < turns; turn++)
                {
                    var finalStep = workload.ExpectFinalStep(session.SessionId);
                    await runtime.SendAsync(session, executionOptions,
                        new AgentSendOptions { Input = AgentInput.Text($"Benchmark turn {turn}: stream, call the tool, answer.") },
                        deadline.Token).ConfigureAwait(false);
                    await finalStep.WaitAsync(deadline.Token).ConfigureAwait(false);
                    await idle.WaitIdleAsync(session.SessionId, deadline.Token).ConfigureAwait(false);
                }
            }))).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Timed out after {timeout.TotalSeconds:0} s.");
            foreach (var overview in runtime.ListOverview().Where(static overview => overview.Running))
            {
                Console.Error.WriteLine($"  still running: {overview.SessionId}");
                try
                {
                    var history = await runtime.GetHistoryAsync(overview.SessionId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Console.Error.WriteLine("    last events: " + string.Join(", ", history.TakeLast(6).Select(static e => e is AgentSessionUpdateEvent update ? $"update:{update.Kind}" : e.GetType().Name)));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"    history unavailable: {exception.GetType().Name}: {exception.Message}");
                }
            }

            return false;
        }
    }

    // The scripts of the turns go on in the background: the measure ends when the last one ended.
    private static async Task<bool> WaitForScriptsAsync(CodeAlta.Orchestration.Runtime.Scripts.CodeModeRunRegistry scripts, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (scripts.List().Any(static run => run.Running))
        {
            if (deadline.Elapsed > timeout)
            {
                Console.Error.WriteLine($"Scripts still running after {timeout.TotalSeconds:0} s.");
                return false;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return true;
    }

    private static string LinkFreeTempPath()
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (OperatingSystem.IsWindows()) return path;
        // CodeAlta does not keep sessions below a linked folder, and the temporary folder of macOS is below /var.
        var resolved = Path.GetPathRoot(path)!;
        foreach (var name in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, name);
            if (new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target) resolved = target.FullName;
        }

        return resolved;
    }
}

/// <summary>Tells when a session no longer runs, from the runtime's lock-free overview read a few times a second.</summary>
internal sealed class IdleWatcher(SessionRuntimeService runtime)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<TaskCompletionSource>> _waiters = new(StringComparer.OrdinalIgnoreCase);

    public Task WaitIdleAsync(string sessionId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_waiters.TryGetValue(sessionId, out var list)) _waiters[sessionId] = list = [];
            list.Add(completion);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            List<TaskCompletionSource>? ready = null;
            lock (_gate)
            {
                if (_waiters.Count == 0) continue;
            }

            var running = runtime.ListOverview().Where(static overview => overview.Running)
                .Select(static overview => overview.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var sessionId in _waiters.Keys.ToArray())
                {
                    if (running.Contains(sessionId)) continue;
                    (ready ??= []).AddRange(_waiters[sessionId]);
                    _waiters.Remove(sessionId);
                }
            }

            if (ready is not null)
            {
                foreach (var completion in ready) completion.TrySetResult();
            }
        }
    }
}

/// <summary>Looks at one session of the display projection as a visible pane of the desktop application does.</summary>
internal sealed class DisplayObserver
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(33);
    private long _replacements;

    private DisplayObserver() { }

    public long Replacements => Interlocked.Read(ref _replacements);

    public Task Completion { get; private set; } = Task.CompletedTask;

    public static DisplayObserver Start(RuntimeDisplayProjection display, string sessionId, CancellationToken cancellationToken)
    {
        var observer = new DisplayObserver();
        observer.Completion = Task.Run(() => observer.RunAsync(display, sessionId, cancellationToken));
        return observer;
    }

    private async Task RunAsync(RuntimeDisplayProjection display, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            long? sentSession = null;
            var sentAt = 0L;
            var first = true;
            await foreach (var replacement in display.ObserveAsync(cancellationToken).ConfigureAwait(false))
            {
                // What the desktop display service does with each replacement: find the session, skip what did not change.
                long? revision = null;
                foreach (var session in replacement.Snapshot.Sessions)
                {
                    if (string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        revision = session.Revision;
                        break;
                    }
                }

                if (!first && revision == sentSession) continue;
                first = false;
                sentSession = revision;
                Interlocked.Increment(ref _replacements);
                var wait = MinimumInterval - Stopwatch.GetElapsedTime(sentAt);
                sentAt = Stopwatch.GetTimestamp();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Reads the original runtime event stream, as the terminal application does.</summary>
internal sealed class RuntimeStreamReader
{
    private long _events;

    private RuntimeStreamReader() { }

    public long Events => Interlocked.Read(ref _events);

    public Task Completion { get; private set; } = Task.CompletedTask;

    public static RuntimeStreamReader Start(SessionRuntimeService runtime, CancellationToken cancellationToken)
    {
        var reader = new RuntimeStreamReader();
        reader.Completion = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in runtime.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref reader._events);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        });
        return reader;
    }
}

internal readonly record struct MetricsSnapshot(TimeSpan CpuTime, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
    TimeSpan GcPause, long LockContentions, long CompletedWorkItems)
{
    public static MetricsSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.TotalProcessorTime, GC.GetTotalAllocatedBytes(precise: true), GC.CollectionCount(0), GC.CollectionCount(1),
            GC.CollectionCount(2), GC.GetTotalPauseDuration(), Monitor.LockContentionCount, ThreadPool.CompletedWorkItemCount);
    }
}

/// <summary>Samples how many threads the pool has and how much work waits for one.</summary>
internal sealed class ThreadPoolSampler
{
    private int _maxThreads;
    private long _maxPending;
    private Task _completion = Task.CompletedTask;
    private CancellationTokenSource? _stop;

    public static ThreadPoolSampler Start(CancellationToken cancellationToken)
    {
        var sampler = new ThreadPoolSampler { _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) };
        var token = sampler._stop.Token;
        sampler._completion = Task.Factory.StartNew(() =>
        {
            while (!token.IsCancellationRequested)
            {
                sampler._maxThreads = Math.Max(sampler._maxThreads, ThreadPool.ThreadCount);
                sampler._maxPending = Math.Max(sampler._maxPending, ThreadPool.PendingWorkItemCount);
                Thread.Sleep(5);
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return sampler;
    }

    public async Task<(int MaxThreads, long MaxPending)> StopAsync()
    {
        await _stop!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        return (_maxThreads, _maxPending);
    }
}

internal sealed class BenchmarkOptions
{
    public const string Usage = """
        Usage: CodeAlta.Benchmarks [options]
          --sessions N          sessions that run at the same time (default 10)
          --turns N             measured turns of each session (default 3)
          --steps N             model requests of a turn; all but the last call the tool (default 3)
          --deltas N            text fragments streamed by each model request (default 200)
          --delta-chars N       characters of a fragment (default 6)
          --tool-lines N        lines of output streamed by each tool call (default 50)
          --rate N              fragments a session streams each second; 0 streams as fast as possible (default 0)
          --observers N         sessions watched by a display observer, as visible panes (default 2)
          --quiet-plugins N     plugins that do not look at events (default 5)
          --observing-plugins N plugins that look at events (default 2)
          --stream-reader       read the original runtime event stream, as the terminal application does
          --script-calls N      Code Mode: each step that calls the tool runs a script instead, in the background,
                                which calls it N times at once (default 0: the tool is called directly)
          --repeat N            runs, each in a new host (default 1)
          --timeout SECONDS     time allowed for the measured turns (default 600)
          --json PATH           also write the results as JSON
        """;

    public int Sessions { get; private set; } = 10;
    public int Turns { get; private set; } = 3;
    public int Steps { get; private set; } = 3;
    public int Deltas { get; private set; } = 200;
    public int DeltaCharacters { get; private set; } = 6;
    public int ToolLines { get; private set; } = 50;
    public int Rate { get; private set; }
    public int Observers { get; private set; } = 2;
    public int QuietPlugins { get; private set; } = 5;
    public int ObservingPlugins { get; private set; } = 2;
    public bool StreamReader { get; private set; }
    public int ScriptCalls { get; private set; }
    public int Repeat { get; private set; } = 1;
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(600);
    public string? JsonPath { get; private set; }
    public bool Help { get; private set; }

    public static BenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        var options = new BenchmarkOptions();
        var index = 0;
        for (; index < args.Count; index++)
        {
            var name = args[index];
            switch (name)
            {
                case "-h" or "--help": options.Help = true; break;
                case "--stream-reader": options.StreamReader = true; break;
                case "--sessions": options.Sessions = Positive(); break;
                case "--turns": options.Turns = Positive(); break;
                case "--steps": options.Steps = Positive(); break;
                case "--deltas": options.Deltas = Positive(); break;
                case "--delta-chars": options.DeltaCharacters = Positive(); break;
                case "--tool-lines": options.ToolLines = NonNegative(); break;
                case "--rate": options.Rate = NonNegative(); break;
                case "--observers": options.Observers = NonNegative(); break;
                case "--script-calls": options.ScriptCalls = NonNegative(); break;
                case "--quiet-plugins": options.QuietPlugins = NonNegative(); break;
                case "--observing-plugins": options.ObservingPlugins = NonNegative(); break;
                case "--repeat": options.Repeat = Positive(); break;
                case "--timeout": options.Timeout = TimeSpan.FromSeconds(Positive()); break;
                case "--json": options.JsonPath = Value(); break;
                default: throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        return options;

        string Value()
        {
            if (++index >= args.Count) throw new ArgumentException($"Option '{args[index - 1]}' needs a value.");
            return args[index];
        }

        int NonNegative()
        {
            var text = Value();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException($"'{text}' is not a number.");
            return value;
        }

        int Positive()
        {
            var value = NonNegative();
            if (value == 0) throw new ArgumentException($"Option '{args[index - 1]}' must be greater than zero.");
            return value;
        }
    }

    public string Describe()
        => string.Create(CultureInfo.InvariantCulture,
            $"sessions={Sessions} turns={Turns} steps={Steps} deltas={Deltas} delta-chars={DeltaCharacters} tool-lines={ToolLines} rate={(Rate == 0 ? "max" : Rate.ToString(CultureInfo.InvariantCulture))} observers={Observers} plugins={QuietPlugins}+{ObservingPlugins} stream-reader={StreamReader} script-calls={ScriptCalls}");
}

internal sealed class BenchmarkResult
{
    public int Repeat { get; init; }
    public bool Completed { get; init; }
    public double WallMilliseconds { get; init; }
    public double CpuMilliseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double GcPauseMilliseconds { get; init; }
    public long LockContentions { get; init; }
    public long ThreadPoolWorkItems { get; init; }
    public int MaxThreadPoolThreads { get; init; }
    public long MaxPendingWorkItems { get; init; }
    public long RetainedManagedBytes { get; init; }
    public long Deltas { get; init; }
    public long ToolOutputLines { get; init; }
    public long PluginEvents { get; init; }
    public long PluginDeltas { get; init; }
    public long DisplayReplacements { get; init; }
    public long RuntimeStreamEvents { get; init; }
    public long DroppedRuntimeEvents { get; init; }
    public long Scripts { get; init; }
    public long ToolCalls { get; init; }
}

internal sealed class BenchmarkReport
{
    public string Machine { get; init; } = string.Empty;
    public string Options { get; init; } = string.Empty;
    public List<BenchmarkResult> Results { get; init; } = [];

    public static BenchmarkReport Create(BenchmarkOptions options, List<BenchmarkResult> results)
        => new()
        {
            Machine = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} cores, .NET {Environment.Version}",
            Options = options.Describe(),
            Results = results,
        };

    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {Machine}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {Options}");
        foreach (var result in Results)
        {
            var events = Math.Max(1, result.Deltas + result.ToolOutputLines);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"run {result.Repeat}: {(result.Completed ? "completed" : "TIMED OUT")}, wall {result.WallMilliseconds:0} ms, cpu {result.CpuMilliseconds:0} ms ({result.CpuMilliseconds / Math.Max(1, result.WallMilliseconds) * 100:0}% of one core)");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  streamed {result.Deltas} fragments + {result.ToolOutputLines} tool lines: {events / Math.Max(0.001, result.WallMilliseconds / 1000):0}/s, cpu {result.CpuMilliseconds * 1000 / events:0.0} us and {result.AllocatedBytes / (double)events / 1024:0.00} KB allocated each");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  allocated {result.AllocatedBytes / 1024d / 1024:0.0} MB, GC gen0/1/2 {result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections}, pause {result.GcPauseMilliseconds:0} ms, retained heap {result.RetainedManagedBytes / 1024d / 1024:0.0} MB");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  lock contentions {result.LockContentions}, thread-pool work items {result.ThreadPoolWorkItems} ({result.ThreadPoolWorkItems / (double)events:0.0} per streamed item), max pool threads {result.MaxThreadPoolThreads}, max queued {result.MaxPendingWorkItems}");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  plugin events {result.PluginEvents} (fragments {result.PluginDeltas}), display replacements {result.DisplayReplacements}, runtime stream events {result.RuntimeStreamEvents}, dropped {result.DroppedRuntimeEvents}");
            if (result.Scripts > 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"  scripts {result.Scripts}, tool calls {result.ToolCalls} ({result.ToolCalls / (double)result.Scripts:0.0} each), {result.Scripts / Math.Max(0.001, result.WallMilliseconds / 1000):0} scripts/s, cpu {result.CpuMilliseconds / result.Scripts:0.00} ms and {result.AllocatedBytes / (double)result.Scripts / 1024:0} KB allocated per script");
            }
        }

        return builder.ToString();
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(BenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
