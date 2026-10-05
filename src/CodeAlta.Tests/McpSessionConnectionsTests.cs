using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task DirectToolCalls_OfASession_KeepTheirServerConnected()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path, "one")) };
        Assert.AreEqual(0, await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stderr.ToString());
        var before = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = project.Path, SessionId = "one",
        });
        var echo = before!.AdditionalTools.Single(tool => tool.Spec.Name == "mcp__tiny__echo");
        // The activation and the start of the run each listed the tools on a connection of their own.
        var listedBefore = Starts(log);

        async Task<string> EchoAsync(string session, string text)
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { text }));
            var result = await echo.Handler(new AgentToolInvocation(new ModelProviderId("test"), session, "call", echo.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);
            return Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        }

        Assert.AreEqual("echo:first", await EchoAsync("one", "first"));
        Assert.AreEqual("echo:second", await EchoAsync("one", "second"));
        Assert.AreEqual("echo:third", await EchoAsync("one", "third"));
        // A server lists its tools once when it is connected: three calls, one connection.
        Assert.AreEqual(listedBefore + 1, Starts(log), "The calls of a session share one server.");
        Assert.AreEqual(3, File.ReadAllLines(log).Count(line => line == "CALL echo"));

        // Another session has a server of its own: the state of one is not the state of the other.
        Assert.AreEqual("echo:other", await EchoAsync("two", "other"));
        Assert.AreEqual(listedBefore + 2, Starts(log));

        // The servers end with the plugin, and a later call says so instead of starting one.
        await plugin.DisposeAsync();
        StringAssert.Contains(await EchoAsync("one", "late"), "the MCP plugin was stopped");
        Assert.AreEqual(listedBefore + 2, Starts(log));

        static int Starts(string path) => File.ReadAllLines(path).Count(line => line == "LIST");
    }

    [TestMethod]
    public async Task SessionConnections_CloseWhenIdle_AndReconnectAfterAFailedCall()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log);
        var time = new ManualTime();
        await using var connections = new McpSessionConnections(TimeSpan.FromMinutes(15), time);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path, UserHomeDirectory = _home.Path };
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = "hi" };

        async Task<McpRuntimeToolCallResult?> CallAsync(string tool)
            => await connections.CallToolAsync("session", request, "tiny", tool, arguments, new List<McpRuntimeDiagnostic>(), CancellationToken.None);
        int Starts() => File.ReadAllLines(log).Count(line => line == "LIST");

        Assert.IsNotNull(await CallAsync("echo"));
        Assert.IsNotNull(await CallAsync("echo"));
        Assert.AreEqual(1, Starts());
        Assert.AreEqual(1, connections.Count);

        // Used a moment ago: it stays.
        time.Advance(TimeSpan.FromMinutes(14));
        await connections.CloseIdleAsync();
        Assert.AreEqual(1, connections.Count);
        Assert.IsNotNull(await CallAsync("echo"));
        Assert.AreEqual(1, Starts());

        // Unused for the idle lifetime: the server is closed, and the next call starts it again.
        time.Advance(TimeSpan.FromMinutes(16));
        await connections.CloseIdleAsync();
        Assert.AreEqual(0, connections.Count);
        Assert.IsNotNull(await CallAsync("echo"));
        Assert.AreEqual(2, Starts());

        // A call that could not be made drops what was connected: the one after connects again.
        Assert.IsNull(await CallAsync("no-such-tool"));
        Assert.IsNotNull(await CallAsync("echo"));
        Assert.AreEqual(3, Starts());
    }

    // A clock the test moves; the periodic timer is the test's own call of CloseIdleAsync.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
