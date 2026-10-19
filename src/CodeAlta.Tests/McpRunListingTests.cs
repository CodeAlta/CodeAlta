using System.Diagnostics;
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
    public async Task TheRunsOfASession_ListTheirToolsOnItsConnections_SoTheServerStartsOncePerSession()
    {
        using var project = TempDirectory.Create();
        var starts = Path.Combine(project.Path, "starts");
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_START_DIR"] = starts });
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        try
        {
            await ActivateTinyAsync(plugin, project.Path, "one");
            // The activation lists the tools on a connection of its own, which ends with it.
            Assert.AreEqual(1, StartedProcesses(starts).Count);

            const int Turns = 5;
            for (var turn = 1; turn <= Turns; turn++)
            {
                var tools = await RunToolsAsync(plugin, project.Path, "one");
                Assert.AreEqual($"echo:turn {turn}", await EchoAsync(tools, "one", $"turn {turn}"));
            }

            // Each run used to start the server once more only to list its tools, and the first call a third time:
            // 1 + Turns + 1 starts. The runs now list the tools on the session's own server, which its calls use.
            Assert.AreEqual(2, StartedProcesses(starts).Count, "One start for the activation, one for the session, whatever the number of runs.");

            // Two sessions that start their runs at once each get a server of their own.
            await Task.WhenAll(ActivateTinyAsync(plugin, project.Path, "two"), ActivateTinyAsync(plugin, project.Path, "three"));
            var runs = await Task.WhenAll(RunToolsAsync(plugin, project.Path, "two"), RunToolsAsync(plugin, project.Path, "three"));
            Assert.AreEqual("echo:two", await EchoAsync(runs[0], "two", "two"));
            Assert.AreEqual("echo:three", await EchoAsync(runs[1], "three", "three"));
            Assert.AreEqual(6, StartedProcesses(starts).Count);
        }
        finally
        {
            await plugin.DisposeAsync();
        }

        // The servers of the sessions end with the plugin: none is left running.
        AssertExited(StartedProcesses(starts));
    }

    [TestMethod]
    public async Task ARunOnAKeptConnection_GetsTheToolsThePolicyGivesNow_AndAServerTurnedOffIsClosed()
    {
        using var project = TempDirectory.Create();
        var starts = Path.Combine(project.Path, "starts");
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_START_DIR"] = starts });
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        try
        {
            await ActivateTinyAsync(plugin, project.Path, "session");
            var activation = StartedProcesses(starts);
            var first = Names(await RunToolsAsync(plugin, project.Path, "session"));
            CollectionAssert.IsSubsetOf(new[] { "mcp__tiny__echo", "mcp__tiny__secret" }, first);
            var session = StartedProcesses(starts).Except(activation).Single();

            // A tool the policy turns off is no longer offered, as a new start would list it: the kept server answers.
            WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\ndisabled_tools = [\"secret\"]\n");
            var second = Names(await RunToolsAsync(plugin, project.Path, "session"));
            CollectionAssert.Contains(second, "mcp__tiny__echo");
            CollectionAssert.DoesNotContain(second, "mcp__tiny__secret");
            Assert.HasCount(2, StartedProcesses(starts));

            // A server the policy turns off is not offered, the run says so as before, and its process ends.
            WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\nenabled = false\n");
            var disabled = await plugin.OnBeforeAgentRunAsync(RunContext(project.Path, "session"), CancellationToken.None);
            Assert.IsNotNull(disabled);
            Assert.IsEmpty(disabled.AdditionalTools);
            var diagnostics = await disabled.TemporaryPromptContributions.Single().Content(CreatePromptContext(project.Path, "session"), CancellationToken.None);
            StringAssert.Contains(diagnostics, "MCP server 'tiny' is disabled by policy.");
            AssertExited([session]);

            // Turned on again, the next run starts it again, and its calls use it.
            WriteProjectPolicy(project.Path, "[plugins.mcp]\nenabled = true\n");
            var again = await RunToolsAsync(plugin, project.Path, "session");
            Assert.AreEqual("echo:again", await EchoAsync(again, "session", "again"));
            Assert.HasCount(3, StartedProcesses(starts));
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AServerThatExited_IsStartedAgainByTheNextRun_SoItsFirstCallWorks()
    {
        using var project = TempDirectory.Create();
        var starts = Path.Combine(project.Path, "starts");
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_START_DIR"] = starts });
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        try
        {
            await ActivateTinyAsync(plugin, project.Path, "session");
            var activation = StartedProcesses(starts);
            Assert.AreEqual("echo:before", await EchoAsync(await RunToolsAsync(plugin, project.Path, "session"), "session", "before"));
            var session = StartedProcesses(starts).Except(activation).Single();

            using (var process = Process.GetProcessById(session))
            {
                process.Kill(entireProcessTree: true);
                Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(30)));
            }

            // The connection sees its process end; until it does, a run is offered the tools it listed, and starts nothing.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            var tools = await RunToolsAsync(plugin, project.Path, "session");
            while (StartedProcesses(starts).Count < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                tools = await RunToolsAsync(plugin, project.Path, "session");
            }

            Assert.HasCount(3, StartedProcesses(starts), "The run starts the server again.");
            Assert.AreEqual("echo:after", await EchoAsync(tools, "session", "after"), "The first call of the run uses the new server.");
            Assert.HasCount(3, StartedProcesses(starts));
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SessionConnections_ListingKeepsTheServerForTheCalls_TriesAFailedStartAgain_AndCountsAsAUse()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        var variable = "CODEALTA_TEST_MCP_LATE_" + Guid.NewGuid().ToString("N");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "${" + variable + "}" });
        var time = new ManualTime();
        await using var connections = new McpSessionConnections(TimeSpan.FromMinutes(15), time);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path, UserHomeDirectory = _home.Path };
        int Lists() => File.Exists(log) ? File.ReadAllLines(log).Count(static line => line == "LIST") : 0;
        try
        {
            // The variable is not set: the server is not started, and the listing says why.
            var failed = await connections.ListToolsForServersAsync("session", request, ["tiny"], CancellationToken.None);
            Assert.IsEmpty(failed.Tools);
            Assert.AreEqual("environment_variable_not_found", failed.Diagnostics.Single().Code);
            Assert.AreEqual(0, Lists());

            // Set now: the next listing tries again, as a new start would, instead of answering the failure it had.
            Environment.SetEnvironmentVariable(variable, "late");
            var listed = await connections.ListToolsForServersAsync("session", request, ["tiny"], CancellationToken.None);
            Assert.IsEmpty(listed.Diagnostics);
            Assert.IsTrue(listed.Tools.Any(static tool => tool.Name == "late"));
            Assert.AreEqual(1, Lists());

            // The calls use the server the listing connected, and another listing answers from it.
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = "hi" };
            Assert.IsNotNull(await connections.CallToolAsync("session", request, "tiny", "echo", arguments, new List<McpRuntimeDiagnostic>(), CancellationToken.None));
            Assert.IsTrue((await connections.ListToolsForServersAsync("session", request, ["tiny"], CancellationToken.None)).Tools.Any(static tool => tool.Name == "echo"));
            Assert.AreEqual(1, Lists());

            // A listing is a use: the connections stay while the session lists, and close once idle.
            time.Advance(TimeSpan.FromMinutes(14));
            await connections.ListToolsForServersAsync("session", request, ["tiny"], CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(14));
            await connections.CloseIdleAsync();
            Assert.AreEqual(1, connections.Count);
            time.Advance(TimeSpan.FromMinutes(2));
            await connections.CloseIdleAsync();
            Assert.AreEqual(0, connections.Count);
            Assert.IsTrue((await connections.ListToolsForServersAsync("session", request, ["tiny"], CancellationToken.None)).Tools.Any(static tool => tool.Name == "echo"));
            Assert.AreEqual(2, Lists());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task ARunWithoutASession_ListsItsToolsOnAConnectionOfItsOwn()
    {
        using var project = TempDirectory.Create();
        var starts = Path.Combine(project.Path, "starts");
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_START_DIR"] = starts });
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        try
        {
            await ActivateTinyAsync(plugin, project.Path, sessionId: null);
            var before = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
            }, CancellationToken.None);
            Assert.IsTrue(before!.AdditionalTools.Any(static tool => tool.Spec.Name == "mcp__tiny__echo"));
            // Nothing keeps it: the listing's server ended with the listing, as the activation's did.
            var processes = StartedProcesses(starts);
            Assert.HasCount(2, processes);
            AssertExited(processes);
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    private static async Task ActivateTinyAsync(McpPlugin plugin, string projectPath, string? sessionId)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(CreateAltaContext(stdout, stderr, projectPath, sessionId)) };
        Assert.AreEqual(0, await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stdout + stderr.ToString());
    }

    private static PluginBeforeAgentRunContext RunContext(string projectPath, string sessionId) => new()
    {
        Plugin = CreatePluginDescriptor(),
        Services = NoopPluginServices.Create(),
        ProjectPath = projectPath,
        SessionId = sessionId,
    };

    private static PluginSystemPromptContext CreatePromptContext(string projectPath, string? sessionId = null) => new()
    {
        Plugin = CreatePluginDescriptor(),
        Services = NoopPluginServices.Create(),
        ProjectPath = projectPath,
        SessionId = sessionId,
    };

    private static async Task<IReadOnlyList<AgentToolDefinition>> RunToolsAsync(McpPlugin plugin, string projectPath, string sessionId)
        => (await plugin.OnBeforeAgentRunAsync(RunContext(projectPath, sessionId), CancellationToken.None))?.AdditionalTools ?? [];

    private static string[] Names(IReadOnlyList<AgentToolDefinition> tools) => [.. tools.Select(static tool => tool.Spec.Name)];

    private static async Task<string> EchoAsync(IReadOnlyList<AgentToolDefinition> tools, string sessionId, string text)
    {
        var echo = tools.Single(static tool => tool.Spec.Name == "mcp__tiny__echo");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { text }));
        var result = await echo.Handler(new AgentToolInvocation(new ModelProviderId("test"), sessionId, "call", echo.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);
        return Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value;
    }

    // The processes the tiny server started as, one file each (MCP_TEST_START_DIR).
    private static IReadOnlyList<int> StartedProcesses(string directory)
        => Directory.Exists(directory)
            ? [.. Directory.GetFiles(directory, "*.start").Select(static file => int.Parse(Path.GetFileName(file).Split('-')[0], CultureInfo.InvariantCulture))]
            : [];

    private static void AssertExited(IEnumerable<int> processes)
    {
        foreach (var id in processes)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(30)), $"The MCP server process {id} is still running.");
            }
        }
    }
}
