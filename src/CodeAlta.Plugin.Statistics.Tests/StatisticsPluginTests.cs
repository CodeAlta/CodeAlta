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
                : text.StartsWith("provider list", StringComparison.Ordinal)
                    ? "{\"type\":\"alta.provider.item\",\"providerKey\":\"claude-code\",\"displayName\":\"Claude Code\"}\n{\"type\":\"alta.provider.item\",\"providerKey\":\"codex\",\"displayName\":\"Codex\"}\n{\"type\":\"alta.provider.item\",\"providerKey\":\"bare\"}\n"
                    : string.Empty;
        return ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = transcript });
    }
}

[TestClass]
public sealed class StatisticsPluginTests
{
    /// <summary>
    /// The time zone of the stores of these tests: the one of the machine. A plugin always cuts its days in that zone, so a store made
    /// in another one would have its roll-ups added up again by the first command, and the numbers of a test would be those of other days.
    /// </summary>
    private static string LocalZone => TimeZoneInfo.Local.Id;

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
        await using var harness = await QueryHarness.CreateAsync(LocalZone, now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
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
        await using var harness = await QueryHarness.CreateAsync(LocalZone);
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

    /// <summary>Checks that a record of a command holds the result of the query, property for property, after its envelope.</summary>
    private static void AssertRecordIs<T>(string type, T expected, JsonElement record)
    {
        Assert.AreEqual(type, record.GetProperty("type").GetString());
        Assert.AreEqual(1, record.GetProperty("version").GetInt32());
        Assert.AreEqual("corr-1", record.GetProperty("correlationId").GetString());
        using var document = JsonDocument.Parse(StatisticsJson.Serialize(expected));
        var properties = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.IsTrue(record.TryGetProperty(property.Name, out var value), property.Name);
            if (property.NameEquals("query"))
            {
                // The header names today, which is the day of the clock of the test for the query and the real one for the command.
                foreach (var name in new[] { "period", "from", "frequency", "timeZone", "compareFrom", "compareTo" })
                {
                    Assert.AreEqual(
                        property.Value.TryGetProperty(name, out var wanted) ? wanted.GetRawText() : null,
                        value.TryGetProperty(name, out var found) ? found.GetRawText() : null,
                        "query." + name);
                }

                continue;
            }

            Assert.AreEqual(property.Value.GetRawText(), value.GetRawText(), property.Name);
            properties++;
        }

        Assert.IsTrue(properties > 0, "the result has more than its header");
    }

    [TestMethod]
    public async Task TheDetails_AreTheNamesOfTheQuery_AndAListIsRequired()
    {
        await using var harness = await QueryHarness.CreateAsync(LocalZone, now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Limit = 2 };

        foreach (var list in new[] { "shell-program", "alta-command", "changed-file-extension" })
        {
            var result = await RunAsync(plugin, services, "details", list, "--period", "2026-04-01..2026-09-30", "--limit", "2");

            Assert.AreEqual(0, result.ExitCode, result.Stderr);
            var expected = await harness.Queries.DetailsAsync(request, list);
            Assert.AreEqual(2, expected.Rows.Count, list);
            AssertRecordIs("alta.statistics.details", expected, Single(result.Stdout));
        }

        // A project narrows the names to its sessions.
        var ofProject = await RunAsync(plugin, services, "details", "shell-program", "--period", "2026-04-01..2026-09-30", "--project", "alpha");
        Assert.AreEqual(0, ofProject.ExitCode, ofProject.Stderr);
        AssertRecordIs("alta.statistics.details", await harness.Queries.DetailsAsync(new StatisticsRequest { Period = "2026-04-01..2026-09-30", Filter = new StatisticsFilter { Project = "project-0" } }, "shell-program"), Single(ofProject.Stdout));

        var unknown = await RunAsync(plugin, services, "details", "colors");
        Assert.AreEqual(2, unknown.ExitCode);
        StringAssert.Contains(unknown.Stderr, "usage.invalidQuery");
        Assert.AreNotEqual(0, (await RunAsync(plugin, services, "details")).ExitCode, "The command line asks for the list.");
    }

    [TestMethod]
    public async Task OneSession_IsGivenAlone_OrWithTheSessionsItCreated()
    {
        // The life of a session ends today: the query it is compared with has the clock of the command, which is the one of the machine.
        await using var harness = await QueryHarness.CreateAsync(LocalZone, now: DateTimeOffset.UtcNow);
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());

        // The projects the command learns from the host, for the query it is compared with.
        harness.Directory.Projects.AddRange([new ProjectInfo("project-0", "alpha", "Alpha", ["work"]), new ProjectInfo("project-1", "beta", "Beta", [])]);

        // session-1 created session-2 (QueryHarness).
        var alone = await RunAsync(plugin, services, "session", "session-1");
        var withChildren = await RunAsync(plugin, services, "session", "session-1", "--with-children");
        var byStart = await RunAsync(plugin, services, "session", "session-4", "--with-children");

        Assert.AreEqual(0, alone.ExitCode, alone.Stderr);
        Assert.AreEqual(0, withChildren.ExitCode, withChildren.Stderr);
        AssertRecordIs("alta.statistics.session", (await harness.Queries.SessionAsync("session-1", false))!, Single(alone.Stdout));
        AssertRecordIs("alta.statistics.session", (await harness.Queries.SessionAsync("session-1", true))!, Single(withChildren.Stdout));
        Assert.AreEqual(0, Single(alone.Stdout).GetProperty("children").GetArrayLength());
        CollectionAssert.AreEqual(new[] { "session-2" }, Single(withChildren.Stdout).GetProperty("children").EnumerateArray().Select(static child => child.GetProperty("sessionId").GetString()).ToArray());
        static double Tokens(JsonElement record) => record.GetProperty("totals").EnumerateArray().Single(static tile => tile.GetProperty("id").GetString() == "tokens").GetProperty("value").GetDouble();
        Assert.IsTrue(Tokens(Single(withChildren.Stdout)) > Tokens(Single(alone.Stdout)), "The tokens of the child are added to those of the session.");
        Assert.AreEqual(0, byStart.ExitCode, byStart.Stderr);
        Assert.AreEqual(1, Single(byStart.Stdout).GetProperty("children").GetArrayLength());
    }

    [TestMethod]
    public async Task TheComparison_TheOriginAndTheKindOfTool_ReachTheQuery()
    {
        await using var harness = await QueryHarness.CreateAsync(LocalZone, now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());
        string[] period = ["--period", "2026-06-01..2026-06-30", "--by", "week", "--week-start", "monday"];
        var request = new StatisticsRequest { Period = "2026-06-01..2026-06-30", Frequency = StatisticsFrequency.Week, WeekStart = DayOfWeek.Monday };

        var previous = await RunAsync(plugin, services, ["summary", .. period, "--compare", "previous"]);
        var year = await RunAsync(plugin, services, ["summary", .. period, "--compare", "year"]);
        var none = await RunAsync(plugin, services, ["summary", .. period, "--compare", "none"]);
        var origin = await RunAsync(plugin, services, ["series", "prompts", .. period, "--origin", "agent"]);
        var toolKind = await RunAsync(plugin, services, ["series", "tool-calls", .. period, "--tool-kind", "shell"]);
        var both = await RunAsync(plugin, services, ["series", "tokens", .. period, "--provider", "codex", "--model", "gpt-5-mini", "--effort", "high"]);

        foreach (var result in new[] { previous, year, none, origin, toolKind, both })
        {
            Assert.AreEqual(0, result.ExitCode, result.Stderr);
        }

        AssertRecordIs("alta.statistics.summary", await harness.Queries.SummaryAsync(request with { Comparison = StatisticsComparison.PreviousPeriod }), Single(previous.Stdout));
        Assert.AreEqual("2026-05-02", Single(previous.Stdout).GetProperty("query").GetProperty("compareFrom").GetString(), "The 30 days before the first of June.");
        AssertRecordIs("alta.statistics.summary", await harness.Queries.SummaryAsync(request with { Comparison = StatisticsComparison.SamePeriodLastYear }), Single(year.Stdout));
        Assert.AreEqual("2025-06-01", Single(year.Stdout).GetProperty("query").GetProperty("compareFrom").GetString());
        Assert.IsFalse(Single(none.Stdout).GetProperty("query").TryGetProperty("compareFrom", out _));

        var byOrigin = await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { Origin = "agent" } }, "prompts", null);
        AssertRecordIs("alta.statistics.series", byOrigin, Single(origin.Stdout));
        var every = await harness.Queries.SeriesAsync(request, "prompts", null);
        Assert.IsTrue(byOrigin.Series.Sum(static line => line.Total) < every.Series.Sum(static line => line.Total), "The prompts of agents are some of the prompts.");

        var byKind = await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { ToolKind = "shell" } }, "tool-calls", null);
        AssertRecordIs("alta.statistics.series", byKind, Single(toolKind.Stdout));
        Assert.IsTrue(byKind.Series.Sum(static line => line.Total) < (await harness.Queries.SeriesAsync(request, "tool-calls", null)).Series.Sum(static line => line.Total));

        AssertRecordIs("alta.statistics.series", await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { Provider = "codex", Model = "gpt-5-mini", Effort = "high" } }, "tokens", null), Single(both.Stdout));

        var bad = await RunAsync(plugin, services, ["summary", .. period, "--compare", "decade"]);
        Assert.AreEqual(2, bad.ExitCode);
        StringAssert.Contains(bad.Stderr, "usage.invalidQuery");
    }

    [TestMethod]
    public async Task TheWeeksOfACommand_StartOnTheDayAsked_AndOnTheDayOfTheComputerOtherwise()
    {
        await using var harness = await QueryHarness.CreateAsync(LocalZone, now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var plugin = new StatisticsPlugin();
        var services = new TestServices(harness.Store.Database, new FakeAlta());
        // A period that starts on a Wednesday: its second bucket is the first whole week.
        string[] weeks = ["series", "tokens", "--period", "2026-06-03..2026-06-30", "--by", "week"];
        static DayOfWeek FirstWholeWeek(string stdout)
            => DateOnly.ParseExact(Single(stdout).GetProperty("buckets")[1].GetProperty("start").GetString()![..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayOfWeek;

        var monday = await RunAsync(plugin, services, [.. weeks, "--week-start", "monday"]);
        var sunday = await RunAsync(plugin, services, [.. weeks, "--week-start", "Sunday"]);
        Assert.AreEqual(0, monday.ExitCode, monday.Stderr);
        Assert.AreEqual(DayOfWeek.Monday, FirstWholeWeek(monday.Stdout));
        Assert.AreEqual(DayOfWeek.Sunday, FirstWholeWeek(sunday.Stdout));

        // Without the option, the day of the regional settings: the one the page is told, so both cut the same weeks.
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var saturday = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            saturday.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Saturday;
            System.Globalization.CultureInfo.CurrentCulture = saturday;
            Assert.AreEqual(DayOfWeek.Saturday, StatisticsQueries.DefaultWeekStart);
            var byDefault = await RunAsync(plugin, services, weeks);
            Assert.AreEqual(0, byDefault.ExitCode, byDefault.Stderr);
            Assert.AreEqual(DayOfWeek.Saturday, FirstWholeWeek(byDefault.Stdout));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        // A day or a frequency is a name: a number is not one, even one that an enumeration has.
        foreach (var arguments in new[] { new[] { "--week-start", "someday" }, ["--week-start", "3"], ["--week-start", "9"], ["--by", "2"], ["--by", "99"] })
        {
            var refused = await RunAsync(plugin, services, ["series", "tokens", "--period", "2026-06-03..2026-06-30", .. arguments]);
            Assert.AreEqual(2, refused.ExitCode, string.Join(' ', arguments) + refused.Stdout);
            StringAssert.Contains(refused.Stderr, "usage.invalidQuery");
        }
    }

    [TestMethod]
    public async Task AnError_IsOneRecordWithItsCodeItsExitCodeAndItsSentence()
    {
        await using var harness = await QueryHarness.CreateAsync();
        var plugin = new StatisticsPlugin();

        var result = await RunAsync(plugin, new TestServices(harness.Store.Database, new FakeAlta()), "summary", "--period", "so\"on");

        var lines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, result.Stderr);
        var error = JsonDocument.Parse(lines[0]).RootElement;
        CollectionAssert.AreEqual(new[] { "type", "version", "correlationId", "code", "exitCode", "message" }, error.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("alta.error", error.GetProperty("type").GetString());
        Assert.AreEqual(1, error.GetProperty("version").GetInt32());
        Assert.AreEqual("corr-1", error.GetProperty("correlationId").GetString());
        Assert.AreEqual("usage.invalidQuery", error.GetProperty("code").GetString());
        Assert.AreEqual(2, error.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(error.GetProperty("message").GetString(), "'so\"on' is not a period");
    }

    [TestMethod]
    public async Task TheHistoryCommands_ChooseReadMorePauseResumeAndStop_WhereTheEngineRuns()
    {
        var time = new ManualTime(EngineHarness.Now);
        await using var harness = await CanvasPluginHarness.CreateAsync(startDelay: Timeout.InfiniteTimeSpan, time: time);
        var recent = EngineHarness.Session("recent", EngineHarness.Now.AddHours(-3), 1);
        harness.Journals.Set(recent, EngineHarness.Now.AddHours(-2));
        var old = EngineHarness.Session("old", EngineHarness.Now.AddDays(-50), 1);
        harness.Journals.Set(old, EngineHarness.Now.AddDays(-50).AddMinutes(1));
        var engine = (StatisticsEngine)harness.Plugin.Statistics!;
        // The plugin cuts its days in the zone of the machine: today, and the first of the last 30 days, are days of that zone.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(EngineHarness.Now, TimeZoneInfo.Local).DateTime);
        var floor30 = Store.LocalDays.ToDay(today.AddDays(-29));
        async Task<JsonElement> HistoryAsync(params string[] arguments)
        {
            var result = await RunAsync(harness.Plugin, harness.Services, ["history", .. arguments]);
            Assert.AreEqual(0, result.ExitCode, string.Join(' ', arguments) + result.Stderr);
            var record = Single(result.Stdout);
            Assert.AreEqual("alta.statistics.history", record.GetProperty("type").GetString());
            Assert.AreEqual(arguments[0], record.GetProperty("action").GetString());
            Assert.IsTrue(record.GetProperty("running").GetBoolean());
            return record;
        }

        // The first choice: the last 30 days. It starts the reading.
        var chosen = await HistoryAsync("read", "--days", "30");
        Assert.AreEqual("reading", chosen.GetProperty("state").GetString());
        Assert.AreEqual("days:30", chosen.GetProperty("choice").GetString());
        Assert.AreEqual(floor30, chosen.GetProperty("floorDay").GetInt32());

        var paused = await HistoryAsync("pause");
        Assert.AreEqual("paused", paused.GetProperty("state").GetString());
        var resumed = await HistoryAsync("resume");
        Assert.AreEqual("reading", resumed.GetProperty("state").GetString());
        await engine.DrainAsync();
        Assert.AreEqual(HistoryState.Done, engine.Status.State);
        Assert.IsNull(await engine.Store.GetJournalAsync("old"), "Older than the 30 days.");

        // A choice that does not go further back changes nothing; one that does reads more.
        var less = await HistoryAsync("read", "--days", "7");
        Assert.AreEqual("done", less.GetProperty("state").GetString());
        Assert.AreEqual("days:30", less.GetProperty("choice").GetString());
        var more = await HistoryAsync("read", "--all");
        Assert.AreEqual("reading", more.GetProperty("state").GetString());
        Assert.AreEqual("all", more.GetProperty("choice").GetString());
        Assert.AreEqual("extended", more.GetProperty("reason").GetString());

        // Stopped where it is: the statistics keep starting where they did, and the status command says so.
        var stopped = await HistoryAsync("stop");
        Assert.AreEqual("stoppedHere", stopped.GetProperty("state").GetString());
        Assert.AreEqual(floor30, stopped.GetProperty("floorDay").GetInt32());
        var status = Single((await RunAsync(harness.Plugin, harness.Services, "status")).Stdout);
        Assert.AreEqual("stoppedHere", status.GetProperty("state").GetString());
        Assert.IsTrue(status.GetProperty("running").GetBoolean());

        // From today, on a history that was never chosen, is the third choice.
        await engine.ResetAsync();
        var fromToday = await HistoryAsync("read", "--from-today");
        Assert.AreEqual("from-today", fromToday.GetProperty("choice").GetString());
        Assert.AreEqual(Store.LocalDays.ToDay(today), fromToday.GetProperty("floorDay").GetInt32());

        // Two choices at once, or none, are a usage error.
        Assert.AreEqual(2, (await RunAsync(harness.Plugin, harness.Services, "history", "read", "--all", "--days", "3")).ExitCode);
        Assert.AreEqual(2, (await RunAsync(harness.Plugin, harness.Services, "history", "read")).ExitCode);
    }

    [TestMethod]
    public async Task ForgetDeleted_RemovesTheSessionsWhoseJournalIsGone_AndSaysHowMany()
    {
        await using var query = await QueryHarness.CreateAsync(LocalZone, now: new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store, startDelay: Timeout.InfiniteTimeSpan, time: new ManualTime(EngineHarness.Now));
        await query.Store.Store.MarkDeletedAsync(["session-0", "session-3"]);
        var sessions = await query.Store.CountAsync("session");

        var forgotten = await RunAsync(harness.Plugin, harness.Services, "history", "forget-deleted");
        var again = await RunAsync(harness.Plugin, harness.Services, "history", "forget-deleted");

        Assert.AreEqual(0, forgotten.ExitCode, forgotten.Stderr);
        var record = Single(forgotten.Stdout);
        Assert.AreEqual("alta.statistics.forgotten", record.GetProperty("type").GetString());
        Assert.AreEqual(2, record.GetProperty("sessions").GetInt32());
        Assert.AreEqual(sessions - 2, await query.Store.CountAsync("session"));
        Assert.AreEqual(0, Single(again.Stdout).GetProperty("sessions").GetInt32());
        await query.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task AQuestionAskedBeforeTheEngineIsReady_PreparesTheTablesFirst()
    {
        // A database that never had the tables of the statistics, and an engine that has not started its work yet.
        await using var store = await StoreHarness.CreateAsync(initialize: false);
        await using var harness = await CanvasPluginHarness.CreateAsync(store, startDelay: Timeout.InfiniteTimeSpan, time: new ManualTime(EngineHarness.Now));

        var summary = await RunAsync(harness.Plugin, harness.Services, "summary");
        var status = await RunAsync(harness.Plugin, harness.Services, "status");

        Assert.AreEqual(0, summary.ExitCode, summary.Stderr);
        Assert.AreEqual("alta.statistics.summary", Single(summary.Stdout).GetProperty("type").GetString());
        Assert.AreEqual(0, status.ExitCode, status.Stderr);
        Assert.AreEqual("needsChoice", Single(status.Stdout).GetProperty("state").GetString(), "The state is known, not 'starting'.");
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
