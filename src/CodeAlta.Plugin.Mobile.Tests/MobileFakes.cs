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
    private readonly List<(string Prefix, Func<IReadOnlyList<string>, string?, string> Answer)> _answers = [];

    internal ConcurrentQueue<(string[] Args, string? Stdin)> Calls { get; } = new();

    /// <summary>Answers the commands that start with a prefix (such as <c>session list</c>) with records, one JSON object each.</summary>
    internal FakeAlta Answer(string prefix, params JsonObject[] records) => Answer(prefix, (_, _) => Transcript(0, records));

    /// <summary>Answers the commands that start with a prefix with a transcript of its own.</summary>
    internal FakeAlta Answer(string prefix, Func<IReadOnlyList<string>, string?, string> answer)
    {
        _answers.Insert(0, (prefix, answer));
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

    public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(([.. args], stdin));
        var line = string.Join(' ', args);
        foreach (var (prefix, answer) in _answers)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var transcript = answer(args, stdin);
            var failed = transcript.Contains("\"alta.error\"", StringComparison.Ordinal);
            return ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = failed ? 1 : 0, TranscriptJsonl = transcript });
        }

        return ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = Transcript(0) });
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
