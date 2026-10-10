using System.Text.Json;
using CodeAlta.Plugin.Statistics.Canvas;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The Statistics plugin as a canvas of the window: what it declares, its button, its command, and the calls its script makes.</summary>
[TestClass]
public sealed class StatisticsCanvasPluginTests
{
    private static readonly string[] Questions =
    [
        "summary", "series", "top", "tools", "models", "projects", "sessions", "session", "distribution", "calendar", "week-hour", "records", "health", "details", "runs",
    ];

    private static readonly string[] Controls = ["status", "choose-history", "pause", "resume", "stop-here", "forget-deleted", "reset", "context"];

    private const string Request = """{"period":"all","frequency":"month","comparison":"none","filter":{}}""";

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < end, "Waited for " + what);
            await Task.Delay(20);
        }
    }

    // ---- what the plugin declares ----

    [TestMethod]
    public async Task TheDesktopPlugin_DeclaresTheCanvas_TheButtons_AndTheCommands()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();

        var canvas = harness.Plugin.GetCanvases().Single();
        Assert.AreEqual("statistics", canvas.Id);
        Assert.AreEqual("Statistics", canvas.Title);
        Assert.AreEqual("chart-column", canvas.Icon);
        Assert.AreEqual(PluginCanvasScope.Application, canvas.Scope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(canvas.Description));

        var buttons = harness.Plugin.GetUiContributions().OfType<PluginButtonContribution>().ToList();
        Assert.AreEqual(2, buttons.Count);
        var title = buttons.Single(button => button.Place == PluginButtonPlace.TitleBar);
        Assert.AreEqual(("statistics", "chart-column", "Statistics", "statistics", null), (title.Id, title.Icon, title.Label, title.Canvas, title.Command));
        var menu = buttons.Single(button => button.Place == PluginButtonPlace.ProjectMenu);
        Assert.AreEqual(("statistics-project", null, "statistics-project", "Statistics of this project"), (menu.Id, menu.Canvas, menu.Command, menu.Label));
        Assert.IsTrue(buttons.All(button => button.Validate() is null), "the host keeps both buttons");
        Assert.IsFalse(buttons.Any(button => button.Place == PluginButtonPlace.SessionMenu), "no session filter exists in the queries: the session menu has no line");

        var commands = harness.Plugin.GetCommands().ToList();
        var open = commands.Single(command => command.Name == "statistics");
        Assert.AreEqual("Ctrl+G C", open.KeyBinding!.ToString());
        Assert.IsTrue(open.ShowInCommandPalette && open.ShowInHelp, "/statistics is in the palette and in the help");
        var project = commands.Single(command => command.Name == "statistics-project");
        Assert.IsFalse(project.ShowInCommandPalette || project.ShowInHelp || project.ShowInCommandBar, "the line of the menu runs it; the palette does not list it");
        Assert.IsTrue(project.Availability.RequiresProject);
        Assert.IsTrue(commands.All(command => command.KeyBinding is null || command.Name == "statistics"), "one shortcut only");
        Assert.IsTrue(commands.Select(command => command.Name).Contains(menu.Command!), "the button names a command the plugin has");
    }

    [TestMethod]
    public async Task ThePlugin_HasNoCanvasButtonOrCommand_WhereItReadsNothing()
    {
        var store = await StoreHarness.CreateAsync();
        await using var _ = store;

        // CodeAlta TUI: the plugin is made without journals.
        var tui = new StatisticsPlugin();
        var services = new WindowServices(store.Database);
        tui.AttachRuntimeContext(services.Context(PluginFrontends.Terminal));
        // The same plugin, made for the window, in an application that is not the window.
        var elsewhere = StatisticsPlugin.CreateForDesktop(new FakeJournalCatalog());
        elsewhere.AttachRuntimeContext(services.Context(PluginFrontends.Terminal));
        // The window, with a host that has no database.
        var noDatabase = StatisticsPlugin.CreateForDesktop(new FakeJournalCatalog());
        var bare = new WindowServices(NoopPluginServices.Create().Database);
        noDatabase.AttachRuntimeContext(bare.Context(PluginFrontends.Desktop));
        // A desktop plugin that is not given a database is the same as one without journals.
        var withoutJournals = new StatisticsPlugin();
        withoutJournals.AttachRuntimeContext(services.Context(PluginFrontends.Desktop));

        foreach (var plugin in new[] { tui, elsewhere, noDatabase, withoutJournals })
        {
            Assert.AreEqual(0, plugin.GetCanvases().Count());
            Assert.AreEqual(0, plugin.GetUiContributions().Count());
            Assert.AreEqual(0, plugin.GetCommands().Count());
            Assert.IsNull(plugin.Statistics);
        }

        // The cards of the timeline and `alta statistics` stay.
        Assert.AreEqual(1, tui.GetSessionEventProjections().Count());
        Assert.AreEqual(1, tui.GetAltaCommands().Count());
    }

    // ---- the button follows the history ----

    [TestMethod]
    public async Task TheButton_ShowsADotWhileTheChoiceWaits_ARingWhileTheHistoryIsRead_AndNothingOtherwise()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();
        var button = harness.Plugin.GetUiContributions().OfType<PluginButtonContribution>().Single(candidate => candidate.Place == PluginButtonPlace.TitleBar);
        var context = new PluginButtonContext(PluginButtonPlace.TitleBar, "work", null, null);
        PluginButtonState? State() => button.GetState!(context);
        var invalidations = () => harness.Services.RecordingUi.Invalidations;

        Assert.IsNull(State(), "nothing before the engine says anything");

        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.NeedsChoice });
        Assert.AreEqual(PluginButtonBadgeKind.Dot, State()!.Badge.Kind);
        Assert.AreEqual(1, invalidations());

        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.NeedsChoice, SessionsTotal = 9 });
        Assert.AreEqual(1, invalidations(), "the same kind of badge is not told again");

        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Reading });
        Assert.AreEqual(PluginButtonBadgeKind.Busy, State()!.Badge.Kind);
        Assert.AreEqual(2, invalidations());
        for (var done = 1; done <= 20; done++)
        {
            harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Reading, SessionsTotal = 20, SessionsDone = done });
        }

        Assert.AreEqual(2, invalidations(), "the progress of a reading never asks the host to read the buttons");

        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Paused });
        Assert.IsNull(State(), "a paused reading is no ring");
        Assert.AreEqual(3, invalidations());
        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Reading });
        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Done });
        Assert.IsNull(State());
        Assert.AreEqual(5, invalidations());
        harness.Plugin.OnButtonStatusChanged(new StatisticsStatus { State = HistoryState.Failed });
        Assert.AreEqual(5, invalidations());
    }

    [TestMethod]
    public async Task TheButton_FollowsARealReading_FromTheDotToTheRingToNothing()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync(startDelay: TimeSpan.Zero);
        var builder = EngineHarness.Session("s1", EngineHarness.Now.AddDays(-2), runs: 3);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, EngineHarness.Now.AddDays(-2), 3));
        var button = harness.Plugin.GetUiContributions().OfType<PluginButtonContribution>().Single(candidate => candidate.Place == PluginButtonPlace.TitleBar);
        var context = new PluginButtonContext(PluginButtonPlace.TitleBar, null, null, null);

        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.NeedsChoice, "the first-time state");
        Assert.AreEqual(PluginButtonBadgeKind.Dot, button.GetState!(context)!.Badge.Kind);
        var before = harness.Services.RecordingUi.Invalidations;
        Assert.IsTrue(before >= 1);

        await harness.Plugin.Statistics!.ChooseHistoryAsync(HistoryChoice.All);
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.Done, "the end of the reading");
        await WaitAsync(() => button.GetState!(context) is null, "the ring to go");
        Assert.IsTrue(harness.Services.RecordingUi.Invalidations >= before + 1, "the host read the buttons again when the dot went");
    }

    // ---- the commands ----

    [TestMethod]
    public async Task TheCommand_OpensTheCanvas_AndTheLineOfAProjectOpensItForThatProjectOnly()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();
        var commands = harness.Plugin.GetCommands().ToDictionary(command => command.Name);
        PluginCommandContext Context(string? project) => new() { Plugin = PluginDescriptorFactory.FromType(typeof(StatisticsPlugin)), Services = harness.Services, ProjectId = project };

        var opened = await commands["statistics"].Handler(Context(null), CancellationToken.None);
        Assert.AreEqual(PluginCommandResult.Handled, opened);
        Assert.AreEqual(("statistics", null), (harness.Services.RecordingCanvases.Opened[0].CanvasId, harness.Services.RecordingCanvases.Opened[0].Options?.Key));

        await commands["statistics-project"].Handler(Context("project-0"), CancellationToken.None);
        var project = harness.Services.RecordingCanvases.Opened[1];
        Assert.AreEqual(("statistics", "project:project-0"), (project.CanvasId, project.Options!.Key));
        Assert.AreEqual("project-0", StatisticsPlugin.ProjectOfKey(project.Options.Key));
        Assert.IsNull(StatisticsPlugin.ProjectOfKey("project:"));
        Assert.IsNull(StatisticsPlugin.ProjectOfKey("other"));
        Assert.IsNull(StatisticsPlugin.ProjectOfKey(null));

        var none = await commands["statistics-project"].Handler(Context(null), CancellationToken.None);
        Assert.AreNotEqual(PluginCommandResult.Handled, none);
        Assert.AreEqual(2, harness.Services.RecordingCanvases.Opened.Count, "no project, no canvas");

        harness.Services.RecordingCanvases.Status = PluginCanvasOpenStatus.Unavailable;
        Assert.AreNotEqual(PluginCommandResult.Handled, await commands["statistics"].Handler(Context(null), CancellationToken.None), "a host that cannot show it says so");
    }

    // ---- the view ----

    [TestMethod]
    public async Task TheView_NamesTheModuleOfTheApplication_RegistersTheCallsFirst_AndTitlesAProjectCanvas()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();

        var all = await harness.OpenAsync();
        Assert.AreEqual("statistics", all.Script!.AppModule);
        Assert.AreEqual("Statistics", all.Title);
        Assert.IsFalse(string.IsNullOrWhiteSpace(all.Fragment), "a skeleton is there before the module draws");
        var expected = Questions.Concat(Controls).Select(name => "statistics." + name).Order().ToArray();
        CollectionAssert.AreEqual(expected, harness.Rpc.Names.Order().ToArray());

        var project = await harness.OpenAsync("project:project-0");
        Assert.AreEqual("Statistics: Alpha", project.Title, "the name of the project, from the host");
        var unknown = await harness.OpenAsync("project:nope");
        Assert.AreEqual("Statistics", unknown.Title);

        var described = await harness.Plugin.GetCanvases().Single().Describe!(harness.Canvas, CancellationToken.None);
        StringAssert.StartsWith(described, "# Statistics");
    }

    // ---- the calls ----

    [TestMethod]
    public async Task EveryQuestion_AnswersTheJsonOfTheQueryService_ToTheLetter()
    {
        await using var query = await QueryHarness.CreateAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        var queries = harness.Plugin.Statistics!.Queries;
        var request = StatisticsJson.ParseRequest(Request);
        var calls = new (string Name, string Json, Func<Task<string>> Expected)[]
        {
            ("summary", Wrap(), async () => StatisticsJson.Serialize(await queries.SummaryAsync(request))),
            ("series", Wrap("\"metric\":\"tokens\",\"group\":\"provider\""), async () => StatisticsJson.Serialize(await queries.SeriesAsync(request, "tokens", "provider"))),
            ("series", Wrap("\"metric\":\"runs\",\"group\":null"), async () => StatisticsJson.Serialize(await queries.SeriesAsync(request, "runs", null))),
            ("top", Wrap("\"kind\":\"models\",\"by\":\"calls\""), async () => StatisticsJson.Serialize(await queries.TopAsync(request, "models", "calls"))),
            ("tools", Wrap(), async () => StatisticsJson.Serialize(await queries.ToolsAsync(request))),
            ("models", Wrap(), async () => StatisticsJson.Serialize(await queries.ModelsAsync(request))),
            ("projects", Wrap(), async () => StatisticsJson.Serialize(await queries.ProjectsAsync(request))),
            ("sessions", Wrap("\"sort\":\"tokens\""), async () => StatisticsJson.Serialize(await queries.SessionsAsync(request, "tokens"))),
            ("session", """{"id":"session-2","withChildren":true}""", async () => StatisticsJson.Serialize((await queries.SessionAsync("session-2", true))!)),
            ("distribution", Wrap("\"measure\":\"run-duration\",\"subject\":null"), async () => StatisticsJson.Serialize(await queries.DistributionAsync(request, "run-duration", null))),
            ("calendar", Wrap(), async () => StatisticsJson.Serialize(await queries.CalendarAsync(request))),
            ("week-hour", Wrap(), async () => StatisticsJson.Serialize(await queries.WeekHourAsync(request))),
            ("records", Wrap(), async () => StatisticsJson.Serialize(await queries.RecordsAsync(request))),
            ("health", Wrap(), async () => StatisticsJson.Serialize(await queries.HealthAsync(request))),
            ("details", Wrap("\"list\":\"skill\""), async () => StatisticsJson.Serialize(await queries.DetailsAsync(request, "skill"))),
            ("runs", Wrap("\"sort\":\"longest\""), async () => StatisticsJson.Serialize(await queries.RunsAsync(request, "longest"))),
        };

        foreach (var (name, json, expected) in calls)
        {
            var result = await harness.Rpc.InvokeAsync("statistics." + name, json);
            Assert.AreEqual(await expected(), result.GetRawText(), name);
        }

        Assert.AreEqual(Questions.Length, calls.Select(call => call.Name).Distinct().Count());

        static string Wrap(string? more = null) => "{\"request\":" + Request + (more is null ? string.Empty : "," + more) + "}";
    }

    [TestMethod]
    public async Task TheRequestOfAPage_IsReadAsThePageWritesIt_CamelCaseEnumsAndFilters()
    {
        await using var query = await QueryHarness.CreateAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        var request = """{"request":{"period":"2026-04-01..2026-09-30","frequency":"Month","comparison":"previousPeriod","filter":{"provider":"codex"},"limit":3,"weekStart":"Monday"}}""";

        var result = await harness.Rpc.InvokeAsync("statistics.models", request);

        var echoed = result.GetProperty("query");
        Assert.AreEqual("2026-04-01", echoed.GetProperty("from").GetString());
        Assert.AreEqual("month", echoed.GetProperty("frequency").GetString());
        Assert.IsTrue(echoed.TryGetProperty("compareFrom", out _), "the comparison was asked");
        Assert.IsFalse(result.GetRawText().Contains("\"filter\":null", StringComparison.Ordinal), "no noise: nulls are left out");
    }

    [TestMethod]
    public async Task TheStatus_OfAFirstStart_CountsTheSessionsTheCardTalksAbout()
    {
        await using var query = await QueryHarness.CreateAsync(sessions: 0, chosen: false);
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        for (var index = 0; index < 4; index++)
        {
            var start = EngineHarness.Now.AddDays(-10 * (index + 1));
            var builder = EngineHarness.Session("s" + index, start, runs: 2);
            harness.Journals.Set(builder, EngineHarness.EndOf(builder, start, 2));
        }

        var status = await harness.Rpc.InvokeAsync("statistics.status");

        Assert.AreEqual("needsChoice", status.GetProperty("state").GetString());
        Assert.AreEqual(4, status.GetProperty("sessionsTotal").GetInt32());
        Assert.IsTrue(status.GetProperty("bytesTotal").GetInt64() > 0);
        Assert.AreEqual(20260830, status.GetProperty("oldestDateReached").GetInt32(), "the day of the oldest session");
        Assert.AreEqual(0, harness.Journals.Opens, "nothing is read to tell it");

        // A session that appears is counted when the numbers are asked again after their half minute; before, the listing is not repeated.
        var lists = harness.Journals.Lists;
        await harness.Rpc.InvokeAsync("statistics.status");
        Assert.AreEqual(lists, harness.Journals.Lists);
    }

    [TestMethod]
    public async Task TheControls_ChooseThePauseAndTheResetAnswerWithTheStatusAfterThem()
    {
        await using var query = await QueryHarness.CreateAsync(sessions: 0, chosen: false);
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        var builder = EngineHarness.Session("s1", EngineHarness.Now.AddDays(-2), runs: 2);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, EngineHarness.Now.AddDays(-2), 2));

        var chosen = await harness.Rpc.InvokeAsync("statistics.choose-history", """{"kind":"days","days":90}""");
        Assert.AreEqual("reading", chosen.GetProperty("state").GetString());
        Assert.AreEqual("days:90", chosen.GetProperty("choice").GetString());
        var paused = await harness.Rpc.InvokeAsync("statistics.pause");
        Assert.AreEqual("paused", paused.GetProperty("state").GetString());
        var resumed = await harness.Rpc.InvokeAsync("statistics.resume");
        Assert.AreEqual("reading", resumed.GetProperty("state").GetString());
        var stopped = await harness.Rpc.InvokeAsync("statistics.stop-here");
        Assert.AreEqual("stoppedHere", stopped.GetProperty("state").GetString());
        var forgotten = await harness.Rpc.InvokeAsync("statistics.forget-deleted");
        Assert.AreEqual(0, forgotten.GetProperty("count").GetInt32());
        var reset = await harness.Rpc.InvokeAsync("statistics.reset");
        Assert.AreEqual("needsChoice", reset.GetProperty("state").GetString());
        Assert.IsFalse(reset.TryGetProperty("choice", out _));
        Assert.AreEqual(1, reset.GetProperty("sessionsTotal").GetInt32(), "the card has its numbers again");

        foreach (var bad in new[] { """{"kind":"days","days":0}""", """{"kind":"days"}""", """{"kind":"most"}""", """{"kind":"days","days":9999}""", "{}"})
        {
            var error = await harness.Rpc.FailureAsync("statistics.choose-history", bad);
            Assert.AreEqual("invalid_request", error?.Code, bad);
        }
    }

    [TestMethod]
    public async Task TheContext_GivesTheSpacesWithTheirProjects_AndTheNamesOfTheProjects()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();
        await harness.OpenAsync();

        var context = await harness.Rpc.InvokeAsync("statistics.context");

        var spaces = context.GetProperty("spaces").EnumerateArray().ToDictionary(space => space.GetProperty("id").GetString()!);
        Assert.AreEqual(2, spaces.Count);
        Assert.IsTrue(spaces["default"].GetProperty("isDefault").GetBoolean());
        CollectionAssert.AreEqual(new[] { "project-0", "project-1" }, spaces["default"].GetProperty("projectIds").EnumerateArray().Select(item => item.GetString()).ToArray(), "the default space has every project");
        CollectionAssert.AreEqual(new[] { "project-0" }, spaces["work"].GetProperty("projectIds").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.AreEqual("Work", spaces["work"].GetProperty("name").GetString());
        var projects = context.GetProperty("projects").EnumerateArray().ToDictionary(project => project.GetProperty("id").GetString()!, project => project.GetProperty("name").GetString());
        Assert.AreEqual("Alpha", projects["project-0"]);
        Assert.AreEqual("Beta", projects["project-1"]);
        // The page lays its weeks out from the day the questions use when a request names none.
        Assert.AreEqual(StatisticsQueries.DefaultWeekStart.ToString(), context.GetProperty("weekStart").GetString());
    }

    // ---- when a call cannot be answered ----

    [TestMethod]
    public async Task AQuestionThatCannotBeAnswered_FailsWithAStableCodeAndASentence_NeverTheTextOfAnException()
    {
        await using var query = await QueryHarness.CreateAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        string With(string period, string more = "") => "{\"request\":{\"period\":\"" + period + "\"}" + more + "}";

        var period = await harness.Rpc.FailureAsync("statistics.summary", With("soon"));
        var metric = await harness.Rpc.FailureAsync("statistics.series", With("30d", ",\"metric\":\"colors\""));
        var missing = await harness.Rpc.FailureAsync("statistics.series", With("30d"));
        var sort = await harness.Rpc.FailureAsync("statistics.sessions", With("30d", ",\"sort\":\"loudest\""));
        var session = await harness.Rpc.FailureAsync("statistics.session", """{"id":"no-such-session"}""");
        var noId = await harness.Rpc.FailureAsync("statistics.session", "{}");
        var space = await harness.Rpc.FailureAsync("statistics.summary", """{"request":{"period":"30d","filter":{"space":"nowhere"}}}""");
        var badRequest = await harness.Rpc.FailureAsync("statistics.summary", """{"request":{"period":"30d","frequency":"fortnight"}}""");

        Assert.AreEqual("invalid_request", period?.Code);
        StringAssert.Contains(period!.Message, "'soon' is not a period");
        Assert.IsFalse(period.Message.Contains("(Parameter", StringComparison.Ordinal), period.Message);
        Assert.AreEqual("invalid_request", metric?.Code);
        StringAssert.Contains(metric!.Message, "is not a metric");
        Assert.AreEqual("invalid_request", missing?.Code);
        StringAssert.Contains(missing!.Message, "'metric'");
        Assert.AreEqual("invalid_request", sort?.Code);
        Assert.AreEqual("not_found", session?.Code);
        Assert.AreEqual("invalid_request", noId?.Code);
        Assert.AreEqual("invalid_request", space?.Code);
        Assert.AreEqual("invalid_request", badRequest?.Code);
        foreach (var error in new[] { period, metric, missing, sort, session, noId, space, badRequest })
        {
            Assert.IsTrue(PluginRpcException.IsValidCode(error!.Code));
            Assert.IsFalse(error.Message.Contains('\n') || error.Message.Contains("   at ", StringComparison.Ordinal), "a sentence, not a stack trace");
        }
    }

    [TestMethod]
    public void AnUnexpectedFailure_IsNotTranslated_SoTheHostReportsItAsInternalAndKeepsItsTextInTheLog()
    {
        Assert.IsNull(StatisticsCanvasRpc.Describe(new InvalidOperationException("C:\\Users\\someone\\secret.db is locked")));
        Assert.IsNull(StatisticsCanvasRpc.Describe(new IOException("disk")));
        Assert.IsNull(StatisticsCanvasRpc.Describe(new OperationCanceledException()));
        var known = new PluginRpcException("busy", "Try again.", retryable: true);
        Assert.AreSame(known, StatisticsCanvasRpc.Describe(known));
        Assert.AreEqual("invalid_request", StatisticsCanvasRpc.Describe(new ArgumentException("Nope. (Parameter 'x')"))!.Code);
        Assert.AreEqual("Nope.", StatisticsCanvasRpc.Describe(new ArgumentException("Nope. (Parameter 'x')"))!.Message);
    }

    [TestMethod]
    public async Task WithoutTheEngine_EveryCallSaysTheStatisticsAreNotRunning_AndCanBeTriedAgain()
    {
        await using var store = await StoreHarness.CreateAsync();
        var rpc = new RecordingRpc();
        new StatisticsCanvasRpc(() => null, _ => ValueTask.FromResult("{}"u8.ToArray())).Register(rpc);
        rpc.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = StatisticsCanvasJsonContext.Default };

        foreach (var name in rpc.Names)
        {
            var error = await rpc.FailureAsync(name);
            Assert.AreEqual("unavailable", error?.Code, name);
            Assert.IsTrue(error!.Retryable, name);
        }
    }

    [TestMethod]
    public async Task AResultThatWouldNotFitTheTransport_IsRefusedWithAClearCode()
    {
        await using var query = await QueryHarness.CreateAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        var rpc = new RecordingRpc { JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = StatisticsCanvasJsonContext.Default } };
        new StatisticsCanvasRpc(() => (StatisticsEngine?)harness.Plugin.Statistics, _ => ValueTask.FromResult("{}"u8.ToArray()), maximumResultBytes: 200).Register(rpc);

        var error = await rpc.FailureAsync("statistics.summary", "{\"request\":" + Request + "}");

        Assert.AreEqual("result_too_large", error?.Code);
        StringAssert.Contains(error!.Message, "shorter period");
    }

    [TestMethod]
    public async Task ManyQuestionsAtOnce_AllAnswer_AndLeaveNoneRunning_AndACanceledOneStops()
    {
        await using var query = await QueryHarness.CreateAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);
        await harness.OpenAsync();
        var json = "{\"request\":" + Request + ",\"metric\":\"tokens\",\"group\":\"model\"}";

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => harness.Rpc.InvokeAsync("statistics.series", json)));

        Assert.AreEqual(1, results.Select(result => result.GetRawText()).Distinct().Count(), "the same question gives the same answer");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Rpc.InvokeAsync("statistics.summary", "{\"request\":" + Request + "}", canceled.Token));
        // The gate is free again: a question asked after goes through.
        await harness.Rpc.InvokeAsync("statistics.summary", "{\"request\":" + Request + "}");
    }

    // ---- what the script is told ----

    [TestMethod]
    public async Task TheStatusAndTheDaysThatChange_ReachTheScript_GatheredInABurst_AndNotWhileTheTabIsHidden()
    {
        // The delay of the events is the one of a clock the test moves: no assertion below depends on how fast the machine is.
        var time = new ManualTime(EngineHarness.Now);
        var delay = StatisticsPlugin.EventDelay;
        await using var query = await QueryHarness.CreateAsync(sessions: 0, chosen: false);
        // The engine never starts its loop: the only timers of the clock are the delays of the events.
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store, startDelay: Timeout.InfiniteTimeSpan, time: time);
        await harness.OpenAsync();
        var builder = EngineHarness.Session("s1", EngineHarness.Now.AddDays(-2), runs: 2);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, EngineHarness.Now.AddDays(-2), 2));

        // A burst of controls: one status event, the last state, once the delay has passed and not before.
        await harness.Plugin.Statistics!.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Plugin.Statistics.PauseAsync();
        await harness.Plugin.Statistics.ResumeAsync();
        await harness.Plugin.Statistics.PauseAsync();
        time.Advance(delay - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, harness.Rpc.TakeEvents().Count, "nothing is sent before the delay");
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitAsync(() => harness.Rpc.Events.Count > 0, "the status event");
        time.Advance(delay + delay);
        var events = harness.Rpc.TakeEvents();
        Assert.AreEqual(1, events.Count, "gathered");
        Assert.AreEqual((StatisticsCanvasRpc.EventsName, "status", "paused"), (events[0].Name, events[0].Value.GetProperty("kind").GetString(), events[0].Value.GetProperty("status").GetProperty("state").GetString()));

        // Hidden: the delay passes and nothing goes; shown again: what changed since arrives, after the delay.
        harness.Canvas.SetVisible(false);
        await harness.Plugin.Statistics.ResumeAsync();
        await WaitAsync(() => time.PendingTimers > 0, "the delay of the hidden tab");
        time.Advance(delay);
        Assert.AreEqual(0, time.PendingTimers, "the delay of the hidden tab ended");
        harness.Canvas.SetVisible(true);
        // The delay that ended while the tab was hidden sent nothing; showing the tab starts one, unless the first is still on its way out.
        time.Advance(delay);
        await WaitAsync(() => harness.Rpc.Events.Count > 0, "the status held back");
        var held = harness.Rpc.TakeEvents().Single();
        Assert.AreEqual("reading", held.Value.GetProperty("status").GetProperty("state").GetString());

        // A reset says that every day changed, and what the status is.
        await harness.Plugin.Statistics.ResetAsync();
        time.Advance(delay);
        await WaitAsync(() => harness.Rpc.Events.Count >= 2, "the data event and the status event");
        var reset = harness.Rpc.TakeEvents();
        Assert.AreEqual(2, reset.Count);
        var data = reset.Single(item => item.Value.GetProperty("kind").GetString() == "data").Value.GetProperty("change");
        Assert.AreEqual((10101, 99991231), (data.GetProperty("fromDay").GetInt32(), data.GetProperty("toDay").GetInt32()));
        Assert.AreEqual("needsChoice", reset.Single(item => item.Value.GetProperty("kind").GetString() == "status").Value.GetProperty("status").GetProperty("state").GetString());

        // Closed: the tab is not told any more, and nothing is left listening on the engine or waiting on the clock.
        harness.Canvas.Close();
        await harness.Plugin.Statistics.ChooseHistoryAsync(HistoryChoice.FromToday);
        Assert.AreEqual(0, time.PendingTimers, "a closed tab starts no delay");
        time.Advance(delay + delay);
        Assert.AreEqual(0, harness.Rpc.TakeEvents().Count);
    }
}
