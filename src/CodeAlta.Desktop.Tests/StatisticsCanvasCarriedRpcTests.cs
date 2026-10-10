using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Tests;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The Statistics canvas as a script of the window uses it: the built-in plugin of the window, activated by a real plugin runtime over a database
/// and synthetic journals, opened as a canvas, and called through the service of the page with the frames the script's client writes.
/// </summary>
[TestClass]
public sealed class StatisticsCanvasCarriedRpcTests
{
    private const string Epoch = "epoch-1";
    private const string Request = """{"period":"all","frequency":"month","comparison":"none","filter":{}}""";

    [TestMethod]
    public async Task TheCanvas_IsServedToItsScript_WithTheModuleOfTheApplication_EveryCallAnswersTheQueryJson_AndTheStatusIsPushed()
    {
        await using var fixture = await Fixture.CreateAsync();

        var opened = await fixture.OpenAsync();

        Assert.AreEqual("ok", opened.Status);
        Assert.AreEqual("Statistics", opened.Title);
        Assert.AreEqual("/lib/app/statistics.js", opened.Script, "the module is the one the build of the application emits");
        var listed = fixture.Service.List(new(Epoch)).Canvases.Single(canvas => canvas.PluginKey == fixture.Key);
        Assert.AreEqual(("statistics", "chart-column", "Application"), (listed.Id, listed.Icon, listed.Scope));

        var connection = fixture.Service.RpcOpen(new(Epoch, opened.InstanceId));
        Assert.AreEqual("ok", connection.Status);
        var id = connection.Connection!;

        // The first time: the card is told what exists, without reading it.
        fixture.Journals.Add("old", EngineSession("old", 40));
        fixture.Journals.Add("recent", EngineSession("recent", 3));
        fixture.Send(opened.InstanceId!, id, Invoke("c0", "statistics.status", "{}"));
        var status = (await fixture.FrameAsync(id, frame => Id(frame) == "c0")).GetProperty("value");
        Assert.AreEqual(("needsChoice", 2), (status.GetProperty("state").GetString(), status.GetProperty("sessionsTotal").GetInt32()));
        Assert.IsTrue(status.GetProperty("bytesTotal").GetInt64() > 0);

        // The events of the plugin ride the one event of the connection; the script hears the status while the history is read.
        fixture.Send(opened.InstanceId!, id, $$"""{"neoastra":1,"kind":"subscribe","id":"sub1","event":"{{CanvasRpcEndpoint.EventsName}}"}""");
        await fixture.FrameAsync(id, frame => frame.GetProperty("kind").GetString() == "subscribed");
        fixture.Send(opened.InstanceId!, id, Invoke("c1", "statistics.choose-history", """{"kind":"all"}"""));
        var chosen = (await fixture.FrameAsync(id, frame => Id(frame) == "c1")).GetProperty("value");
        Assert.AreEqual("all", chosen.GetProperty("choice").GetString());
        var done = await fixture.FrameAsync(id, frame => frame.GetProperty("kind").GetString() == "event" && frame.GetProperty("value").GetProperty("name").GetString() == "statistics.events"
            && frame.GetProperty("value").GetProperty("value") is { ValueKind: JsonValueKind.Object } value && value.GetProperty("kind").GetString() == "status" && value.GetProperty("status").GetProperty("state").GetString() == "done");
        Assert.AreEqual(2, done.GetProperty("value").GetProperty("value").GetProperty("status").GetProperty("sessionsDone").GetInt32());

        // Every question answers exactly what the query service says, through the frames of the carrier.
        var queries = fixture.Plugin.Statistics!.Queries;
        var request = StatisticsJson.ParseRequest(Request);
        string Wrap(string? more = null) => "{\"request\":" + Request + (more is null ? string.Empty : "," + more) + "}";
        var calls = new (string Name, string Args, Func<Task<string>> Expected)[]
        {
            ("summary", Wrap(), async () => StatisticsJson.Serialize(await queries.SummaryAsync(request))),
            ("series", Wrap("\"metric\":\"tokens\",\"group\":\"model\""), async () => StatisticsJson.Serialize(await queries.SeriesAsync(request, "tokens", "model"))),
            ("top", Wrap("\"kind\":\"models\",\"by\":\"tokens\""), async () => StatisticsJson.Serialize(await queries.TopAsync(request, "models", "tokens"))),
            ("tools", Wrap(), async () => StatisticsJson.Serialize(await queries.ToolsAsync(request))),
            ("models", Wrap(), async () => StatisticsJson.Serialize(await queries.ModelsAsync(request))),
            ("projects", Wrap(), async () => StatisticsJson.Serialize(await queries.ProjectsAsync(request))),
            ("sessions", Wrap("\"sort\":\"recent\""), async () => StatisticsJson.Serialize(await queries.SessionsAsync(request, "recent"))),
            ("session", """{"id":"recent","withChildren":false}""", async () => StatisticsJson.Serialize((await queries.SessionAsync("recent", false))!)),
            ("distribution", Wrap("\"measure\":\"run-duration\",\"subject\":null"), async () => StatisticsJson.Serialize(await queries.DistributionAsync(request, "run-duration", null))),
            ("calendar", Wrap(), async () => StatisticsJson.Serialize(await queries.CalendarAsync(request))),
            ("week-hour", Wrap(), async () => StatisticsJson.Serialize(await queries.WeekHourAsync(request))),
            ("records", Wrap(), async () => StatisticsJson.Serialize(await queries.RecordsAsync(request))),
            ("health", Wrap(), async () => StatisticsJson.Serialize(await queries.HealthAsync(request))),
            ("details", Wrap("\"list\":\"skill\""), async () => StatisticsJson.Serialize(await queries.DetailsAsync(request, "skill"))),
            ("runs", Wrap("\"sort\":\"recent\""), async () => StatisticsJson.Serialize(await queries.RunsAsync(request, "recent"))),
        };
        foreach (var (name, args, expected) in calls)
        {
            var call = "q-" + name;
            fixture.Send(opened.InstanceId!, id, Invoke(call, "statistics." + name, args));
            var answer = await fixture.FrameAsync(id, frame => Id(frame) == call);
            Assert.IsTrue(answer.GetProperty("ok").GetBoolean(), name + ": " + answer);
            using var wanted = JsonDocument.Parse(await expected());
            Assert.IsTrue(JsonElement.DeepEquals(wanted.RootElement, answer.GetProperty("value")), name + " is the JSON of the query service");
        }

        // The numbers are real: the two sessions were read.
        fixture.Send(opened.InstanceId!, id, Invoke("s1", "statistics.summary", Wrap()));
        var summary = (await fixture.FrameAsync(id, frame => Id(frame) == "s1")).GetProperty("value");
        Assert.IsTrue(summary.GetProperty("tiles").EnumerateArray().Single(tile => tile.GetProperty("id").GetString() == "sessions").GetProperty("value").GetDouble() >= 2);
        Assert.IsFalse(summary.GetRawText().Contains("null", StringComparison.Ordinal), "nulls are left out, as in alta statistics");
    }

    [TestMethod]
    public async Task TheCalls_RefuseWhatCannotBeAnswered_WithTheCodesOfTheTransportAndOfThePlugin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        var id = fixture.Service.RpcOpen(new(Epoch, opened.InstanceId)).Connection!;
        fixture.Journals.Add("one", EngineSession("one", 1));

        fixture.Send(opened.InstanceId!, id, Invoke("a", "statistics.summary", """{"request":{"period":"soon"}}"""), Invoke("b", "statistics.series", """{"request":{"period":"30d"},"metric":"colors"}"""),
            Invoke("c", "statistics.session", """{"id":"nobody"}"""), Invoke("d", "statistics.nothing", "{}"), Invoke("e", "statistics.summary", """{"request":42}"""),
            Invoke("f", "statistics.choose-history", """{"kind":"days","days":0}"""), Invoke("g", "statistics.series", """{"request":{"period":"30d"}}"""),
            Invoke("h", "statistics.weekHour", "{}"));
        var codes = new Dictionary<string, (string Code, string Message)>();
        while (codes.Count < 8)
        {
            var answer = await fixture.FrameAsync(id, frame => frame.GetProperty("kind").GetString() == "result" && !frame.GetProperty("ok").GetBoolean());
            var error = answer.GetProperty("error");
            codes[answer.GetProperty("id").GetString()!] = (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
        }

        Assert.AreEqual("invalid_request", codes["a"].Code);
        StringAssert.Contains(codes["a"].Message, "'soon' is not a period");
        Assert.AreEqual("invalid_request", codes["b"].Code);
        Assert.AreEqual("not_found", codes["c"].Code);
        Assert.AreEqual("command_not_found", codes["d"].Code);
        Assert.AreEqual("invalid_request", codes["e"].Code, "a request that is not an object is refused");
        Assert.AreEqual("invalid_request", codes["f"].Code);
        Assert.AreEqual("invalid_request", codes["g"].Code);
        Assert.AreEqual("command_not_found", codes["h"].Code, "names are lowercase: the camel case spelling is not a call");
        Assert.IsFalse(codes.Values.Any(error => error.Message.Contains("   at ", StringComparison.Ordinal) || error.Message.Contains("Exception", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ClosingTheTab_EndsTheConnection_AndStopsTheEventsOfThatTab()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        var id = fixture.Service.RpcOpen(new(Epoch, opened.InstanceId)).Connection!;
        fixture.Journals.Add("one", EngineSession("one", 1));
        fixture.Send(opened.InstanceId!, id, Invoke("w", "statistics.status", "{}"));
        await fixture.FrameAsync(id, frame => Id(frame) == "w");

        var closed = await fixture.Service.CloseAsync(new(Epoch, opened.InstanceId), default);

        Assert.AreEqual("ok", closed.Status);
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, opened.InstanceId, id, [Invoke("late", "statistics.status", "{}")])).Status);
        // The plugin stays: the same canvas opens again, with its state.
        var again = await fixture.OpenAsync();
        Assert.AreEqual("ok", again.Status);
        Assert.AreEqual(opened.InstanceId, again.InstanceId);
    }

    [TestMethod]
    public async Task TheLineOfASessionMenu_IsGivenForTheSessionOfARow_AndItsCommandAsksForTheCanvasOfThatSession()
    {
        // What the menu of a session row does, for a row that is not selected: the page asks for the lines of that row, then runs the
        // command of the line for that row. Nothing here says which project or session is selected: the host never needs it.
        await using var fixture = await Fixture.CreateAsync();
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = fixture.Global });
        var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName);
        var ui = new PluginUiService(projects, fixture.Runtime, fixture.Ui, Epoch, fixture.Broker);
        try
        {
            // A chat: a session of no project.
            var chat = await ui.ButtonsAsync(new(Epoch, "SessionMenu", "work", null, "chat-of-the-row"), default);
            Assert.AreEqual("ok", chat.Status);
            var line = chat.Buttons.Single();
            Assert.AreEqual(("statistics-session", "SessionMenu", "Statistics of this session", (string?)null, false, false), (line.ButtonId, line.Place, line.Label, line.Canvas, line.Disabled, line.Hidden));
            Assert.IsNotNull(line.CommandId);
            // The command is the hidden one: the palette and the help do not list it.
            var command = (await ui.ContributionsAsync(new(Epoch, null), default)).Commands.Single(candidate => candidate.Id == line.CommandId);
            Assert.AreEqual(("statistics-session", false, false, true, false), (command.Name, command.Palette, command.Help, command.NeedsSession, command.NeedsProject));

            Assert.AreEqual("started", (await ui.InvokeCommandAsync(new(Epoch, line.CommandId, null, "chat-of-the-row", false, null, "work"), default)).Status);
            var opened = await fixture.NextAsync(static value => value.Kind == "open");
            Assert.AreEqual((fixture.Key, "statistics", "work", "session:chat-of-the-row", (string?)null, (string?)null, true),
                (opened.PluginKey, opened.CanvasId, opened.SpaceId, opened.Key, opened.ProjectId, opened.SessionId, opened.Focus));

            // A session of a project: the line is asked and run for that project and that session.
            var row = (await ui.ButtonsAsync(new(Epoch, "SessionMenu", "work", project.Id, "session-of-the-project"), default)).Buttons.Single();
            Assert.IsFalse(row.Disabled);
            Assert.AreEqual("started", (await ui.InvokeCommandAsync(new(Epoch, row.CommandId, project.Id, "session-of-the-project", false, null, "work"), default)).Status);
            var second = await fixture.NextAsync(static value => value.Kind == "open");
            Assert.AreEqual(("statistics", "session:session-of-the-project", (string?)null, (string?)null), (second.CanvasId, second.Key, second.ProjectId, second.SessionId),
                "the canvas of the application, told apart by the session of its key");
            Assert.AreNotEqual(opened.InstanceId, second.InstanceId, "a tab for each session");

            // The canvas the window then opens is titled for that session, and is the same one when it is asked again.
            var tab = await fixture.Service.OpenAsync(new(Epoch, fixture.Key, "statistics", "work", null, null, "session:chat-of-the-row", true), default);
            Assert.AreEqual(("ok", "Statistics: chat-of-", opened.InstanceId), (tab.Status, tab.Title, tab.InstanceId));

            // Without a session the command does not run: never the canvas of every session in its place.
            Assert.AreEqual("unavailable", (await ui.InvokeCommandAsync(new(Epoch, line.CommandId, project.Id, null, false, null, "work"), default)).Status);
            Assert.IsTrue((await ui.ButtonsAsync(new(Epoch, "SessionMenu", "work", project.Id, null), default)).Buttons.Single().Disabled);
        }
        finally
        {
            await ui.CloseAsync();
        }
    }

    [TestMethod]
    public async Task APageThatStartsWatching_IsToldToAskAgainForTheTabsThatWait()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        // The page restored its tab and asked before it watched: the tab waits. When the page watches, the first thing it hears is that plugins are there.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var value in fixture.Service.WatchAsync(new(Epoch), timeout.Token))
        {
            Assert.AreEqual("plugins", value.Kind);
            break;
        }
    }

    [TestMethod]
    public async Task APageThatWatchesBeforeTheHostHasPlugins_IsToldWhenTheyAreThere()
    {
        using var broker = new DesktopCanvases(new DesktopPluginUi());
        var service = new CanvasesService(broker, Epoch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var watch = service.WatchAsync(new(Epoch), timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = watch.MoveNextAsync().AsTask();
        SpinWait.SpinUntil(() => broker.WatchGeneration > 0, TimeSpan.FromSeconds(5));

        broker.Attach(new PluginRuntimeManager(), () => null);

        Assert.IsTrue(await first);
        Assert.AreEqual("plugins", watch.Current.Kind);
    }

    [TestMethod]
    public void ThePluginUiOfAPlugin_TellsThePageThatItsButtonsChanged()
    {
        var ui = new DesktopPluginUi();
        var heard = new List<string>();
        using var watching = ui.Watch(value => heard.Add(value.Kind));

        ui.ForPlugin("builtin:statistics").InvalidateButtons();

        CollectionAssert.AreEqual(new[] { "buttons" }, heard, "the ring and the dot of a plugin follow its state while the window runs");
    }

    private static string Invoke(string id, string command, string args)
        => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{args}}}""";

    private static string? Id(JsonElement frame) => frame.TryGetProperty("id", out var id) ? id.GetString() : null;

    private static byte[] EngineSession(string id, int daysAgo)
    {
        var start = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddHours(-1);
        var builder = new JournalBuilder(id, "codex");
        builder.Header(start).State(start);
        for (var run = 0; run < 2; run++)
        {
            var at = start.AddMinutes(run * 10);
            var runId = $"{id}-run{run}";
            builder.User(at.AddSeconds(1), runId, "a prompt of some words").Usage(at.AddSeconds(5), runId, input: 1000 * (run + 1), output: 100 + run).Assistant(at.AddSeconds(10), runId).Idle(at.AddSeconds(20), runId);
        }

        return builder.ToBytes();
    }

    /// <summary>The journals of a session store, in memory.</summary>
    private sealed class MemoryJournals : ISessionJournalCatalog
    {
        private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset At)> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _gate = new();

        public void Add(string id, byte[] bytes)
        {
            lock (_gate) _files[id] = (bytes, DateTimeOffset.UtcNow.AddDays(-0.5));
        }

        public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SessionJournalFile[] files;
            lock (_gate) files = [.. _files.Select(pair => new SessionJournalFile(pair.Key, "memory://" + pair.Key, pair.Value.Bytes.Length, pair.Value.At)).OrderByDescending(file => file.LastWriteUtc)];
            foreach (var file in files) yield return file;
            await Task.CompletedTask;
        }

        public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult(_files.TryGetValue(sessionId, out var file) ? new SessionJournalFile(sessionId, "memory://" + sessionId, file.Bytes.Length, file.At) : null);
        }

        public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(sessionId, out var file)) return ValueTask.FromResult<Stream?>(null);
                var stream = new MemoryStream(file.Bytes, writable: false) { Position = offset };
                return ValueTask.FromResult<Stream?>(stream);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly DesktopCanvases _broker;
        private readonly PluginRuntimeManager _runtime;
        private readonly CancellationTokenSource _watching = new();
        private readonly Channel<CanvasEvent> _events = Channel.CreateUnbounded<CanvasEvent>();
        private readonly List<CanvasEvent> _kept = [];
        private readonly List<(string Connection, JsonElement Frame)> _frames = [];
        private Task _pump = Task.CompletedTask;

        private Fixture(string root, DesktopCanvases broker, PluginRuntimeManager runtime, StatisticsPlugin plugin, MemoryJournals journals, DesktopPluginUi ui)
        {
            _root = root;
            _broker = broker;
            _runtime = runtime;
            Ui = ui;
            Plugin = plugin;
            Journals = journals;
            Service = new CanvasesService(broker, Epoch);
        }

        public StatisticsPlugin Plugin { get; }

        public MemoryJournals Journals { get; }

        public CanvasesService Service { get; }

        public string Key => _runtime.ActivePlugins.Single().Descriptor.RuntimeKey;

        /// <summary>The folder of the fixture, and the global root of its catalog.</summary>
        public string Root => _root;

        public string Global => Path.Combine(_root, "home", ".alta");

        public PluginRuntimeManager Runtime => _runtime;

        public DesktopCanvases Broker => _broker;

        public DesktopPluginUi Ui { get; }

        public static async Task<Fixture> CreateAsync(bool watch = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-statistics-canvas-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var journals = new MemoryJournals();
            var plugin = new StatisticsPlugin(journals, TimeSpan.Zero, TimeSpan.Zero);
            var ui = new DesktopPluginUi();
            var broker = new DesktopCanvases(ui);
            ui.Modules = broker.Modules;
            var runtime = new PluginRuntimeManager();
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global,
                StateRoot = Path.Combine(root, "state"),
                Frontend = PluginFrontends.Desktop,
                IsHeadless = true,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker),
                BuiltIns = [new BuiltInPluginDefinition { Id = "statistics", DisplayName = "Statistics", PluginType = typeof(StatisticsPlugin), Factory = () => plugin }],
            });
            broker.Attach(runtime, () => "work");
            var fixture = new Fixture(root, broker, runtime, plugin, journals, ui);
            if (watch) fixture.StartWatching();
            return fixture;
        }

        public Task<CanvasOpenResponse> OpenAsync()
            => Service.OpenAsync(new(Epoch, Key, "statistics", "work", null, null, null, true), default);

        public void Send(string instanceId, string connection, params string[] frames)
            => Assert.AreEqual("ok", Service.RpcSend(new(Epoch, instanceId, connection, frames)).Status);

        /// <summary>The next frame of a connection that a test asks for; the others wait here, in order.</summary>
        public async Task<JsonElement> FrameAsync(string connection, Func<JsonElement, bool> wanted)
        {
            while (true)
            {
                var index = _frames.FindIndex(candidate => candidate.Connection == connection && wanted(candidate.Frame));
                if (index >= 0)
                {
                    var found = _frames[index].Frame;
                    _frames.RemoveAt(index);
                    return found;
                }

                var carried = await NextAsync(value => value.Kind == "rpc");
                foreach (var text in carried.Frames!) _frames.Add((carried.Connection!, JsonDocument.Parse(text).RootElement.Clone()));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watching.Cancel();
            await _pump;
            _broker.Dispose();
            await _runtime.DisposeAsync();
            _watching.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }

        private void StartWatching()
        {
            var generation = _broker.WatchGeneration;
            _pump = Task.Run(async () =>
            {
                try { await foreach (var value in Service.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The test is over. */ }
            });
            SpinWait.SpinUntil(() => _broker.WatchGeneration > generation, TimeSpan.FromSeconds(5));
        }

        public async Task<CanvasEvent> NextAsync(Func<CanvasEvent, bool> wanted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var value in _kept.ToArray())
            {
                if (!wanted(value)) continue;
                _kept.Remove(value);
                return value;
            }

            while (true)
            {
                var value = await _events.Reader.ReadAsync(timeout.Token);
                if (wanted(value)) return value;
                _kept.Add(value);
            }
        }
    }
}
