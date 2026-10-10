using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The services of a plugin command in the tests: a real database and an <c>alta</c> that answers with the projects and the spaces.</summary>
internal sealed class TestServices(IPluginDatabase database, IPluginAltaService? alta = null) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();

    public XenoAtom.Logging.Logger Logger => _inner.Logger;

    public IPluginUiService Ui => _inner.Ui;

    public IPluginStateStore State => _inner.State;

    public IPluginDatabase Database { get; } = database;

    public IPluginWorkspaceService Workspace => _inner.Workspace;

    public IPluginSessionService Sessions => _inner.Sessions;

    public IPluginPromptService Prompts => _inner.Prompts;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = alta ?? _noAlta;

    private static readonly NoopPluginAltaService _noAlta = new();
}

/// <summary>An <c>alta</c> that knows two projects and two spaces.</summary>
internal sealed class FakeAlta : IPluginAltaService
{
    public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = string.Join(' ', args);
        var transcript = text.StartsWith("project list", StringComparison.Ordinal)
            ? "{\"type\":\"alta.project.item\",\"projectId\":\"project-0\",\"slug\":\"alpha\",\"displayName\":\"Alpha\",\"spaces\":[\"work\"]}\n{\"type\":\"alta.project.item\",\"projectId\":\"project-1\",\"slug\":\"beta\",\"displayName\":\"Beta\",\"spaces\":[]}\n"
            : text.StartsWith("space list", StringComparison.Ordinal)
                ? "{\"type\":\"alta.space.item\",\"id\":\"default\",\"name\":\"Default\",\"default\":true}\n{\"type\":\"alta.space.item\",\"id\":\"work\",\"name\":\"Work\",\"default\":false}\n"
                : string.Empty;
        return ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = transcript });
    }
}

[TestClass]
public sealed class StatisticsPluginTests
{
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(StatisticsPlugin plugin, IPluginServices services, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var descriptor = PluginDescriptorFactory.FromType(typeof(StatisticsPlugin));
        var context = new PluginAltaCommandContext
        {
            Plugin = descriptor,
            Services = services,
            CorrelationId = "corr-1",
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
        };
        var node = (Command)plugin.GetAltaCommands().Single().CreateCommandNode(context);
        var app = new CommandApp("alta", "test", new CommandConfig { StrictOptionParsing = true }) { new CommandUsage(), new HelpOption() };
        app.Add(node);
        var exit = await app.RunAsync(["statistics", .. args], new CommandRunConfig { Out = stdout, Error = stderr });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement Single(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, stdout);
        return JsonDocument.Parse(lines[0]).RootElement;
    }

    [TestMethod]
    public async Task TheCommands_ReadTheSameNumbersAsTheQueries()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC", now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());

        var summary = await RunAsync(plugin, services, "summary", "--period", "2026-04-01..2026-09-30", "--by", "month");
        var series = await RunAsync(plugin, services, "series", "tokens", "--period", "2026-04-01..2026-09-30", "--by", "month", "--group", "provider");
        var top = await RunAsync(plugin, services, "top", "models", "--period", "2026-04-01..2026-09-30", "--by", "calls", "--limit", "2");

        Assert.AreEqual(0, summary.ExitCode, summary.Stderr);
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month };
        var expected = await harness.Queries.SummaryAsync(request);
        var record = Single(summary.Stdout);
        Assert.AreEqual("alta.statistics.summary", record.GetProperty("type").GetString());
        Assert.AreEqual(1, record.GetProperty("version").GetInt32());
        Assert.AreEqual("corr-1", record.GetProperty("correlationId").GetString());
        var tokens = record.GetProperty("tiles").EnumerateArray().Single(static tile => tile.GetProperty("id").GetString() == "tokens");
        Assert.AreEqual(expected.Tiles.Single(static tile => tile.Id == "tokens").Value, tokens.GetProperty("value").GetDouble(), 0.001);

        Assert.AreEqual(0, series.ExitCode, series.Stderr);
        var seriesRecord = Single(series.Stdout);
        Assert.AreEqual("alta.statistics.series", seriesRecord.GetProperty("type").GetString());
        Assert.AreEqual("provider", seriesRecord.GetProperty("group").GetString());
        var total = seriesRecord.GetProperty("series").EnumerateArray().Sum(static line => line.GetProperty("total").GetDouble());
        Assert.AreEqual(expected.Tiles.Single(static tile => tile.Id == "tokens").Value, total, 0.001);

        Assert.AreEqual(0, top.ExitCode, top.Stderr);
        var rows = Single(top.Stdout).GetProperty("rows");
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual("alta.statistics.top", Single(top.Stdout).GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task AProjectFilter_IsResolvedThroughTheProjectsOfTheHost()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC");
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());

        var byName = await RunAsync(plugin, services, "summary", "--period", "2026-04-01..2026-09-30", "--project", "Alpha");
        var bySpace = await RunAsync(plugin, services, "summary", "--period", "2026-04-01..2026-09-30", "--space", "work");

        var alphaTokens = harness.Naive(QueryHarness.Tokens, StatisticsFrequency.Year, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30), DayOfWeek.Monday, batch => batch.Session!.ProjectRef == "project-0").Values.Sum();
        foreach (var result in new[] { byName, bySpace })
        {
            Assert.AreEqual(0, result.ExitCode, result.Stderr);
            var tokens = Single(result.Stdout).GetProperty("tiles").EnumerateArray().Single(static tile => tile.GetProperty("id").GetString() == "tokens");
            Assert.AreEqual(alphaTokens, tokens.GetProperty("value").GetDouble(), 0.001);
        }

        StringAssert.Contains(bySpace.Stdout, "space-membership-is-current");
    }

    [TestMethod]
    public async Task TheCommands_RefuseWhatTheyCannotAnswer_WithAStableError()
    {
        await using var harness = await QueryHarness.CreateAsync();
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());

        var period = await RunAsync(plugin, services, "summary", "--period", "soon");
        var metric = await RunAsync(plugin, services, "series", "colors");
        var missing = await RunAsync(plugin, services, "series");
        var kind = await RunAsync(plugin, services, "top", "ghosts");
        var by = await RunAsync(plugin, services, "summary", "--by", "fortnight");
        var session = await RunAsync(plugin, services, "session", "no-such-session");
        var noDatabase = await RunAsync(plugin, NoopPluginServices.Create(), "summary");
        var choose = await RunAsync(plugin, services, "history", "read", "--days", "0");
        var notRunning = await RunAsync(plugin, services, "history", "pause");

        Assert.AreNotEqual(0, missing.ExitCode, "The command line itself asks for the metric.");
        foreach (var result in new[] { period, metric, kind, by })
        {
            Assert.AreEqual(2, result.ExitCode, result.Stdout + result.Stderr);
            Assert.AreEqual("alta.error", JsonDocument.Parse(result.Stderr.Trim()).RootElement.GetProperty("type").GetString());
        }

        Assert.AreEqual(1, session.ExitCode);
        StringAssert.Contains(session.Stderr, "session.notFound");
        Assert.AreEqual(1, noDatabase.ExitCode);
        StringAssert.Contains(noDatabase.Stderr, "statistics.unavailable");
        Assert.AreEqual(2, choose.ExitCode);
        Assert.AreEqual(1, notRunning.ExitCode);
        StringAssert.Contains(notRunning.Stderr, "statistics.notRunning");
    }

    [TestMethod]
    public async Task TheStatusOfAProcessThatReadsNothing_IsReadFromTheStore()
    {
        await using var harness = await QueryHarness.CreateAsync(chosen: false);
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());

        var before = Single((await RunAsync(plugin, services, "status")).Stdout);
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?> { ["history.choice"] = "days:30", ["history.paused"] = "1", ["history.complete_from_day"] = "20260901", ["history.floor_day"] = "20260910" });
        var after = Single((await RunAsync(plugin, services, "status")).Stdout);

        Assert.AreEqual("needsChoice", before.GetProperty("state").GetString());
        Assert.IsFalse(before.GetProperty("running").GetBoolean());
        Assert.AreEqual("alta.statistics.status", before.GetProperty("type").GetString());
        Assert.AreEqual("paused", after.GetProperty("state").GetString());
        Assert.AreEqual("days:30", after.GetProperty("choice").GetString());
        Assert.AreEqual(20260901, after.GetProperty("completeFromDay").GetInt32());
        Assert.AreEqual(8, after.GetProperty("sessionsDone").GetInt32());
    }

    [TestMethod]
    public async Task TheHelp_OfEveryCommand_IsWritten()
    {
        var plugin = new StatisticsPlugin();
        var services = NoopPluginServices.Create();
        foreach (var path in new[] { Array.Empty<string>(), ["summary"], ["series"], ["top"], ["session"], ["status"], ["history"], ["history", "read"], ["history", "pause"], ["history", "forget-deleted"], ["estimate"] })
        {
            var help = await RunAsync(plugin, services, [.. path, "--help"]);

            Assert.AreEqual(0, help.ExitCode, string.Join(' ', path) + help.Stderr);
            StringAssert.Contains(help.Stdout, "alta statistics" + (path.Length == 0 ? string.Empty : " " + string.Join(' ', path)));
        }

        var root = await RunAsync(plugin, services, "--help");
        foreach (var word in new[] { "summary", "series", "top", "session", "status", "history", "estimate" })
        {
            StringAssert.Contains(root.Stdout, word);
        }
    }

    [TestMethod]
    public async Task EstimateStaysAsItWas()
    {
        var plugin = new StatisticsPlugin();

        var result = await RunAsync(plugin, NoopPluginServices.Create(), "estimate", "four five six");

        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var record = Single(result.Stdout);
        Assert.AreEqual("alta.statistics.estimate", record.GetProperty("type").GetString());
        Assert.AreEqual(13, record.GetProperty("bytes").GetInt32());
    }

    [TestMethod]
    public async Task OnDesktop_ThePluginReadsWhatTheUserChose_KeepsTheFlowAndAnswersTheCommands()
    {
        using var temp = new TempFolder();
        await using var application = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });
        var journals = new FakeJournalCatalog();
        var start = EngineHarness.Now.AddHours(-3);
        var session = EngineHarness.Session("a", start, 2);
        journals.Set(session, EngineHarness.EndOf(session, start));
        StatisticsPlugin? created = null;
        var runtime = new PluginRuntimeManager();
        var result = await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = temp.Path,
            StateRoot = temp.Path,
            ApplicationDatabase = application,
            Frontend = PluginFrontends.Desktop,
            BuiltIns =
            [
                new BuiltInPluginDefinition
                {
                    Id = "statistics",
                    DisplayName = "Statistics",
                    PluginType = typeof(StatisticsPlugin),
                    Factory = () => created = new StatisticsPlugin(journals, TimeSpan.Zero, TimeSpan.Zero),
                },
            ],
        });
        try
        {
            Assert.IsNotNull(created);
            var service = created.Statistics;
            Assert.IsNotNull(service, "The job runs on the desktop, with a database.");
            await WaitAsync(() => service.Status.State == HistoryState.NeedsChoice);
            Assert.AreEqual(0, journals.Opens, "Nothing is read until the user chooses.");

            await service.ChooseHistoryAsync(HistoryChoice.All);
            await WaitAsync(() => service.Status.State == HistoryState.Done);

            var summary = await service.Queries.SummaryAsync(new StatisticsRequest { Period = "2026-10-09..2026-10-09" });
            Assert.AreEqual(2, summary.Tiles.Single(static tile => tile.Id == "runs").Value);

            // An event of a session is a signal: the session is caught up from its journal.
            journals.Append("a", EngineHarness.More("a", start.AddHours(1), 1), start.AddHours(1).AddSeconds(30));
            var active = result.ActivePlugins.Single();
            await runtime.Adapter.ObserveAgentEventAsync(
                result.ActivePlugins,
                new PluginAgentEventContext
                {
                    Plugin = active.Descriptor,
                    Services = active.RuntimeContext.Services,
                    Event = new AgentActivityEvent(new ModelProviderId("codex"), "a", DateTimeOffset.UtcNow, null, AgentActivityKind.Turn, AgentActivityPhase.Started, "x", null, "turn", "started"),
                });
            await WaitAsync(async () => (await service.Queries.SummaryAsync(new StatisticsRequest { Period = "2026-10-09..2026-10-09" })).Tiles.Single(static tile => tile.Id == "runs").Value == 3);

            var stdout = new StringWriter();
            var context = new PluginAltaCommandContext { Plugin = active.Descriptor, Services = active.RuntimeContext.Services, CorrelationId = "c", Stdin = TextReader.Null, Stdout = stdout, Stderr = new StringWriter() };
            var node = (Command)created.GetAltaCommands().Single().CreateCommandNode(context);
            var app = new CommandApp("alta", "test", new CommandConfig { StrictOptionParsing = true }) { new CommandUsage(), new HelpOption() };
            app.Add(node);
            Assert.AreEqual(0, await app.RunAsync(["statistics", "status"], new CommandRunConfig { Out = stdout, Error = new StringWriter() }));
            var status = Single(stdout.ToString());
            Assert.IsTrue(status.GetProperty("running").GetBoolean());
            Assert.AreEqual("done", status.GetProperty("state").GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        Assert.IsNull(created.Statistics, "The engine stops with the plugin.");
    }

    [TestMethod]
    public async Task InTheTerminal_OrWithoutAWindow_ThePluginReadsNothing()
    {
        using var temp = new TempFolder();
        await using var application = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });
        var journals = new FakeJournalCatalog();
        foreach (var (frontend, database) in new[] { (PluginFrontends.Terminal, true), (PluginFrontends.None, true) })
        {
            StatisticsPlugin? created = null;
            var runtime = new PluginRuntimeManager();
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = temp.Path,
                StateRoot = temp.Path,
                ApplicationDatabase = database ? application : null,
                Frontend = frontend,
                BuiltIns = [new BuiltInPluginDefinition { Id = "statistics", DisplayName = "Statistics", PluginType = typeof(StatisticsPlugin), Factory = () => created = new StatisticsPlugin(journals, TimeSpan.Zero, TimeSpan.Zero) }],
            });
            try
            {
                Assert.IsNotNull(created);
                Assert.IsNull(created.Statistics, $"{frontend}, database: {database}");
            }
            finally
            {
                await runtime.DisposeAsync();
            }
        }

        Assert.AreEqual(0, journals.Opens);
    }

    private static Task WaitAsync(Func<bool> condition) => WaitAsync(() => Task.FromResult(condition()));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(25);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.Plugin.Statistics.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
