using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

/// <summary>The canvases of plugins in the window: the broker and its service, over a real plugin runtime that runs one fixture plugin.</summary>
[TestClass]
public sealed class CanvasesRpcTests
{
    private const string Epoch = "epoch-1";
    private const string Plugin = "builtin:canvas-fixture";

    [TestMethod]
    public async Task Requests_AreRefusedWithoutPluginsAndForAnotherStart()
    {
        var unavailable = new CanvasesService();
        Assert.AreEqual("unavailable", unavailable.List(new(Epoch)).Status);
        Assert.AreEqual("unavailable", (await unavailable.OpenAsync(new(Epoch, Plugin, "board", null, null, null, null, true), default)).Status);
        Assert.AreEqual("unavailable", unavailable.Visible(new(Epoch, "x", true)).Status);
        Assert.AreEqual("unavailable", (await unavailable.CloseAsync(new(Epoch, "x"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.ActionAsync(new(Epoch, "x", "a", null, null), default)).Status);
        var events = new List<CanvasEvent>();
        await foreach (var value in unavailable.WatchAsync(new(Epoch), default)) events.Add(value);
        Assert.AreEqual(0, events.Count);

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", fixture.Service.List(new("other")).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.OpenAsync(new("other", Plugin, "board", null, null, null, null, true), default)).Status);
        await foreach (var value in fixture.Service.WatchAsync(new("other"), default)) events.Add(value);
        Assert.AreEqual(0, events.Count, "a page of another start watches nothing");
        Assert.AreEqual("invalid_request", (await fixture.Service.OpenAsync(new(Epoch, null, "board", null, null, null, null, true), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.OpenAsync(new(Epoch, Plugin, "bad id", null, null, null, null, true), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.OpenAsync(new(Epoch, Plugin, "board", null, null, null, "line\nbreak", true), default)).Status);
        Assert.AreEqual("invalid_request", fixture.Service.Visible(new(Epoch, "", true)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ActionAsync(new(Epoch, "x", "", null, null), default)).Status);
    }

    [TestMethod]
    public async Task List_ReportsTheCanvasesOfTheActivePlugins()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = fixture.Service.List(new(Epoch));

        Assert.AreEqual("ok", response.Status);
        CollectionAssert.AreEqual(new[] { "board", "notes", "run", "broken", "scripted", "scriptfile", "plain", "calls" }, response.Canvases.Select(static canvas => canvas.Id).ToArray());
        var board = response.Canvases[0];
        Assert.AreEqual((Plugin, "Canvas fixture", "Board", "A board.", "list-checks", "Application", true, 2, true),
            (board.PluginKey, board.Plugin, board.Title, board.Description, board.Icon, board.Scope, board.Input, board.Actions, board.Describes));
        Assert.IsNull(board.Package, "a built-in plugin has no package folder");
        Assert.AreEqual("Project", response.Canvases[1].Scope);
        Assert.AreEqual("Session", response.Canvases[2].Scope);
        Assert.IsFalse(response.Canvases[6].Describes);
    }

    [TestMethod]
    public async Task Open_RunsTheHandlerOncePerIdentity_AndReturnsWhatTheTabShows()
    {
        await using var fixture = await Fixture.CreateAsync();

        // A tab under StrictMode asks twice at once.
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.OpenAsync("board", space: "work", key: "k1")));

        Assert.AreEqual(1, fixture.Plugin.Opened);
        Assert.IsTrue(answers.All(static answer => answer.Status == "ok"));
        Assert.AreEqual(1, answers.Select(static answer => answer.InstanceId).Distinct().Count());
        var first = answers[0];
        Assert.AreEqual(("Board k1", "<p>board k1</p>", true, 1), (first.Title, first.Html, first.Actions, first.Revision));
        Assert.AreEqual("list-checks", first.Icon);
        Assert.IsNull(first.StatusText);

        // Another space, another key, another instance of the same canvas: the plugin holds the state.
        var otherSpace = await fixture.OpenAsync("board", space: "play", key: "k1");
        var otherKey = await fixture.OpenAsync("board", space: "work", key: "k2");
        Assert.AreEqual(3, new[] { first.InstanceId, otherSpace.InstanceId, otherKey.InstanceId }.Distinct().Count());
        Assert.AreEqual(3, fixture.Plugin.Opened);
        var context = fixture.Plugin.Contexts[first.InstanceId!];
        Assert.AreEqual(("board", "work", "k1", null, null), (context.CanvasId, context.SpaceId, context.Key, context.ProjectId, context.SessionId));
    }

    [TestMethod]
    public async Task Open_NamesTheInstanceByWhatTheScopeSays()
    {
        await using var fixture = await Fixture.CreateAsync();

        // An application canvas has no project or session, whatever the tab says.
        var board = await fixture.OpenAsync("board", project: "p1", session: "s1");
        Assert.AreEqual(board.InstanceId, (await fixture.OpenAsync("board", project: "p2", session: "s2")).InstanceId);
        Assert.IsNull(fixture.Plugin.Contexts[board.InstanceId!].ProjectId);
        // A project canvas needs a project, and ignores the session.
        Assert.AreEqual("invalid_request", (await fixture.OpenAsync("notes")).Status);
        var notes = await fixture.OpenAsync("notes", project: "p1", session: "s1");
        Assert.AreEqual("ok", notes.Status);
        Assert.AreEqual(notes.InstanceId, (await fixture.OpenAsync("notes", project: "p1", session: "s2")).InstanceId);
        Assert.AreNotEqual(notes.InstanceId, (await fixture.OpenAsync("notes", project: "p2")).InstanceId);
        // A session canvas needs a session, and keeps the project of the session.
        Assert.AreEqual("invalid_request", (await fixture.OpenAsync("run", project: "p1")).Status);
        var run = await fixture.OpenAsync("run", project: "p1", session: "s1");
        Assert.AreEqual(("p1", "s1"), (fixture.Plugin.Contexts[run.InstanceId!].ProjectId, fixture.Plugin.Contexts[run.InstanceId!].SessionId));
        Assert.AreEqual(run.InstanceId, (await fixture.OpenAsync("run", session: "s1", project: "p1")).InstanceId);
    }

    [TestMethod]
    public async Task Open_RefusesAnUnknownCanvas_AStoppedPlugin_AndAFailingHandler()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.AreEqual("unknown_canvas", (await fixture.OpenAsync("missing")).Status);
        var stopped = await fixture.Service.OpenAsync(new(Epoch, "builtin:gone", "board", null, null, null, null, true), default);
        Assert.AreEqual("plugin_stopped", stopped.Status);
        Assert.IsNull(stopped.InstanceId);

        var broken = await fixture.OpenAsync("broken");
        Assert.AreEqual(("failed", null), (broken.Status, broken.InstanceId));
        Assert.AreEqual("Broken", broken.Title, "a refusal that knows the canvas says its title");
        Assert.IsFalse(JsonSerializer.Serialize(broken, DesktopJsonContext.Default.CanvasOpenResponse).Contains("secret", StringComparison.Ordinal), "the text of a plugin failure stays out of the page");
        // A failed instance is not kept: the next request tries again.
        await fixture.OpenAsync("broken");
        Assert.AreEqual(2, fixture.Plugin.BrokenAttempts);
        Assert.AreEqual(0, fixture.Broker.GetOpen().Count);
    }

    [TestMethod]
    public async Task Rpc_CarriesTheCallsOfAScriptThroughTheService_AndRefusesWhatIsNotItsOwn()
    {
        await using var fixture = await Fixture.CreateAsync();

        // Nothing to connect to: a stale page, a bad request, no such instance, an instance that has no calls.
        Assert.AreEqual("stale_epoch", fixture.Service.RpcOpen(new("other", "x")).Status);
        Assert.AreEqual("invalid_request", fixture.Service.RpcOpen(new(Epoch, "")).Status);
        Assert.AreEqual("unknown", fixture.Service.RpcOpen(new(Epoch, "nope")).Status);
        Assert.AreEqual("unavailable", fixture.Service.RpcOpen(new(Epoch, (await fixture.OpenAsync("plain")).InstanceId)).Status, "its plugin registered no call and its view has no script");
        Assert.AreEqual("unavailable", new CanvasesService().RpcOpen(new(Epoch, "x")).Status);

        var first = await fixture.OpenAsync("calls", space: "work", key: "k1");
        var second = await fixture.OpenAsync("calls", space: "work", key: "k2");
        var one = fixture.Service.RpcOpen(new(Epoch, first.InstanceId));
        var two = fixture.Service.RpcOpen(new(Epoch, second.InstanceId));
        Assert.AreEqual(("ok", "ok", 1 << 20), (one.Status, two.Status, one.MaximumFrameBytes));
        Assert.IsFalse(string.IsNullOrEmpty(one.Connection));
        Assert.AreNotEqual(one.Connection, two.Connection);

        // Each instance answers for itself, on its own connection, and the answer names both.
        Assert.AreEqual("ok", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [Invoke("r1", "where"), Invoke("r2", "echo", "{\"a\":1}")])).Status);
        Assert.AreEqual("ok", fixture.Service.RpcSend(new(Epoch, second.InstanceId, two.Connection, [Invoke("r3", "where")])).Status);
        var answers = new Dictionary<string, JsonElement>();
        while (answers.Count < 3)
        {
            var carried = await fixture.NextAsync("rpc");
            foreach (var frame in carried.Frames!)
            {
                var root = JsonDocument.Parse(frame).RootElement.Clone();
                answers[root.GetProperty("id").GetString()!] = root;
                Assert.AreEqual(carried.InstanceId == first.InstanceId ? one.Connection : two.Connection, carried.Connection);
                Assert.AreEqual(carried.InstanceId == first.InstanceId, root.GetProperty("id").GetString() != "r3", "a frame of an instance is carried with the connection of that instance");
            }
        }

        Assert.AreEqual(("k1", first.InstanceId), (answers["r1"].GetProperty("value").GetProperty("key").GetString(), answers["r1"].GetProperty("value").GetProperty("instance").GetString()));
        Assert.AreEqual(1, answers["r2"].GetProperty("value").GetProperty("a").GetInt32());
        Assert.AreEqual(("k2", second.InstanceId), (answers["r3"].GetProperty("value").GetProperty("key").GetString(), answers["r3"].GetProperty("value").GetProperty("instance").GetString()));

        // A connection serves the instance that opened it and nothing else.
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, second.InstanceId, one.Connection, [Invoke("x1", "where")])).Status);
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, first.InstanceId, two.Connection, [Invoke("x2", "where")])).Status);
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, "nope", one.Connection, [Invoke("x3", "where")])).Status);
        Assert.IsFalse(fixture.HasEvent("rpc"), "nothing was answered for them");
        Assert.AreEqual("invalid_request", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [])).Status);
        Assert.AreEqual("invalid_request", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, null)).Status);
        Assert.AreEqual("invalid_request", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, new string[257])).Status);
        Assert.AreEqual("invalid_request", fixture.Service.RpcSend(new(Epoch, first.InstanceId, "bad\nid", ["{}"])).Status);
        Assert.AreEqual("payload_too_large", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [new string('x', CanvasRpcEndpoint.MaximumFrameBytes + 1)])).Status);
        Assert.AreEqual("unknown", fixture.Service.RpcClose(new(Epoch, first.InstanceId, two.Connection)).Status);
        Assert.AreEqual("ok", fixture.Service.RpcClose(new(Epoch, first.InstanceId, one.Connection)).Status);
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [Invoke("x4", "where")])).Status);
    }

    [TestMethod]
    public async Task Rpc_EndsWithItsInstance_Cancelling_TheCallsAndTellingThePage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.OpenAsync("calls", space: "work", key: "k1");
        var second = await fixture.OpenAsync("calls", space: "play", key: "k2");
        var one = fixture.Service.RpcOpen(new(Epoch, first.InstanceId));
        var two = fixture.Service.RpcOpen(new(Epoch, second.InstanceId));
        fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [Invoke("slow1", "slow")]));
        await fixture.Plugin.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The page closes the tab: what the plugin was doing for it is cancelled, and nothing is left to talk to.
        Assert.AreEqual("ok", (await fixture.Service.CloseAsync(new(Epoch, first.InstanceId), default)).Status);

        await fixture.Plugin.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var ended = await fixture.NextAsync("rpcClosed");
        Assert.AreEqual((first.InstanceId, one.Connection), (ended.InstanceId, ended.Connection));
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, first.InstanceId, one.Connection, [Invoke("late", "where")])).Status);

        // A space that goes away takes its instances with it.
        await fixture.Service.CloseSpaceAsync(new(Epoch, "play"), default);
        var gone = await fixture.NextAsync("rpcClosed");
        Assert.AreEqual((second.InstanceId, two.Connection), (gone.InstanceId, gone.Connection));
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, second.InstanceId, two.Connection, [Invoke("late", "where")])).Status);
    }

    [TestMethod]
    public async Task Rpc_DropsTheConnectionsOfAPageThatIsReplacedOrGone_AndHasNoneForNoPage()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        var page = fixture.Watch();
        var opened = await fixture.OpenAsync("calls", space: "work", key: "k1");
        var before = fixture.Service.RpcOpen(new(Epoch, opened.InstanceId));
        Assert.AreEqual("ok", before.Status);
        Assert.AreEqual("ok", fixture.Service.RpcSend(new(Epoch, opened.InstanceId, before.Connection, [Invoke("r1", "where")])).Status);
        await page.NextAsync("rpc");

        // A page that reloads watches again: the connection of the one before cannot be answered any more.
        var next = fixture.Watch();
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, opened.InstanceId, before.Connection, [Invoke("r2", "where")])).Status);
        await page.EndedAsync();
        var after = fixture.Service.RpcOpen(new(Epoch, opened.InstanceId));
        Assert.AreEqual("ok", after.Status);
        Assert.AreEqual("ok", fixture.Service.RpcSend(new(Epoch, opened.InstanceId, after.Connection, [Invoke("r3", "where")])).Status);
        await next.NextAsync("rpc");

        // A page that goes away leaves no connection and no way to open one.
        next.Dispose();
        SpinWait.SpinUntil(() => fixture.Service.RpcSend(new(Epoch, opened.InstanceId, after.Connection, [Invoke("r4", "where")])).Status == "closed", TimeSpan.FromSeconds(30));
        Assert.AreEqual("closed", fixture.Service.RpcSend(new(Epoch, opened.InstanceId, after.Connection, [Invoke("r5", "where")])).Status);
        Assert.AreEqual("unavailable", fixture.Service.RpcOpen(new(Epoch, opened.InstanceId)).Status);
        page.Dispose();
    }

    private static string Invoke(string id, string command, string args = "{}")
        => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{args}}}""";

    [TestMethod]
    public async Task Visibility_IsToldToThePlugin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", visible: false);
        var context = fixture.Plugin.Contexts[opened.InstanceId!];
        var changes = new List<bool>();
        context.VisibilityChanged += changes.Add;
        Assert.IsFalse(context.IsVisible);
        Assert.IsTrue(context.IsOpen);

        Assert.AreEqual("ok", fixture.Service.Visible(new(Epoch, opened.InstanceId, true)).Status);
        Assert.IsTrue(context.IsVisible);
        fixture.Service.Visible(new(Epoch, opened.InstanceId, true));
        fixture.Service.Visible(new(Epoch, opened.InstanceId, false));

        CollectionAssert.AreEqual(new[] { true, false }, changes, "only a change is told");
        Assert.IsFalse(context.IsVisible);
        Assert.AreEqual("unknown", fixture.Service.Visible(new(Epoch, "nope", true)).Status);
        Assert.AreEqual(false, fixture.Broker.GetOpen().Single().IsVisible);
        // Opening again says the tab is shown.
        await fixture.OpenAsync("board", visible: true);
        Assert.IsTrue(context.IsVisible);
    }

    [TestMethod]
    public async Task APush_ReachesThePageOnTheWatch_AndANewPageGetsTheLatest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];

        await context.UpdateAsync("<p>one</p>");
        var update = await fixture.NextAsync("update");
        Assert.AreEqual(("<p>one</p>", opened.InstanceId), (update.Html, update.InstanceId));
        Assert.IsTrue(update.Revision > opened.Revision);
        Assert.IsNull(update.Title, "an update says only what changed");

        await context.SetTitleAsync("Renamed");
        await context.SetStatusAsync("3 of 8 done");
        CanvasEvent last;
        do last = await fixture.NextAsync("update");
        while (last.StatusText != "3 of 8 done");
        Assert.AreEqual(("Renamed", "3 of 8 done", null), (last.Title, last.StatusText, last.Html));

        // The same content is no news, and a cleared status is a blank one.
        await context.UpdateAsync("<p>one</p>");
        Assert.IsFalse(fixture.HasEvent("update"));
        await context.SetStatusAsync(null);
        var cleared = await fixture.NextAsync("update");
        Assert.AreEqual((null, "", "Renamed"), (cleared.Html, cleared.StatusText, cleared.Title));
        // A page that opens again gets the latest.
        var again = await fixture.OpenAsync("board", key: "k");
        Assert.AreEqual(("<p>one</p>", "Renamed", null), (again.Html, again.Title, again.StatusText));
    }

    [TestMethod]
    public void TheOutbox_KeepsTheLastStateOfEachInstanceForAPageThatReadsSlowly()
    {
        var outbox = new CanvasOutbox();
        outbox.Add(new CanvasEvent("update") { InstanceId = "a", Html = "<p>1</p>", Revision = 2 });
        outbox.Add(new CanvasEvent("update") { InstanceId = "b", Html = "<p>b</p>", Revision = 2 });
        outbox.Add(new CanvasEvent("update") { InstanceId = "a", Title = "Renamed", StatusText = "", Revision = 3 });
        outbox.Add(new CanvasEvent("update") { InstanceId = "a", Html = "<p>3</p>", Revision = 4 });
        outbox.Add(new CanvasEvent("plugins"));
        outbox.Add(new CanvasEvent("plugins"));
        outbox.Complete();

        var read = outbox.ReadAllAsync(default).ToBlockingEnumerable().ToArray();

        Assert.AreEqual(3, read.Length);
        Assert.AreEqual(("a", "<p>3</p>", "Renamed", 4), (read[0].InstanceId, read[0].Html, read[0].Title, read[0].Revision), "the pushes of an instance are one event, in the place of the first");
        Assert.AreEqual(("b", "<p>b</p>"), (read[1].InstanceId, read[1].Html));
        Assert.AreEqual("plugins", read[2].Kind, "the same news twice is told once");
    }

    [TestMethod]
    public void TheOutbox_DropsWhatAStateOrACloseMakesMoot_AndBoundsWhatWaits()
    {
        var outbox = new CanvasOutbox();
        outbox.Add(new CanvasEvent("update") { InstanceId = "a", Html = "<p>1</p>" });
        outbox.Add(new CanvasEvent("state") { InstanceId = "a", State = "plugin_stopped" });
        outbox.Add(new CanvasEvent("update") { InstanceId = "b", Html = "<p>b</p>" });
        outbox.Add(new CanvasEvent("closed") { InstanceId = "b" });
        for (var index = 0; index < DesktopCanvases.MaximumPendingEvents + 40; index++) outbox.Add(new CanvasEvent("open") { Key = "k" + index });
        outbox.Complete();

        var read = outbox.ReadAllAsync(default).ToBlockingEnumerable().ToArray();

        Assert.AreEqual(DesktopCanvases.MaximumPendingEvents, read.Length);
        Assert.IsFalse(read.Any(static value => value.Kind == "update"), "an update of an instance that changed state or closed is not told");
        Assert.IsTrue(read.Any(static value => value.Kind == "open" && value.Key == "k" + (DesktopCanvases.MaximumPendingEvents + 39)), "the newest request is kept");
        outbox.Add(new CanvasEvent("plugins"));
        Assert.AreEqual(0, outbox.ReadAllAsync(default).ToBlockingEnumerable().Count(), "nothing is added to a page that is gone");
    }

    [TestMethod]
    public async Task APushAfterTheInstanceIsClosed_IsDropped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];
        Assert.AreEqual("ok", (await fixture.Service.CloseAsync(new(Epoch, opened.InstanceId), default)).Status);

        Assert.IsFalse(context.IsOpen);
        Assert.IsTrue(context.Closed.IsCancellationRequested);
        await context.UpdateAsync("<p>late</p>");
        await context.SetTitleAsync("late");
        await context.InvalidateAsync();
        Assert.IsFalse(fixture.HasEvent("update"));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await context.UpdateAsync(null!));
    }

    [TestMethod]
    public async Task Invalidate_WritesTheFragmentAgain_ForTheInstanceOrForEveryInstanceOfACanvas()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.OpenAsync("notes", project: "p1");
        var second = await fixture.OpenAsync("notes", project: "p2");
        Assert.AreEqual("<p>notes p1: 0</p>", first.Html);
        fixture.Plugin.Counter = 5;

        await fixture.Plugin.Contexts[first.InstanceId!].InvalidateAsync();

        var one = await fixture.NextAsync("update");
        Assert.AreEqual((first.InstanceId, "<p>notes p1: 5</p>"), (one.InstanceId, one.Html));
        Assert.IsFalse(fixture.HasEvent("update"));
        fixture.Plugin.Counter = 6;
        await fixture.PluginServices.Canvases.InvalidateAsync("notes");
        var both = new[] { await fixture.NextAsync("update"), await fixture.NextAsync("update") };
        Assert.AreEqual(2, both.Select(static value => value.InstanceId).Distinct().Count());
        Assert.IsTrue(both.All(static value => value.Html!.EndsWith(": 6</p>", StringComparison.Ordinal)));
        Assert.AreEqual(second.InstanceId, both.Single(value => value.Html!.Contains("p2", StringComparison.Ordinal)).InstanceId);
        // A canvas whose view has no renderer is left alone.
        await fixture.OpenAsync("board", key: "k");
        await fixture.PluginServices.Canvases.InvalidateAsync("board");
        Assert.IsFalse(fixture.HasEvent("update"));
    }

    [TestMethod]
    public async Task AnAction_ReachesThePlugin_WithTheValuesOfTheFragment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");

        var response = await fixture.Service.ActionAsync(new(Epoch, opened.InstanceId, "tick", "item-2", new() { ["note"] = "hello" }), default);

        Assert.AreEqual(("ok", "<p>ticked item-2 hello</p>", false), (response.Status, response.Html, response.Closed));
        var seen = fixture.Plugin.Actions.Single();
        Assert.AreEqual((opened.InstanceId, "tick", "item-2", "hello"), (seen.Instance, seen.Name, seen.Value, seen.Values["note"]));
        // What the answer carries is not pushed again, and a later action sees the new content.
        Assert.IsFalse(fixture.HasEvent("update"));
        Assert.AreEqual("<p>ticked item-2 hello</p>", (await fixture.OpenAsync("board", key: "k")).Html);
        // The action that does nothing leaves the tab as it is; the one that closes closes it.
        Assert.AreEqual(("ok", null, false), Tuple(await fixture.Service.ActionAsync(new(Epoch, opened.InstanceId, "noop", null, null), default)));
        Assert.AreEqual(("ok", null, true), Tuple(await fixture.Service.ActionAsync(new(Epoch, opened.InstanceId, "close", null, null), default)));
        Assert.AreEqual("unknown", (await fixture.Service.ActionAsync(new(Epoch, opened.InstanceId, "tick", null, null), default)).Status);
        Assert.AreEqual(1, fixture.Plugin.Closed.Count, "closing by an action runs the handler of the plugin");

        static (string, string?, bool) Tuple(CanvasActionResponse value) => (value.Status, value.Html, value.Closed);
    }

    [TestMethod]
    public async Task AnAction_ThatFailsOrIsNotHandled_SaysSoWithoutTheText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var board = await fixture.OpenAsync("board", key: "k");
        var plain = await fixture.OpenAsync("plain");

        var failed = await fixture.Service.ActionAsync(new(Epoch, board.InstanceId, "throw", null, null), default);

        Assert.AreEqual(("failed", null, false), (failed.Status, failed.Html, failed.Closed));
        Assert.IsFalse(JsonSerializer.Serialize(failed, DesktopJsonContext.Default.CanvasActionResponse).Contains("secret", StringComparison.Ordinal));
        Assert.AreEqual("unsupported", (await fixture.Service.ActionAsync(new(Epoch, plain.InstanceId, "tick", null, null), default)).Status);
        Assert.AreEqual("unknown", (await fixture.Service.ActionAsync(new(Epoch, "nope", "tick", null, null), default)).Status);
        var huge = new string('x', 70_000);
        Assert.AreEqual("invalid_request", (await fixture.Service.ActionAsync(new(Epoch, board.InstanceId, "tick", huge, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ActionAsync(new(Epoch, board.InstanceId, "tick", null, new() { [""] = "v" }), default)).Status);
    }

    [TestMethod]
    public async Task ThePluginsFragmentIsCutAtTheLimit_AndATitleIsOneLine()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];

        await context.UpdateAsync(new string('x', DesktopCanvases.MaximumHtmlUnits + 5000));
        await context.SetTitleAsync("two\nlines\tof " + new string('t', 400));

        // The fragment and the title may arrive together or one after the other.
        string? html = null, title = null;
        while (html is null || title is null)
        {
            var update = await fixture.NextAsync("update");
            html ??= update.Html;
            title ??= update.Title;
        }

        Assert.AreEqual(DesktopCanvases.MaximumHtmlUnits, html.Length);
        Assert.AreEqual(DesktopCanvases.MaximumTitleUnits, title.Length);
        Assert.IsFalse(title.Any(char.IsControl));
        // A blank title brings back the title of the canvas.
        await context.SetTitleAsync("  ");
        Assert.AreEqual("Board", (await fixture.NextAsync("update")).Title);
    }

    [TestMethod]
    public async Task Close_RunsTheHandlerOfThePlugin_AndEndsWhatTheInstanceHeld()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];

        Assert.AreEqual("ok", (await fixture.Service.CloseAsync(new(Epoch, opened.InstanceId), default)).Status);

        Assert.AreEqual(opened.InstanceId, fixture.Plugin.Closed.Single());
        Assert.IsTrue(context.Closed.IsCancellationRequested, "the token is cancelled before the handler runs");
        Assert.AreEqual("unknown", (await fixture.Service.CloseAsync(new(Epoch, opened.InstanceId), default)).Status);
        Assert.AreEqual(0, fixture.Broker.GetOpen().Count);
        Assert.IsFalse(fixture.HasEvent("closed"), "the page that closed its tab is not told");
        // The tab opens again, with the state the plugin kept.
        Assert.AreEqual("ok", (await fixture.OpenAsync("board", key: "k")).Status);
        Assert.AreEqual(2, fixture.Plugin.Opened);
    }

    [TestMethod]
    public async Task ThePluginCanCloseItsInstance_AndThePageIsTold()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");

        Assert.IsTrue(await fixture.PluginServices.Canvases.CloseAsync(opened.InstanceId!));
        Assert.IsFalse(await fixture.PluginServices.Canvases.CloseAsync(opened.InstanceId!));

        Assert.AreEqual(opened.InstanceId, (await fixture.NextAsync("closed")).InstanceId);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await fixture.PluginServices.Canvases.CloseAsync(" "));
    }

    [TestMethod]
    public async Task CloseSpace_ClosesTheInstancesOfADeletedSpace()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenAsync("board", space: "a", key: "1");
        await fixture.OpenAsync("board", space: "a", key: "2");
        await fixture.OpenAsync("board", space: "b", key: "1");

        Assert.AreEqual("ok", (await fixture.Service.CloseSpaceAsync(new(Epoch, "a"), default)).Status);

        Assert.AreEqual(1, fixture.Broker.GetOpen().Count);
        Assert.AreEqual("b", fixture.Broker.GetOpen().Single().SpaceId);
        Assert.AreEqual(2, fixture.Plugin.Closed.Count);
        Assert.AreEqual("invalid_request", (await fixture.Service.CloseSpaceAsync(new(Epoch, ""), default)).Status);
    }

    [TestMethod]
    public async Task APluginAsksForATab_InTheContextOfItsOperation_AndTheShownSpace()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Shown = "work";
        var canvases = fixture.PluginServices.Canvases;

        var board = await canvases.OpenAsync("board");

        Assert.AreEqual((PluginCanvasOpenStatus.Requested, "work", true), (board.Status, board.SpaceId, board.Shown));
        var request = await fixture.NextAsync("open");
        Assert.AreEqual((Plugin, "board", "work", null, null, null, true, "Board", "list-checks"),
            (request.PluginKey, request.CanvasId, request.SpaceId, request.ProjectId, request.SessionId, request.Key, request.Focus, request.Title, request.Icon));
        Assert.AreEqual(board.InstanceId, request.InstanceId, "the plugin and the page name the instance alike");
        Assert.AreEqual(board.InstanceId, (await fixture.OpenAsync("board", space: "work")).InstanceId);

        // The pane a command runs in names the project and the session; another space is not the shown one.
        fixture.Ui.Enter(new DesktopPluginScope { ProjectId = "p1", SessionId = "s1" });
        var run = await canvases.OpenAsync("run", new PluginCanvasOpenOptions { SpaceId = "play", Key = "k", Focus = false });
        Assert.AreEqual((PluginCanvasOpenStatus.Requested, "play", false), (run.Status, run.SpaceId, run.Shown));
        var second = await fixture.NextAsync("open");
        Assert.AreEqual(("run", "play", "p1", "s1", "k", false), (second.CanvasId, second.SpaceId, second.ProjectId, second.SessionId, second.Key, second.Focus));
        Assert.AreEqual((await fixture.OpenAsync("run", space: "play", project: "p1", session: "s1", key: "k")).InstanceId, run.InstanceId);
    }

    [TestMethod]
    public async Task APluginsRequest_IsRefusedWhenItCannotBeServed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var canvases = fixture.PluginServices.Canvases;

        Assert.AreEqual(PluginCanvasOpenStatus.UnknownCanvas, (await canvases.OpenAsync("missing")).Status);
        Assert.AreEqual(PluginCanvasOpenStatus.Invalid, (await canvases.OpenAsync("bad id")).Status);
        Assert.AreEqual(PluginCanvasOpenStatus.MissingContext, (await canvases.OpenAsync("notes")).Status, "no project in the operation");
        Assert.AreEqual(PluginCanvasOpenStatus.MissingContext, (await canvases.OpenAsync("run", new PluginCanvasOpenOptions { ProjectId = "p1" })).Status);
        Assert.AreEqual(PluginCanvasOpenStatus.Invalid, (await canvases.OpenAsync("board", new PluginCanvasOpenOptions { Key = "a\nb" })).Status);
        var huge = JsonDocument.Parse("\"" + new string('x', DesktopCanvases.MaximumInputUnits) + "\"").RootElement;
        Assert.AreEqual(PluginCanvasOpenStatus.Invalid, (await canvases.OpenAsync("board", new PluginCanvasOpenOptions { Input = huge })).Status);
        Assert.IsFalse(fixture.HasEvent("open"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await canvases.OpenAsync(""));

        // A plugin without a broker behind it, and a service that is not the one of a plugin.
        Assert.AreEqual(PluginCanvasOpenStatus.UnknownCanvas, (await fixture.Broker.OpenAsync("board")).Status);
    }

    [TestMethod]
    public async Task Open_GivesTheScriptOfTheCanvas_AndTheInputItWasOpenedWith()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        string Source(string? path) => Encoding.UTF8.GetString(fixture.Broker.Modules.GetResponse(new NeoResourceRequest(new Uri("app://codealta" + path), "GET", new Dictionary<string, string>(), null, NeoResourceKind.Script, false, default))!.Bytes.Span);

        // A canvas whose script is given as text: the page is told where to import it from, and the host serves it.
        await fixture.PluginServices.Canvases.OpenAsync("scripted", new PluginCanvasOpenOptions { Input = JsonDocument.Parse("{\"issue\":42}").RootElement });
        var scripted = await fixture.OpenAsync("scripted");
        Assert.AreEqual("ok", scripted.Status);
        StringAssert.StartsWith(scripted.Script, $"/plugin/{DesktopPluginModules.KeySegment(Plugin)}/");
        StringAssert.EndsWith(scripted.Script, "/main.js");
        Assert.IsNull(scripted.ScriptProblem);
        Assert.AreEqual(CanvasFixturePlugin.ScriptText, Source(scripted.Script));
        Assert.AreEqual("{\"issue\":42}", scripted.Input);
        Assert.AreEqual("<p>scripted</p>", scripted.Html, "the fragment is the skeleton the module mounts on");

        // The same open again is the same module: a tab that is drawn again does not import another one.
        Assert.AreEqual(scripted.Script, (await fixture.OpenAsync("scripted")).Script);

        // A script that cannot be served is told to the tab, not hidden: a built-in plugin has no package folder for a file.
        var missing = await fixture.OpenAsync("scriptfile");
        Assert.AreEqual("ok", missing.Status);
        Assert.IsNull(missing.Script);
        Assert.AreEqual("The script of the canvas could not be found.", missing.ScriptProblem);

        // A canvas without a script says neither.
        var plain = await fixture.OpenAsync("plain");
        Assert.IsNull(plain.Script);
        Assert.IsNull(plain.ScriptProblem);
        Assert.IsNull(plain.Input);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ASourcePlugin_GivesAScriptThatIsAFileOfItsPackage_AndANewVersionOfTheFileIsANewAddress()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-canvases-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(global, "plugins", "boards")).FullName;
        var file = Path.Combine(folder, "plugin.cs");
        Directory.CreateDirectory(Path.Combine(folder, "ui"));
        File.WriteAllText(Path.Combine(folder, "ui", "board.js"), "export default () => 'one';");
        File.WriteAllText(file, ScriptedSource());
        var ui = new DesktopPluginUi();
        var broker = new DesktopCanvases(ui);
        ui.Modules = broker.Modules;
        var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global, Frontend = PluginFrontends.Desktop, IsHeadless = true,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker),
            });
            var package = runtime.GetPackages().Single(static candidate => candidate.Package.PackageId == "boards");
            if (package.Build is { Succeeded: false } build && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
                Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
            broker.Attach(runtime, () => "work");
            var service = new CanvasesService(broker, Epoch);
            var events = Channel.CreateUnbounded<CanvasEvent>();
            using var watching = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try { await foreach (var value in service.WatchAsync(new(Epoch), watching.Token)) events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The test is over. */ }
            });
            async Task<CanvasEvent> NextAsync(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (true)
                {
                    var value = await events.Reader.ReadAsync(timeout.Token);
                    if (value.Kind == kind) return value;
                }
            }

            string Module(string? path) => Encoding.UTF8.GetString(broker.Modules.GetResponse(new NeoResourceRequest(new Uri("app://codealta" + path), "GET", new Dictionary<string, string>(), null, NeoResourceKind.Script, false, default))!.Bytes.Span);
            var key = runtime.ActivePlugins.Single().Descriptor.RuntimeKey;
            var opened = await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default);
            Assert.AreEqual("ok", opened.Status);
            StringAssert.EndsWith(opened.Script, "/ui/board.js");
            Assert.AreEqual("export default () => 'one';", Module(opened.Script));
            // A module of the package can import another one beside it.
            File.WriteAllText(Path.Combine(folder, "ui", "helper.js"), "export const x = 1;");
            var helper = string.Join('/', opened.Script!.Split('/').SkipLast(1)) + "/helper.js";
            Assert.AreEqual("export const x = 1;", Module(helper));
            // Nothing else of the folder is served: not the source of the plugin, not what it built.
            Assert.AreEqual(404, broker.Modules.GetResponse(new NeoResourceRequest(new Uri("app://codealta" + string.Join('/', opened.Script.Split('/').SkipLast(2)) + "/plugin.cs"), "GET", new Dictionary<string, string>(), null, NeoResourceKind.Script, false, default))!.StatusCode);

            // A new version of the plugin and of its file: the instance is opened again with another address, and the old one is not asked for.
            File.WriteAllText(Path.Combine(folder, "ui", "board.js"), "export default () => 'two';");
            Assert.AreEqual(PluginPackageChange.Reloaded, (await runtime.ReloadPackageAsync(package.Package)).Change);
            var update = await NextAsync("update");
            Assert.AreEqual(opened.InstanceId, update.InstanceId);
            Assert.AreNotEqual(opened.Script, update.Script);
            StringAssert.EndsWith(update.Script, "/ui/board.js");
            Assert.AreEqual(string.Empty, update.ScriptProblem);
            Assert.AreEqual("export default () => 'two';", Module(update.Script));
            Assert.AreEqual(update.Script, (await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default)).Script);

            // A file of the package that is gone: the tab is told, and nothing is served.
            File.Delete(Path.Combine(folder, "ui", "board.js"));
            Assert.AreEqual(PluginPackageChange.Reloaded, (await runtime.ReloadPackageAsync(package.Package)).Change);
            var gone = await NextAsync("update");
            Assert.AreEqual((string.Empty, "The script of the canvas could not be found."), (gone.Script, gone.ScriptProblem));
            watching.Cancel();
            await pump;
        }
        finally
        {
            broker.Dispose();
            await runtime.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private static string ScriptedSource()
        => """
using CodeAlta.Plugins.Abstractions;

[Plugin("boards")]
public sealed class BoardsPlugin : PluginBase
{
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "board", Title = "Board",
            Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>skeleton</p>") with { Script = PluginScript.File("ui/board.js") }),
        };
    }
}
""";

    [TestMethod]
    public async Task TheRequestsOfAPluginAreKeptForAPageThatIsNotThereYet_AndInputIsGivenToTheInstanceThatTheTabCreates()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        var input = JsonDocument.Parse("{\"issue\":42}").RootElement;
        for (var index = 0; index < DesktopCanvases.MaximumOpenBacklog + 4; index++)
            await fixture.PluginServices.Canvases.OpenAsync("board", new PluginCanvasOpenOptions { Key = "k" + index, Input = input });

        fixture.StartWatching();

        var received = new List<CanvasEvent>();
        for (var index = 0; index < DesktopCanvases.MaximumOpenBacklog; index++) received.Add(await fixture.NextAsync("open"));
        Assert.AreEqual("k4", received[0].Key, "the oldest requests are dropped");
        Assert.AreEqual("k19", received[^1].Key);
        // The tab that the request led to creates the instance, which gets the input once.
        var opened = await fixture.OpenAsync("board", key: "k19");
        Assert.AreEqual(42, fixture.Plugin.Contexts[opened.InstanceId!].Input!.Value.GetProperty("issue").GetInt32());
        // An instance that exists keeps its input: the request of a plugin for an open tab only brings it to the front.
        await fixture.PluginServices.Canvases.OpenAsync("board", new PluginCanvasOpenOptions { Key = "k19", Input = JsonDocument.Parse("{\"issue\":1}").RootElement });
        await fixture.OpenAsync("board", key: "k19");
        Assert.AreEqual(42, fixture.Plugin.Contexts[opened.InstanceId!].Input!.Value.GetProperty("issue").GetInt32());
    }

    [TestMethod]
    public async Task ThePluginSeesItsOwnInstancesOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenAsync("board", key: "1");
        await fixture.OpenAsync("notes", project: "p1");

        var mine = fixture.PluginServices.Canvases.GetOpen();
        var other = fixture.Broker.ForPlugin("builtin:other");

        CollectionAssert.AreEqual(new[] { "board", "notes" }, mine.Select(static info => info.CanvasId).ToArray());
        Assert.AreEqual(("Board 1", false), (mine[0].Title, mine[0].IsVisible is false));
        Assert.AreEqual(0, other.GetOpen().Count);
        Assert.IsFalse(await other.CloseAsync(mine[0].InstanceId), "an instance of another plugin cannot be closed");
        Assert.AreEqual(2, fixture.Broker.GetOpen().Count);
    }

    [TestMethod]
    public async Task Describe_AndTheActionsForAgents_WorkWhetherOrNotTheTabIsOpen()
    {
        await using var fixture = await Fixture.CreateAsync();
        var identity = new CanvasIdentity(Plugin, "board", "work", null, null, "k");

        var closed = await fixture.Broker.DescribeAsync(identity, default);
        Assert.AreEqual(("ok", "# Board (closed)"), closed);
        var opened = await fixture.OpenAsync("board", space: "work", key: "k");
        Assert.AreEqual(("ok", "# Board (open)"), await fixture.Broker.DescribeAsync(identity, default));
        var response = await fixture.Service.DescribeAsync(new(Epoch, opened.InstanceId), default);
        Assert.AreEqual(("ok", "# Board (open)"), (response.Status, response.Markdown));
        Assert.AreEqual(("ok", null), Tuple(await fixture.Service.DescribeAsync(new(Epoch, (await fixture.OpenAsync("plain")).InstanceId), default)));
        Assert.AreEqual("unknown", (await fixture.Service.DescribeAsync(new(Epoch, "nope"), default)).Status);
        Assert.AreEqual("unknown_canvas", (await fixture.Broker.DescribeAsync(new CanvasIdentity(Plugin, "missing", null, null, null, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Broker.DescribeAsync(new CanvasIdentity(Plugin, "notes", null, null, null, null), default)).Status);

        var invoked = await fixture.Broker.InvokeAsync(identity, "add", JsonDocument.Parse("{\"item\":\"milk\"}").RootElement, default);
        Assert.AreEqual("ok", invoked.Status);
        Assert.AreEqual("milk", invoked.Result!.Value.GetProperty("added").GetString());
        Assert.AreEqual("unknown_action", (await fixture.Broker.InvokeAsync(identity, "other", null, default)).Status);
        Assert.AreEqual("failed", (await fixture.Broker.InvokeAsync(identity, "throw", null, default)).Status);
        Assert.AreEqual("plugin_stopped", (await fixture.Broker.InvokeAsync(new CanvasIdentity("builtin:gone", "board", null, null, null, null), "add", null, default)).Status);

        static (string, string?) Tuple(CanvasDescribeResponse value) => (value.Status, value.Markdown);
    }

    [TestMethod]
    public async Task AtTheLimit_TheOldestHiddenInstanceMakesRoom()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.OpenAsync("board", key: "k0", visible: false);
        var shown = await fixture.OpenAsync("board", key: "k1", visible: true);
        for (var index = 2; index < DesktopCanvases.MaximumInstances; index++) await fixture.OpenAsync("board", key: "k" + index, visible: false);
        Assert.AreEqual(DesktopCanvases.MaximumInstances, fixture.Broker.GetOpen().Count);

        var next = await fixture.OpenAsync("board", key: "extra", visible: false);

        Assert.AreEqual("ok", next.Status);
        Assert.AreEqual(DesktopCanvases.MaximumInstances, fixture.Broker.GetOpen().Count);
        var ids = fixture.Broker.GetOpen().Select(static info => info.InstanceId).ToHashSet();
        Assert.IsFalse(ids.Contains(first.InstanceId!), "the oldest hidden one went");
        Assert.IsTrue(ids.Contains(shown.InstanceId!), "a shown one stays");
        Assert.IsTrue(fixture.Plugin.Contexts[first.InstanceId!].Closed.IsCancellationRequested);
    }

    [TestMethod]
    public async Task ANewWatchReplacesTheOneBefore()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        var first = fixture.Watch();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];

        var second = fixture.Watch();
        await context.UpdateAsync("<p>for the new page</p>");

        Assert.AreEqual("<p>for the new page</p>", (await second.NextAsync("update")).Html);
        // A page that starts watching hears first that the plugins are there; the channel of a page ends once it was read.
        await first.NextAsync("plugins");
        await first.EndedAsync();
        Assert.IsFalse(first.Has("update"));
    }

    [TestMethod]
    public async Task WhenPluginsChange_ThePageIsTold_AndAnInstanceOfTheSamePluginIsLeftAlone()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenAsync("board", key: "k");

        await fixture.Broker.ReconcileAsync();

        await fixture.NextAsync("plugins");
        Assert.IsFalse(fixture.HasEvent("state"), "nothing changed for an instance whose plugin is the same");
        Assert.IsFalse(fixture.HasEvent("update"));
        Assert.AreEqual(1, fixture.Plugin.Opened);
    }

    [TestMethod]
    public async Task ACanvasThatTheNewVersionDoesNotDeclare_LeavesItsInstanceWaiting_AndTheOldPluginIsLetGo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var opened = await fixture.OpenAsync("board", key: "k");
        var context = fixture.Plugin.Contexts[opened.InstanceId!];
        Assert.IsTrue(context.IsOpen);

        // The plugin is still there but its contributions are gone: what a version without the canvas looks like.
        fixture.Runtime.Registry.RemoveByPlugin(Plugin);
        await fixture.Broker.ReconcileAsync();

        var state = await fixture.NextAsync("state");
        Assert.AreEqual((opened.InstanceId, "unknown_canvas"), (state.InstanceId, state.State));
        Assert.IsFalse(context.IsOpen, "what the old version held ends");
        Assert.AreEqual(0, fixture.Plugin.Closed.Count, "the old version is not asked to close what it lost");
        Assert.AreEqual("unknown_canvas", (await fixture.OpenAsync("board", key: "k")).Status);
        Assert.AreEqual(1, fixture.Broker.GetOpen().Count, "the tab still waits for its plugin");
        Assert.AreEqual("unknown", (await fixture.Service.ActionAsync(new(Epoch, opened.InstanceId, "tick", null, null), default)).Status);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ANewVersionOfAPlugin_OpensItsInstancesAgain_AndABuildThatFailsLeavesThemAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-canvases-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(global, "plugins", "notes")).FullName, "plugin.cs");
        File.WriteAllText(file, SourceOf("first"));
        var ui = new DesktopPluginUi();
        var broker = new DesktopCanvases(ui);
        var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global, Frontend = PluginFrontends.Desktop, IsHeadless = true,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker),
            });
            var package = runtime.GetPackages().Single(static candidate => candidate.Package.PackageId == "notes");
            if (package.Build is { Succeeded: false } build && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
                Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
            broker.Attach(runtime, () => "work");
            var service = new CanvasesService(broker, Epoch);
            var events = Channel.CreateUnbounded<CanvasEvent>();
            using var watching = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try { await foreach (var value in service.WatchAsync(new(Epoch), watching.Token)) events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The test is over. */ }
            });
            async Task<CanvasEvent> NextAsync(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (true)
                {
                    var value = await events.Reader.ReadAsync(timeout.Token);
                    if (value.Kind == kind) return value;
                }
            }

            var key = runtime.ActivePlugins.Single().Descriptor.RuntimeKey;
            var opened = await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default);
            Assert.AreEqual("ok", opened.Status);
            Assert.AreEqual("<p>first</p>", opened.Html);
            Assert.AreEqual("plugin:global:notes", opened.Package, "the page can name the folder of the plugin, to open its source or build it");
            var firstContext = (service.List(new(Epoch)).Canvases.Single().PluginKey, opened.InstanceId);

            // A new version: the instance is the same, opened again by the new code.
            File.WriteAllText(file, SourceOf("second"));
            Assert.AreEqual(PluginPackageChange.Reloaded, (await runtime.ReloadPackageAsync(package.Package)).Change);
            var update = await NextAsync("update");
            Assert.AreEqual((opened.InstanceId, "<p>second</p>", "ready", true), (update.InstanceId, update.Html, update.State, update.Actions));
            Assert.IsTrue(update.Revision > opened.Revision);
            Assert.AreEqual(1, broker.GetOpen().Count);
            Assert.AreEqual("<p>second</p>", (await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default)).Html);

            // A build that fails leaves the version that runs, and its instances.
            File.WriteAllText(file, SourceOf("third").Replace("PluginCanvasView.Html", "Missing.Html", StringComparison.Ordinal));
            Assert.AreEqual(PluginPackageChange.BuildFailed, (await runtime.ReloadPackageAsync(package.Package)).Change);
            Assert.AreEqual("<p>second</p>", (await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default)).Html);
            Assert.AreEqual(1, broker.GetOpen().Count);

            watching.Cancel();
            await pump;
            Assert.AreEqual(firstContext.PluginKey, key);
        }
        finally
        {
            broker.Dispose();
            await runtime.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private static string SourceOf(string text)
        => $$"""
using CodeAlta.Plugins.Abstractions;

[Plugin("notes")]
public sealed class NotesPlugin : PluginBase
{
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "board", Title = "Board",
            Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>{{text}}</p>", static (_, _, _) => ValueTask.FromResult(PluginCanvasActionResult.KeepOpen))),
        };
    }
}
""";

    [TestMethod]
    public async Task AltaView_ListsTheDeclaredCanvases_AndTheOpenInstancesWithTheirPlugin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var view = new DesktopAltaCanvases(fixture.Broker);

        var declared = view.List();

        CollectionAssert.AreEqual(new[] { "board", "notes", "run", "broken", "scripted", "scriptfile", "plain", "calls" }, declared.Select(static canvas => canvas.Id).ToArray());
        var board = declared[0];
        Assert.AreEqual((Plugin, "Canvas fixture", "Board", "A board.", "list-checks", "application", "{\"type\":\"object\"}", true),
            (board.PluginKey, board.Plugin, board.Title, board.Description, board.Icon, board.Scope, board.InputSchema, board.Describes));
        CollectionAssert.AreEqual(new[] { "add", "throw" }, board.Actions.Select(static action => action.Name).ToArray());
        Assert.AreEqual("Adds an item.", board.Actions[0].Description);
        Assert.AreEqual(("project", "session"), (declared[1].Scope, declared[2].Scope));
        Assert.IsNull(declared[6].Description);
        Assert.IsFalse(declared[6].Describes);

        var opened = await fixture.OpenAsync("board", space: "work", key: "k1");
        var open = view.ListOpen().Single();
        Assert.AreEqual((opened.InstanceId, Plugin, "board", "work", "k1", "Board k1", true), (open.InstanceId, open.PluginKey, open.CanvasId, open.SpaceId, open.Key, open.Title, open.Visible));
    }

    [TestMethod]
    public async Task AltaView_Open_NeedsAPageThatWatches()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        var view = new DesktopAltaCanvases(fixture.Broker);

        Assert.IsFalse(view.HasWindow);
        Assert.AreEqual("unavailable", (await view.OpenAsync(new(Plugin, "board", null, null, null, null), null, true, default)).Status);

        fixture.StartWatching();
        Assert.IsTrue(view.HasWindow);
    }

    [TestMethod]
    public async Task AltaView_Open_InTheShownSpace_AsksThePageToOpenTheTab()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Shown = "work";
        var view = new DesktopAltaCanvases(fixture.Broker);

        var opened = await view.OpenAsync(new(Plugin, "notes", "work", "p1", "s1", null), null, false, default);

        Assert.AreEqual(("requested", "work", true), (opened.Status, opened.SpaceId, opened.Shown));
        var request = await fixture.NextAsync("open");
        Assert.AreEqual((Plugin, "notes", "work", "p1", null, opened.InstanceId, false), (request.PluginKey, request.CanvasId, request.SpaceId, request.ProjectId, request.SessionId, request.InstanceId, request.Focus));
        Assert.AreEqual(0, fixture.Plugin.Opened + view.ListOpen().Count, "The page opens the instance when its tab mounts.");
    }

    [TestMethod]
    public async Task AltaView_Open_InAnotherSpace_AddsTheTabThereAndOpensTheInstanceHidden()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Shown = "work";
        var view = new DesktopAltaCanvases(fixture.Broker);
        var input = JsonSerializer.SerializeToElement(new { issue = 3 });

        var opened = await view.OpenAsync(new(Plugin, "board", "play", null, null, "k1"), input, true, default);

        Assert.AreEqual(("requested", "play", false), (opened.Status, opened.SpaceId, opened.Shown));
        var request = await fixture.NextAsync("open");
        Assert.AreEqual(("play", true), (request.SpaceId, request.Focus));
        // The plugin opened it, hidden, with the input: the canvas is listed and can be described and closed.
        Assert.AreEqual(1, fixture.Plugin.Opened);
        var instance = view.ListOpen().Single();
        Assert.AreEqual((opened.InstanceId, "play", false), (instance.InstanceId, instance.SpaceId, instance.Visible));
        Assert.AreEqual(3, fixture.Plugin.Contexts[opened.InstanceId!].Input!.Value.GetProperty("issue").GetInt32());

        // The tab of that space asks for its instance when the user shows the space: it is the same one, and shown now.
        var shown = await fixture.OpenAsync("board", space: "play", key: "k1");
        Assert.AreEqual(opened.InstanceId, shown.InstanceId);
        Assert.AreEqual(1, fixture.Plugin.Opened);
        Assert.IsTrue(view.ListOpen().Single().Visible);
    }

    [TestMethod]
    public async Task AltaView_Open_TakesNothingFromThePaneAndRefusesWhatIsMissing()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Shown = "work";
        var view = new DesktopAltaCanvases(fixture.Broker);

        Assert.AreEqual("invalid_request", (await view.OpenAsync(new(Plugin, "notes", "work", null, null, null), null, true, default)).Status, "A project canvas needs the project the command resolved.");
        Assert.AreEqual("invalid_request", (await view.OpenAsync(new(Plugin, "run", "work", "p1", null, null), null, true, default)).Status);
        Assert.AreEqual("unknown_canvas", (await view.OpenAsync(new(Plugin, "missing", "work", null, null, null), null, true, default)).Status);
        Assert.AreEqual("plugin_stopped", (await view.OpenAsync(new("builtin:gone", "board", "work", null, null, null), null, true, default)).Status);
        Assert.IsFalse(fixture.HasEvent("open"));
    }

    [TestMethod]
    public async Task AltaView_Close_ClosesTheInstanceAndTellsThePage()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Shown = "work";
        var view = new DesktopAltaCanvases(fixture.Broker);
        var target = new AltaCanvasTarget(Plugin, "board", "play", null, null, null);
        var opened = await view.OpenAsync(target, null, true, default);
        await fixture.NextAsync("open");

        Assert.IsFalse(await view.CloseAsync(target with { SpaceId = "work" }, default), "Another space's tab is another instance.");
        Assert.IsTrue(await view.CloseAsync(target, default));

        Assert.AreEqual(opened.InstanceId, (await fixture.NextAsync("closed")).InstanceId);
        Assert.AreEqual(0, view.ListOpen().Count);
        Assert.IsTrue(fixture.Plugin.Closed.Contains(opened.InstanceId!));
        Assert.IsFalse(await view.CloseAsync(target, default));
    }

    [TestMethod]
    public async Task AltaView_DescribesAndInvokes_WithoutAWindow()
    {
        await using var fixture = await Fixture.CreateAsync(watch: false);
        var view = new DesktopAltaCanvases(fixture.Broker);
        var target = new AltaCanvasTarget(Plugin, "board", null, null, null, null);

        Assert.AreEqual(new AltaCanvasDescription("ok", "# Board (closed)"), await view.DescribeAsync(target, default));
        var result = await view.InvokeAsync(target, "add", JsonSerializer.SerializeToElement(new { item = "milk" }), default);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("milk", result.Result!.Value.GetProperty("added").GetString());
        Assert.AreEqual("unknown_action", (await view.InvokeAsync(target, "nothing", null, default)).Status);
        Assert.AreEqual("failed", (await view.InvokeAsync(target, "throw", null, default)).Status);
        Assert.AreEqual("unknown_canvas", (await view.DescribeAsync(target with { CanvasId = "missing" }, default)).Status);
        Assert.AreEqual("plugin_stopped", (await view.InvokeAsync(target with { PluginKey = "builtin:gone" }, "add", null, default)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _watching = new();
        private readonly Channel<CanvasEvent> _events = Channel.CreateUnbounded<CanvasEvent>();
        private readonly List<Watcher> _watchers = [];

        private Fixture(string root, CanvasFixturePlugin plugin, PluginRuntimeManager runtime, DesktopCanvases broker, DesktopPluginUi ui, DesktopPluginServices services)
        {
            _root = root;
            Plugin = plugin;
            Runtime = runtime;
            Broker = broker;
            Ui = ui;
            PluginServices = new PluginServicesOf(services, broker.ForPlugin(CanvasesRpcTests.Plugin));
            Service = new CanvasesService(broker, Epoch);
        }

        public CanvasFixturePlugin Plugin { get; }

        public PluginRuntimeManager Runtime { get; }

        public DesktopCanvases Broker { get; }

        public DesktopPluginUi Ui { get; }

        public IPluginServices PluginServices { get; }

        public CanvasesService Service { get; }

        /// <summary>The space the window shows.</summary>
        public string? Shown { get; set; }

        public static async Task<Fixture> CreateAsync(bool watch = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-canvases-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var ui = new DesktopPluginUi();
            var broker = new DesktopCanvases(ui);
            var plugin = new CanvasFixturePlugin();
            var runtime = new PluginRuntimeManager();
            var services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker);
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global,
                Frontend = PluginFrontends.Desktop,
                Services = services,
                BuiltIns = [new BuiltInPluginDefinition { Id = "canvas-fixture", DisplayName = "Canvas fixture", PluginType = typeof(CanvasFixturePlugin), Factory = () => plugin }],
            });
            var fixture = new Fixture(root, plugin, runtime, broker, ui, services);
            broker.Attach(runtime, () => fixture.Shown);
            if (watch) fixture.StartWatching();
            return fixture;
        }

        /// <summary>Starts the page that watches: the one of the fixture, whose events <see cref="NextAsync"/> reads.</summary>
        public void StartWatching()
        {
            var generation = Broker.WatchGeneration;
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var value in Service.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value);
                }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            });
            _watchers.Add(new Watcher(pump, null));
            // The watch is registered once its first read started.
            SpinWait.SpinUntil(() => Broker.WatchGeneration > generation, TimeSpan.FromSeconds(5));
        }

        /// <summary>Starts another page that watches, with events of its own.</summary>
        public Page Watch()
        {
            var generation = Broker.WatchGeneration;
            var channel = Channel.CreateUnbounded<CanvasEvent>();
            var cancel = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try { await foreach (var value in Service.WatchAsync(new(Epoch), cancel.Token)) channel.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The test is over. */ }
                finally { channel.Writer.TryComplete(); }
            });
            SpinWait.SpinUntil(() => Broker.WatchGeneration > generation, TimeSpan.FromSeconds(5));
            return new Page(channel, pump, cancel);
        }

        public Task<CanvasOpenResponse> OpenAsync(string canvas, string? space = null, string? project = null, string? session = null, string? key = null, bool visible = true)
            => Service.OpenAsync(new(Epoch, Plugin_, canvas, space, project, session, key, visible), default);

        private const string Plugin_ = CanvasesRpcTests.Plugin;

        public async Task<CanvasEvent> NextAsync(string kind)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var value = await _events.Reader.ReadAsync(timeout.Token);
                if (value.Kind == kind) return value;
            }
        }

        /// <summary>Whether an event of a kind is waiting, once what was started has settled.</summary>
        public bool HasEvent(string kind)
        {
            Thread.Sleep(150);
            var kept = new List<CanvasEvent>();
            var found = false;
            while (_events.Reader.TryRead(out var value))
            {
                if (value.Kind == kind) found = true;
                else kept.Add(value);
            }

            foreach (var value in kept) _events.Writer.TryWrite(value);
            return found;
        }

        public async ValueTask DisposeAsync()
        {
            _watching.Cancel();
            foreach (var watcher in _watchers) await watcher.Pump;
            Broker.Dispose();
            await Runtime.DisposeAsync();
            _watching.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }

        private sealed record Watcher(Task Pump, object? Unused);

        public sealed class Page(Channel<CanvasEvent> events, Task pump, CancellationTokenSource cancel) : IDisposable
        {
            public async Task<CanvasEvent> NextAsync(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var value = await events.Reader.ReadAsync(timeout.Token);
                    if (value.Kind == kind) return value;
                }
            }

            public bool Has(string kind)
            {
                Thread.Sleep(150);
                while (events.Reader.TryRead(out var value)) if (value.Kind == kind) return true;
                return false;
            }

            /// <summary>Waits for the end of the watch: a page that was replaced is told nothing more.</summary>
            public Task EndedAsync() => events.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            public void Dispose()
            {
                cancel.Cancel();
                pump.Wait(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>The services of a plugin as the runtime gives them: the canvases are the plugin's own.</summary>
    private sealed class PluginServicesOf(IPluginServices inner, IPluginCanvasService canvases) : IPluginServices
    {
        public XenoAtom.Logging.Logger Logger => inner.Logger;

        public IPluginUiService Ui => inner.Ui;

        public IPluginStateStore State => inner.State;

        public IPluginWorkspaceService Workspace => inner.Workspace;

        public IPluginSessionService Sessions => inner.Sessions;

        public IPluginPromptService Prompts => inner.Prompts;

        public IPluginAgentService Agents => inner.Agents;

        public IPluginTaskService Tasks => inner.Tasks;

        public IPluginAltaService Alta => inner.Alta;

        public IPluginDatabase Database => inner.Database;

        public IPluginCanvasService Canvases => canvases;
    }

    public sealed class CanvasFixturePlugin : PluginBase
    {
        private int _opened;
        private int _brokenAttempts;

        public int Opened => Volatile.Read(ref _opened);

        public int BrokenAttempts => Volatile.Read(ref _brokenAttempts);

        public int Counter { get; set; }

        /// <summary>The module the scripted canvas gives; the number after it says which version.</summary>
        public const string ScriptText = "export default function Board() { return null; }";

        public ConcurrentDictionary<string, PluginCanvasContext> Contexts { get; } = new();

        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SlowCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Closed { get; } = new();

        public ConcurrentQueue<(string Instance, string Name, string? Value, IReadOnlyDictionary<string, string> Values)> Actions { get; } = new();

        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return new PluginCanvasContribution
            {
                Id = "board", Title = "Board", Description = "A board.", Icon = "list-checks", InputSchema = "{\"type\":\"object\"}",
                Open = async (canvas, _) =>
                {
                    Interlocked.Increment(ref _opened);
                    Contexts[canvas.InstanceId] = canvas;
                    await Task.Yield();
                    return PluginCanvasView.Html($"<p>board {canvas.Key}</p>", OnAction) with { Title = canvas.Key is null ? null : "Board " + canvas.Key };
                },
                Describe = (canvas, _) => ValueTask.FromResult<string?>(canvas.IsOpen ? "# Board (open)" : "# Board (closed)"),
                Closed = canvas => { Closed.Enqueue(canvas.InstanceId); return ValueTask.CompletedTask; },
                Actions =
                [
                    new PluginCanvasActionContribution
                    {
                        Name = "add", Description = "Adds an item.", InputSchema = "{\"type\":\"object\"}",
                        Handler = (_, input, _) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { added = input!.Value.GetProperty("item").GetString() })),
                    },
                    new PluginCanvasActionContribution { Name = "throw", Description = "Fails.", Handler = static (_, _, _) => throw new InvalidOperationException("secret") },
                ],
            };
            yield return new PluginCanvasContribution
            {
                Id = "notes", Title = "Notes", Scope = PluginCanvasScope.Project,
                Open = (canvas, _) =>
                {
                    Contexts[canvas.InstanceId] = canvas;
                    return ValueTask.FromResult(PluginCanvasView.Rendered((context, _) => ValueTask.FromResult($"<p>notes {context.ProjectId}: {Counter}</p>")));
                },
            };
            yield return new PluginCanvasContribution
            {
                Id = "run", Title = "Run", Scope = PluginCanvasScope.Session,
                Open = (canvas, _) =>
                {
                    Contexts[canvas.InstanceId] = canvas;
                    return ValueTask.FromResult(PluginCanvasView.Html("<p>run</p>"));
                },
            };
            yield return new PluginCanvasContribution
            {
                Id = "broken", Title = "Broken",
                Open = (_, _) =>
                {
                    Interlocked.Increment(ref _brokenAttempts);
                    throw new InvalidOperationException("secret");
                },
            };
            yield return new PluginCanvasContribution
            {
                Id = "scripted", Title = "Scripted",
                Open = (canvas, _) =>
                {
                    Contexts[canvas.InstanceId] = canvas;
                    return ValueTask.FromResult(PluginCanvasView.Html("<p>scripted</p>") with { ScriptSource = ScriptText });
                },
            };
            yield return new PluginCanvasContribution
            {
                Id = "scriptfile", Title = "Script file",
                Open = (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>file</p>") with { Script = PluginScript.File("ui/board.js") }),
            };
            yield return new PluginCanvasContribution
            {
                Id = "plain", Title = "Plain",
                Open = (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>plain</p>")),
            };
            yield return new PluginCanvasContribution
            {
                Id = "calls", Title = "Calls",
                Open = (canvas, _) =>
                {
                    Contexts[canvas.InstanceId] = canvas;
                    canvas.Rpc.Handle<JsonElement, JsonElement>("echo", (request, _) => ValueTask.FromResult(request));
                    canvas.Rpc.Handle<JsonElement, JsonElement>("where", (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { instance = canvas.InstanceId, key = canvas.Key })));
                    canvas.Rpc.Handle<JsonElement, JsonElement>("slow", async (_, cancellationToken) =>
                    {
                        SlowStarted.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                        catch (OperationCanceledException) { SlowCancelled.TrySetResult(); throw; }
                        return default;
                    });
                    return ValueTask.FromResult(PluginCanvasView.Html("<p>calls</p>") with { ScriptSource = ScriptText });
                },
            };
        }

        private ValueTask<PluginCanvasActionResult> OnAction(PluginCanvasContext canvas, PluginCanvasAction action, CancellationToken cancellationToken)
        {
            Actions.Enqueue((canvas.InstanceId, action.Name, action.Value, action.Values));
            return action.Name switch
            {
                "tick" => ValueTask.FromResult(PluginCanvasActionResult.Update($"<p>ticked {action.Value} {action.Values.GetValueOrDefault("note")}</p>")),
                "close" => ValueTask.FromResult(PluginCanvasActionResult.CloseCanvas()),
                "throw" => throw new InvalidOperationException("secret"),
                _ => ValueTask.FromResult(PluginCanvasActionResult.KeepOpen),
            };
        }
    }
}
