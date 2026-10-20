using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta window</c>: the windows of the desktop app, their monitors and their tabs, as agents use them.</summary>
[TestClass]
public sealed class AltaWindowCommandsTests
{
    private static readonly string[] Leaves = ["list", "show", "monitors", "open", "focus", "close", "tab move", "place", "set"];

    // The commands are run by a caller that belongs to no session, as the MCP server of the window does.
    private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

    [TestMethod]
    public async Task WindowGroup_ExistsWithTheService_AndNotWithout()
    {
        var none = Fixture.Create(windows: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("window", "list")).ExitCode);
        Assert.IsFalse((await none.RunAsync("tool", "list")).Stdout.Contains("window list", StringComparison.Ordinal));
        Assert.IsFalse((await none.RunAsync("--help")).Stdout.Contains("window", StringComparison.Ordinal));

        var fixture = Fixture.Create();
        foreach (var leaf in Leaves)
        {
            var help = await fixture.RunAsync([.. "window".Split(' '), .. leaf.Split(' '), "--help"]);
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, leaf);
            Assert.IsTrue(help.IsHelp, leaf);
        }

        StringAssert.Contains((await fixture.RunAsync("window", "--help")).Stdout, "The main window has the activity rail");
        var paths = fixture.Single(await fixture.OkAsync("tool", "list"), "alta.tool.paths");
        var all = paths.GetProperty("paths").EnumerateArray().Select(static path => path.GetString()).ToArray();
        var mutating = paths.GetProperty("mutating").EnumerateArray().Select(static path => path.GetString()).Where(static path => path!.StartsWith("window ", StringComparison.Ordinal)).ToArray();
        foreach (var leaf in Leaves) CollectionAssert.Contains(all, $"window {leaf}");
        CollectionAssert.AreEqual(new[] { "window set" }, mutating, "Every command but `set` only changes what the windows show.");
        Assert.IsFalse(paths.GetProperty("disruptive").EnumerateArray().Any(static path => path.GetString()!.StartsWith("window ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task List_PrintsTheWindows_AndWithTabsTheirTabs()
    {
        var fixture = Fixture.Create();
        var records = Records(await fixture.OkAsync("window", "list"));
        var items = records.Where(static line => Type(line) == "alta.window.item").ToArray();
        CollectionAssert.AreEqual(new[] { "main", "w-alpha1", "w-alpha2", "w-beta" }, items.Select(static item => item.GetProperty("id").GetString()).ToArray());
        Assert.IsFalse(records.Any(static line => Type(line) == "alta.window.tab"), "Without --tabs, no tab.");

        var main = items[0];
        Assert.IsTrue(main.GetProperty("main").GetBoolean());
        Assert.AreEqual("maximized", main.GetProperty("state").GetString());
        Assert.IsTrue(main.GetProperty("focused").GetBoolean());
        Assert.AreEqual("m-1", main.GetProperty("monitor").GetProperty("id").GetString());
        Assert.IsTrue(main.GetProperty("monitor").GetProperty("primary").GetBoolean());
        Assert.AreEqual(1920, main.GetProperty("bounds").GetProperty("width").GetInt32());
        Assert.AreEqual(2, main.GetProperty("tabCount").GetInt32());
        Assert.IsFalse(main.TryGetProperty("icon", out _) || main.TryGetProperty("pinnedMonitor", out _) || main.TryGetProperty("pinnedConnected", out _),
            "What a window does not have is left out of its record.");

        var review = items[1];
        Assert.AreEqual(("Review", "eye", "#2d72d2", true), (review.GetProperty("name").GetString(), review.GetProperty("icon").GetString(), review.GetProperty("color").GetString(), review.GetProperty("saved").GetBoolean()));
        Assert.AreEqual("m-2", review.GetProperty("pinnedMonitor").GetProperty("id").GetString());
        Assert.IsTrue(review.GetProperty("pinnedConnected").GetBoolean());
        Assert.IsFalse(review.GetProperty("onCurrentDesktop").GetBoolean());

        var docs = items[3];
        Assert.IsFalse(docs.GetProperty("open").GetBoolean());
        Assert.IsFalse(docs.GetProperty("pinnedConnected").GetBoolean());
        Assert.IsFalse(docs.TryGetProperty("state", out _) || docs.TryGetProperty("bounds", out _) || docs.TryGetProperty("monitor", out _), "A closed window is in no state and at no place.");

        var summary = fixture.Single(await fixture.OkAsync("window", "list"), "alta.window.summary");
        Assert.AreEqual((4, 3, 4), (summary.GetProperty("windows").GetInt32(), summary.GetProperty("open").GetInt32(), summary.GetProperty("tabs").GetInt32()));

        var tabs = Records(await fixture.OkAsync("window", "list", "--tabs")).Where(static line => Type(line) == "alta.window.tab").ToArray();
        Assert.AreEqual(4, tabs.Length);
        Assert.AreEqual(("main", "session:s1", "session", true), (tabs[0].GetProperty("windowId").GetString(), tabs[0].GetProperty("tab").GetString(), tabs[0].GetProperty("kind").GetString(), tabs[0].GetProperty("active").GetBoolean()));
        Assert.AreEqual(("w-alpha2", "terminal:t1"), (tabs[3].GetProperty("windowId").GetString(), tabs[3].GetProperty("tab").GetString()));
    }

    [TestMethod]
    public async Task Show_PrintsAWindowWithItsTabs_ClosedSavedOnesToo()
    {
        var fixture = Fixture.Create();
        var detail = fixture.Single(await fixture.OkAsync("window", "show", "main"), "alta.window.detail");
        var tabs = detail.GetProperty("tabs").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "session:s1", "editor:p1" }, tabs.Select(static tab => tab.GetProperty("tab").GetString()).ToArray());
        Assert.AreEqual("editor", tabs[1].GetProperty("kind").GetString());

        var docs = fixture.Single(await fixture.OkAsync("window", "show", "Docs"), "alta.window.detail");
        Assert.AreEqual("w-beta", docs.GetProperty("id").GetString());
        Assert.AreEqual(0, docs.GetProperty("tabs").GetArrayLength());
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalid", "window", "show");
    }

    [TestMethod]
    public async Task Monitors_ListsTheConnectedMonitors_WithTheirNumbers()
    {
        var fixture = Fixture.Create();
        var records = Records(await fixture.OkAsync("window", "monitors"));
        var monitors = records.Where(static line => Type(line) == "alta.window.monitor").ToArray();
        Assert.AreEqual(2, monitors.Length);
        var primary = monitors[0];
        Assert.AreEqual((1, "m-1", "DELL U2720Q", true, true), (primary.GetProperty("number").GetInt32(), primary.GetProperty("id").GetString(), primary.GetProperty("name").GetString(),
            primary.GetProperty("primary").GetBoolean(), primary.GetProperty("idPersistent").GetBoolean()));
        Assert.AreEqual(1040, primary.GetProperty("workArea").GetProperty("height").GetInt32());
        Assert.AreEqual(1080, primary.GetProperty("bounds").GetProperty("height").GetInt32());
        Assert.AreEqual(1.5, monitors[1].GetProperty("scale").GetDouble());
        Assert.AreEqual(2, records.Single(static line => Type(line) == "alta.window.monitors").GetProperty("count").GetInt32());
    }

    [TestMethod]
    public async Task Windows_AreNamedByIdMainNameOrStartOfId()
    {
        var fixture = Fixture.Create();
        Assert.AreEqual("w-alpha1", await ShownIdAsync("w-alpha1"));
        Assert.AreEqual("w-alpha1", await ShownIdAsync("W-ALPHA1"));
        Assert.AreEqual("main", await ShownIdAsync("main"));
        Assert.AreEqual("main", await ShownIdAsync("Main"));
        Assert.AreEqual("w-alpha1", await ShownIdAsync("review"), "A name, whatever its case.");
        Assert.AreEqual("w-beta", await ShownIdAsync("w-b"), "The start of an id that only one window has.");

        var ambiguous = await fixture.FailsAsync(AltaExitCodes.Usage, "window.ambiguous", "window", "show", "w-alpha");
        StringAssert.Contains(ambiguous.GetProperty("message").GetString(), "w-alpha1 (Review)");
        StringAssert.Contains(ambiguous.GetProperty("message").GetString(), "w-alpha2");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notFound", "window", "show", "nowhere");

        async Task<string?> ShownIdAsync(string reference)
            => fixture.Single(await fixture.OkAsync("window", "show", reference), "alta.window.detail").GetProperty("id").GetString();
    }

    [TestMethod]
    public async Task Monitors_AreNamedByIdNumberNameOrPrimary_AndOnlyConnectedOnesAreUsed()
    {
        var fixture = Fixture.Create();
        foreach (var (reference, expected) in new[] { ("m-2", "m-2"), ("M-1", "m-1"), ("2", "m-2"), ("1", "m-1"), ("primary", "m-1"), ("lg hdr", "m-2") })
        {
            fixture.Single(await fixture.OkAsync("window", "place", "Review", "--monitor", reference), "alta.window.placed");
            Assert.AreEqual(expected, fixture.Windows.Placed[^1].Placement.MonitorId, reference);
        }

        var count = fixture.Windows.Placed.Count;
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "place", "Review", "--monitor", "3");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "place", "Review", "--monitor", "m-3");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "open", "--monitor", "Old monitor");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "set", "Review", "--pin-monitor", "m-3");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "tab", "move", "session:s1", "--to", "new", "--monitor", "0");
        Assert.AreEqual(count, fixture.Windows.Placed.Count, "No window is sent where no monitor is.");
        Assert.AreEqual(0, fixture.Windows.Opened.Count + fixture.Windows.Updated.Count + fixture.Windows.Moved.Count);

        // A monitor the window says it lost on the way is refused the same way.
        fixture.Windows.Status = "monitor_not_found";
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "place", "Review", "--monitor", "2");
        fixture.Windows.Status = "display_not_found";
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.monitorNotFound", "window", "place", "Review", "--monitor", "2");
    }

    [TestMethod]
    public async Task TabMove_CurrentIsTheTabOfTheCallingSession()
    {
        var fixture = Fixture.Create();
        var caller = new AltaCallerIdentity { Kind = "session", SourceSessionId = "s7" };
        foreach (var current in new[] { "current", "session:current" })
        {
            var moved = fixture.Single(await fixture.OkAsync(["window", "tab", "move", current, "--to", "new"], caller), "alta.window.tab.moved");
            Assert.AreEqual(("session:s7", "w-new", true), (moved.GetProperty("tab").GetString(), moved.GetProperty("windowId").GetString(), moved.GetProperty("newWindow").GetBoolean()));
            Assert.AreEqual(("session:s7", (string?)null, (string?)null), fixture.Windows.Moved[^1]);
        }

        // Without a calling session there is no current tab.
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingSession", "window", "tab", "move", "current", "--to", "new");
        Assert.AreEqual(2, fixture.Windows.Moved.Count);
    }

    [TestMethod]
    public async Task TabMove_GoesToAWindow_OrANewOneOnAMonitor()
    {
        var fixture = Fixture.Create();
        var moved = fixture.Single(await fixture.OkAsync("window", "tab", "move", "editor:p1", "--to", "Review"), "alta.window.tab.moved");
        Assert.AreEqual(("editor:p1", "w-alpha1", false), (moved.GetProperty("tab").GetString(), moved.GetProperty("windowId").GetString(), moved.GetProperty("newWindow").GetBoolean()));
        Assert.AreEqual(("editor:p1", "w-alpha1", (string?)null), fixture.Windows.Moved[^1]);

        fixture.Single(await fixture.OkAsync("window", "tab", "move", "session:s2", "--to", "NEW", "--monitor", "2"), "alta.window.tab.moved");
        Assert.AreEqual(("session:s2", (string?)null, "m-2"), fixture.Windows.Moved[^1]);

        // A saved window that is closed opens again, on the monitor named; an open one is where it is.
        fixture.Single(await fixture.OkAsync("window", "tab", "move", "session:s2", "--to", "Docs", "--monitor", "primary"), "alta.window.tab.moved");
        Assert.AreEqual(("session:s2", "w-beta", "m-1"), fixture.Windows.Moved[^1]);
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.optionConflict", "window", "tab", "move", "session:s2", "--to", "Review", "--monitor", "2");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingTarget", "window", "tab", "move", "session:s2");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notFound", "window", "tab", "move", "session:s2", "--to", "nowhere");
        Assert.AreEqual(3, fixture.Windows.Moved.Count);

        fixture.Windows.Status = "other_space";
        StringAssert.Contains((await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.tabNotMovable", "window", "tab", "move", "session:s2", "--to", "main")).GetProperty("message").GetString(),
            "another space");
        fixture.Windows.Status = "not_movable";
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.tabNotMovable", "window", "tab", "move", "session:s2", "--to", "main");
        fixture.Windows.Status = "unsaved";
        StringAssert.Contains((await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.tabNotMovable", "window", "tab", "move", "editor:p1", "--to", "main")).GetProperty("message").GetString(),
            "unsaved edits");
        fixture.Windows.Status = "not_found";
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notFound", "window", "tab", "move", "session:nope", "--to", "main");
        fixture.Windows.Status = "timeout";
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "window", "tab", "move", "session:s2", "--to", "main");
    }

    [TestMethod]
    public async Task Open_ANewWindow_WithItsNameIconColorMonitorAndTabs()
    {
        var fixture = Fixture.Create();
        var caller = new AltaCallerIdentity { Kind = "session", SourceSessionId = "s7" };
        var opened = fixture.Single(await fixture.OkAsync(
            ["window", "open", "--name", " Code review ", "--icon", "eye", "--color", "#abc", "--monitor", "2", "--tab", "current", "--tab", "editor:p1", "--tab", "session:s7"], caller), "alta.window.opened");
        var request = fixture.Windows.Opened.Single();
        Assert.AreEqual(("Code review", "eye", "#abc", "m-2", (string?)null), (request.Name, request.Icon, request.Color, request.MonitorId, request.SavedWindowId));
        CollectionAssert.AreEqual(new[] { "session:s7", "editor:p1" }, request.Tabs.ToArray(), "`current` is the tab of the calling session, named once.");
        Assert.AreEqual(("w-new", "Code review"), (opened.GetProperty("id").GetString(), opened.GetProperty("name").GetString()));
        Assert.AreEqual(2, opened.GetProperty("tabs").GetArrayLength());
        Assert.IsFalse(opened.TryGetProperty("notMoved", out _), "Every tab moved.");

        // A tab that does not move is said, with why: the window opens all the same.
        fixture.Windows.Unmovable.Add("editor:p1");
        var partly = fixture.Single(await fixture.OkAsync(["window", "open", "--tab", "session:s2", "--tab", "editor:p1"], caller), "alta.window.opened");
        Assert.AreEqual(1, partly.GetProperty("tabs").GetArrayLength());
        var notMoved = partly.GetProperty("notMoved").EnumerateArray().Single();
        Assert.AreEqual(("editor:p1", "unsaved"), (notMoved.GetProperty("tab").GetString(), notMoved.GetProperty("status").GetString()));
        fixture.Windows.Opened.RemoveAt(1);

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidIcon", "window", "open", "--icon", "Big Eye");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidColor", "window", "open", "--color", "red");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidName", "window", "open", "--name", new string('x', 65));
        Assert.AreEqual(1, fixture.Windows.Opened.Count);

        fixture.Windows.Status = "unavailable";
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "window", "open");
    }

    [TestMethod]
    public async Task Open_Saved_OpensAClosedSavedWindowAgain()
    {
        var fixture = Fixture.Create();
        fixture.Single(await fixture.OkAsync("window", "open", "--saved", "docs", "--tab", "session:s2"), "alta.window.opened");
        var request = fixture.Windows.Opened.Single();
        Assert.AreEqual("w-beta", request.SavedWindowId);
        CollectionAssert.AreEqual(new[] { "session:s2" }, request.Tabs.ToArray());

        StringAssert.Contains((await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.alreadyOpen", "window", "open", "--saved", "Review")).GetProperty("message").GetString(),
            "alta window focus w-alpha1");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.optionConflict", "window", "open", "--saved", "Docs", "--name", "Other");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notFound", "window", "open", "--saved", "nowhere");
        Assert.AreEqual(1, fixture.Windows.Opened.Count);
    }

    [TestMethod]
    public async Task Focus_Close_AndPlace_NeedAnOpenWindow_AndCloseRefusesTheMainOne()
    {
        var fixture = Fixture.Create();
        var focused = fixture.Single(await fixture.OkAsync("window", "focus", "Review"), "alta.window.focused");
        Assert.AreEqual("w-alpha1", focused.GetProperty("id").GetString());
        Assert.AreEqual("w-alpha1", fixture.Windows.Focused.Single());

        var notOpen = await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notOpen", "window", "focus", "Docs");
        StringAssert.Contains(notOpen.GetProperty("message").GetString(), "alta window open --saved w-beta");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notOpen", "window", "close", "Docs");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notOpen", "window", "place", "Docs", "--state", "normal");

        await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.mainNotClosable", "window", "close", "main");
        Assert.AreEqual(0, fixture.Windows.Closed.Count, "The main window is never asked to close.");
        fixture.Windows.Status = "main";
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.mainNotClosable", "window", "close", "Review");
        fixture.Windows.Status = "ok";

        var closed = fixture.Single(await fixture.OkAsync("window", "close", "Review"), "alta.window.closed");
        Assert.AreEqual(("w-alpha1", "Review", true, 1), (closed.GetProperty("id").GetString(), closed.GetProperty("name").GetString(), closed.GetProperty("saved").GetBoolean(), closed.GetProperty("tabsClosed").GetInt32()));
        CollectionAssert.AreEqual(new[] { "w-alpha1" }, fixture.Windows.Closed);
    }

    [TestMethod]
    public async Task Place_ParsesTheStateTheBoundsAndTheDesktop()
    {
        var fixture = Fixture.Create();
        fixture.Single(await fixture.OkAsync("window", "place", "main", "--state", "Maximized", "--bounds", "-1920, 0, 800,600", "--desktop", "current"), "alta.window.placed");
        var (id, placement) = fixture.Windows.Placed.Single();
        Assert.AreEqual("main", id);
        Assert.AreEqual(new AltaWindowPlacement { State = "maximized", Bounds = new AltaRect(-1920, 0, 800, 600), CurrentDesktop = true }, placement);

        foreach (var bounds in new[] { "1,2,3", "a,b,c,d", "0,0,0,10", "0,0,10,-1", "1,2,3,4,5", "" })
        {
            await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidBounds", "window", "place", "main", "--bounds", bounds);
        }

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidState", "window", "place", "main", "--state", "big");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidDesktop", "window", "place", "main", "--desktop", "2");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingPlacement", "window", "place", "main");
        Assert.AreEqual(1, fixture.Windows.Placed.Count);

        fixture.Windows.Status = "not_supported";
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "window.notSupported", "window", "place", "main", "--desktop", "current");
    }

    [TestMethod]
    public async Task Set_ValidatesTheIconAndTheColor_AndEmptyTakesAValueAway()
    {
        var fixture = Fixture.Create();
        var updated = fixture.Single(await fixture.OkAsync("window", "set", "Review", "--name", "Code review", "--icon", "eye", "--color", "#2D72D2", "--saved", "true", "--pin-monitor", "1"), "alta.window.updated");
        Assert.AreEqual("Code review", updated.GetProperty("name").GetString());
        Assert.AreEqual(("w-alpha1", new AltaWindowUpdate { Name = "Code review", Icon = "eye", Color = "#2D72D2", Saved = true, PinnedMonitorId = "m-1" }), fixture.Windows.Updated[^1]);

        fixture.Single(await fixture.OkAsync("window", "set", "Docs", "--icon=", "--color=", "--pin-monitor", "none", "--saved", "False"), "alta.window.updated");
        Assert.AreEqual(("w-beta", new AltaWindowUpdate { Icon = string.Empty, Color = string.Empty, Saved = false, PinnedMonitorId = string.Empty }), fixture.Windows.Updated[^1],
            "A saved window that is closed is changed too, and an empty value takes the value away.");

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidIcon", "window", "set", "Review", "--icon", "Not An Icon");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidColor", "window", "set", "Review", "--color", "#12345");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidSaved", "window", "set", "Review", "--saved", "maybe");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingChange", "window", "set", "Review");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "window.notFound", "window", "set", "nowhere", "--name", "X");
        Assert.AreEqual(2, fixture.Windows.Updated.Count);
    }

    private static string? Type(JsonElement line) => line.GetProperty("type").GetString();

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private static AltaMonitorReference MonitorOne => new("m-1", "DELL U2720Q", true);

    private static AltaMonitorReference MonitorTwo => new("m-2", "LG HDR", false);

    private sealed class FakeWindows : IAltaWindows
    {
        /// <summary>The status every request answers with.</summary>
        public string Status { get; set; } = "ok";

        public List<AltaWindow> Windows { get; } =
        [
            new()
            {
                Id = "main", Main = true, Name = "Main", Open = true, SpaceId = "default", Monitor = MonitorOne, State = "maximized", Focused = true, Visible = true,
                Bounds = new AltaRect(0, 0, 1920, 1040), OnCurrentDesktop = true, Tabs = [new("session:s1", "session", true), new("editor:p1", "editor", false)],
            },
            new()
            {
                Id = "w-alpha1", Name = "Review", Icon = "eye", Color = "#2d72d2", Saved = true, Open = true, SpaceId = "default", Monitor = MonitorTwo, PinnedMonitor = MonitorTwo,
                PinnedConnected = true, Visible = true, Bounds = new AltaRect(100, 100, 1200, 800), OnCurrentDesktop = false, Tabs = [new("session:s2", "session", true)],
            },
            new()
            {
                Id = "w-alpha2", Name = "Window 3", Open = true, SpaceId = "work", Monitor = MonitorOne, Visible = true, Bounds = new AltaRect(50, 50, 800, 600),
                Tabs = [new("terminal:t1", "terminal", true)],
            },
            new() { Id = "w-beta", Name = "Docs", Saved = true, PinnedMonitor = new("m-3", "Old monitor", false), PinnedConnected = false },
        ];

        public List<AltaWindowOpenRequest> Opened { get; } = [];

        // The tabs that a window opened with does not get: a code editor with unsaved edits.
        public HashSet<string> Unmovable { get; } = new(StringComparer.Ordinal);

        public List<string> Focused { get; } = [];

        public List<string> Closed { get; } = [];

        public List<(string Tab, string? Target, string? Monitor)> Moved { get; } = [];

        public List<(string Id, AltaWindowPlacement Placement)> Placed { get; } = [];

        public List<(string Id, AltaWindowUpdate Update)> Updated { get; } = [];

        public Task<IReadOnlyList<AltaWindow>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AltaWindow>>([.. Windows]);

        public Task<IReadOnlyList<AltaMonitor>> ListMonitorsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AltaMonitor>>(
            [
                new() { Id = "m-1", Name = "DELL U2720Q", Number = 1, Bounds = new(0, 0, 1920, 1080), WorkArea = new(0, 0, 1920, 1040), Primary = true, IdPersistent = true },
                new() { Id = "m-2", Name = "LG HDR", Number = 2, Bounds = new(1920, 0, 2560, 1440), WorkArea = new(1920, 0, 2560, 1400), Scale = 1.5 },
            ]);

        public Task<AltaWindowResult> OpenAsync(AltaWindowOpenRequest request, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaWindowResult(Status));
            Opened.Add(request);
            var window = request.SavedWindowId is { } saved
                ? Windows.Single(window => window.Id == saved) with { Open = true }
                : new AltaWindow { Id = "w-new", Name = request.Name ?? "Window 4", Icon = request.Icon, Color = request.Color, Saved = request.Name is not null, Open = true };
            var moves = request.Tabs.Select(tab => new AltaWindowTabMove(tab, Unmovable.Contains(tab) ? "unsaved" : "ok")).ToArray();
            return Task.FromResult(new AltaWindowResult("ok",
                window with { Tabs = [.. moves.Where(static move => move.Status == "ok").Select(static move => new AltaWindowTab(move.Tab, move.Tab.Split(':')[0], false))] }, moves));
        }

        public Task<AltaWindowResult> FocusAsync(string windowId, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaWindowResult(Status));
            Focused.Add(windowId);
            return Task.FromResult(new AltaWindowResult("ok", Windows.Single(window => window.Id == windowId) with { Focused = true }));
        }

        public Task<AltaWindowResult> CloseAsync(string windowId, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaWindowResult(Status));
            Closed.Add(windowId);
            return Task.FromResult(new AltaWindowResult("ok", Windows.Single(window => window.Id == windowId) with { Open = false, Tabs = [] }));
        }

        public Task<AltaTabMoveResult> MoveTabAsync(string tab, string? targetWindowId, string? monitorId, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaTabMoveResult(Status));
            Moved.Add((tab, targetWindowId, monitorId));
            return Task.FromResult(new AltaTabMoveResult("ok", targetWindowId ?? "w-new"));
        }

        public Task<AltaWindowResult> PlaceAsync(string windowId, AltaWindowPlacement placement, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaWindowResult(Status));
            Placed.Add((windowId, placement));
            return Task.FromResult(new AltaWindowResult("ok", Windows.Single(window => window.Id == windowId)));
        }

        public Task<AltaWindowResult> UpdateAsync(string windowId, AltaWindowUpdate update, CancellationToken cancellationToken)
        {
            if (Status != "ok") return Task.FromResult(new AltaWindowResult(Status));
            Updated.Add((windowId, update));
            var window = Windows.Single(window => window.Id == windowId);
            return Task.FromResult(new AltaWindowResult("ok", window with { Name = string.IsNullOrEmpty(update.Name) ? window.Name : update.Name }));
        }
    }

    private sealed class Fixture
    {
        private Fixture(AltaCommandDispatcher dispatcher, FakeWindows windows)
        {
            Dispatcher = dispatcher;
            Windows = windows;
        }

        public FakeWindows Windows { get; }

        private AltaCommandDispatcher Dispatcher { get; }

        public static Fixture Create(bool windows = true)
        {
            var fake = new FakeWindows();
            var services = new AltaServiceCollection();
            if (windows) services.Add<IAltaWindows>(fake);
            var registry = new AltaCommandRegistry();
            services.Add(registry);
            return new Fixture(new AltaCommandDispatcher(registry, services), fake);
        }

        public Task<AltaCommandResult> RunAsync(params string[] args)
            => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public Task<AltaCommandResult> OkAsync(params string[] args) => OkAsync(args, Mcp);

        public async Task<AltaCommandResult> OkAsync(string[] args, AltaCallerIdentity caller)
        {
            var result = await Dispatcher.InvokeAsync(args, null, caller, null);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            return result;
        }

        public async Task<JsonElement> FailsAsync(int exitCode, string code, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            var error = Records(result).Single(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }

        public JsonElement Single(AltaCommandResult result, string type)
            => Records(result).Single(line => Type(line) == type);
    }
}
