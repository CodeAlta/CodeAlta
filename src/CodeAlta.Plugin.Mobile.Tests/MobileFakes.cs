using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>An <c>alta</c> that answers each command with the records a test gives, and remembers what it was asked.</summary>
internal sealed class FakeAlta : IPluginAltaService
{
    private readonly List<(string Prefix, Func<IReadOnlyList<string>, string?, Task<string>> Answer)> _answers = [];

    internal ConcurrentQueue<(string[] Args, string? Stdin)> Calls { get; } = new();

    /// <summary>Answers the commands that start with a prefix (such as <c>session list</c>) with records, one JSON object each.</summary>
    internal FakeAlta Answer(string prefix, params JsonObject[] records) => Answer(prefix, (_, _) => Transcript(0, records));

    /// <summary>Answers the commands that start with a prefix with a transcript of its own.</summary>
    internal FakeAlta Answer(string prefix, Func<IReadOnlyList<string>, string?, string> answer)
        => Answer(prefix, (args, stdin) => Task.FromResult(answer(args, stdin)));

    /// <summary>Answers the commands that start with a prefix when a task ends: a command that takes its time.</summary>
    internal FakeAlta Answer(string prefix, Func<IReadOnlyList<string>, string?, Task<string>> answer)
    {
        lock (_answers) _answers.Insert(0, (prefix, answer));
        return this;
    }

    /// <summary>The calls of one command, such as <c>session send</c>.</summary>
    internal string[][] CallsOf(string prefix) => [.. Calls.Select(static call => call.Args).Where(args => string.Join(' ', args).StartsWith(prefix, StringComparison.Ordinal))];

    internal static string Transcript(int exitCode, params JsonObject[] records)
    {
        var text = new StringBuilder();
        text.Append(new JsonObject { ["type"] = "alta.result", ["exitCode"] = exitCode }.ToJsonString()).Append('\n');
        foreach (var record in records) text.Append(record.ToJsonString()).Append('\n');
        return text.ToString();
    }

    public async ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(([.. args], stdin));
        var line = string.Join(' ', args);
        (string Prefix, Func<IReadOnlyList<string>, string?, Task<string>> Answer)[] answers;
        lock (_answers) answers = [.. _answers];
        foreach (var (prefix, answer) in answers)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var transcript = await answer(args, stdin).ConfigureAwait(false);
            var failed = transcript.Contains("\"alta.error\"", StringComparison.Ordinal);
            return new PluginAltaCommandResult { ExitCode = failed ? 1 : 0, TranscriptJsonl = transcript };
        }

        return new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = Transcript(0) };
    }

    internal static JsonObject Session(string id, string projectId, string spaceId, string lastActiveAt, string title = "A session", bool running = false, string state = "idle")
        => new()
        {
            ["type"] = "alta.session.item", ["sessionId"] = id, ["projectId"] = projectId, ["spaceId"] = spaceId, ["title"] = title, ["state"] = state,
            ["status"] = state == "archived" ? "archived" : "active", ["isRunning"] = running, ["lastActiveAt"] = lastActiveAt, ["latestSummary"] = "Summary of " + id,
        };

    internal static JsonObject Status(string id, string projectId, string spaceId, string state = "idle", bool running = false)
        => new()
        {
            ["type"] = "alta.session.status", ["sessionId"] = id, ["projectId"] = projectId, ["spaceId"] = spaceId, ["title"] = "Title of " + id, ["state"] = state,
            ["status"] = state == "archived" ? "archived" : "active", ["isRunning"] = running, ["lastActiveAt"] = "2026-10-24T10:00:00Z",
        };

    internal static JsonObject Project(string id, string name, string path, params string[] spaces)
        => new()
        {
            ["type"] = "alta.project.item", ["projectId"] = id, ["slug"] = name.ToLowerInvariant(), ["name"] = name, ["displayName"] = name, ["projectPath"] = path,
            ["archived"] = false, ["spaces"] = new JsonArray([.. spaces.Select(static space => (JsonNode)space)]),
        };
}

/// <summary>The spaces a test assigns the plugin to.</summary>
internal sealed class FakeSpaces(params string[] spaces) : IPluginSpaceService
{
    internal string[] Assigned { get; set; } = spaces;

    public IReadOnlyList<string> GetAssignedSpaces() => Assigned;
}

/// <summary>The files of a page a test gives.</summary>
internal sealed class FakeFiles : IPluginAppFileService
{
    internal Dictionary<string, PluginAppFile> Files { get; } = new(StringComparer.Ordinal);

    public bool HasFiles => true;

    internal FakeFiles Add(string path, string content, string contentType, bool immutable = false)
    {
        Files[path] = new PluginAppFile(path, Encoding.UTF8.GetBytes(content), contentType, immutable);
        return this;
    }

    public ValueTask<PluginAppFile?> ReadAsync(string path, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Files.GetValueOrDefault(path));
}

/// <summary>A Tailscale command line whose answers a test gives.</summary>
internal sealed class FakeTailscaleCli(bool installed = true) : IMobileTailscaleCli
{
    private readonly Dictionary<string, MobileCliResult> _answers = new(StringComparer.Ordinal);

    internal List<string> Calls { get; } = [];

    public bool IsInstalled => installed;

    internal FakeTailscaleCli Answer(string args, string output, int exitCode = 0, bool timedOut = false)
    {
        _answers[args] = new MobileCliResult(exitCode, output, timedOut);
        return this;
    }

    public Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var line = string.Join(' ', args);
        Calls.Add(line);
        return Task.FromResult(_answers.TryGetValue(line, out var answer) ? answer : new MobileCliResult(1, "unknown command: " + line));
    }

    internal const string Running = """{"BackendState":"Running","Self":{"DNSName":"desk.tail1234.ts.net."}}""";
}

internal static class FreePort
{
    internal static int Next()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>A clock a test moves: the timers it made fire when their time comes.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Completes when a timer is made: the code under test waits from then.</summary>
    internal TaskCompletionSource TimerCreated { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer;
        lock (_gate)
        {
            timer = new ManualTimer(this, callback, state, dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + dueTime);
            _timers.Add(timer);
        }

        TimerCreated.TrySetResult();
        return timer;
    }

    /// <summary>Moves the clock, and fires the timers whose time came.</summary>
    internal void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.Due <= _now)];
            foreach (var timer in due) _timers.Remove(timer);
        }

        foreach (var timer in due) timer.Fire();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        internal DateTimeOffset Due { get; private set; } = due;

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
