using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWindowsTests
{
    [TestMethod]
    public void TheLabelOfAWindowNamesItsView()
    {
        Assert.AreEqual("main", DesktopWindows.LabelOf(DesktopWindows.MainId));
        Assert.AreEqual("window-a1b2c3d4", DesktopWindows.LabelOf("a1b2c3d4"));
        Assert.AreEqual(DesktopWindows.MainId, DesktopWindows.IdOf("main"));
        Assert.AreEqual("a1b2c3d4", DesktopWindows.IdOf("window-a1b2c3d4"));
        // A label that is no window's names none: the page of such a view is not a window of the application.
        Assert.IsNull(DesktopWindows.IdOf("window-"));
        Assert.IsNull(DesktopWindows.IdOf("window-../x"));
        Assert.IsNull(DesktopWindows.IdOf("canvas-1"));
        Assert.IsNull(DesktopWindows.IdOf(null));
    }

    [TestMethod]
    public void TheWindowsFile_KeepsTheWindowsForTheNextStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-windows-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Nothing written yet: the windows come back at a start, and there are none.
            var empty = DesktopWindowsFile.Load(root);
            Assert.IsTrue(empty.RestoreAtStartup);
            Assert.AreEqual(0, empty.Windows.Count);

            var file = new DesktopWindowsFile
            {
                RestoreAtStartup = false,
                Main = new DesktopWindowRecord { Id = "main", Color = "#336699" },
                Windows =
                [
                    new DesktopWindowRecord { Id = "a1b2c3d4", Name = "Reviews", Icon = "star", Color = "#d97706", Saved = true, Open = true, SpaceId = "default",
                        PinnedDisplayId = @"\\?\DISPLAY#DEL41A8#1", PinnedDisplayName = "DELL U2720Q", Tabs = ["session:s1", "changes:p1"], PageState = "{\"version\":1}" },
                ],
            };
            Assert.IsTrue(file.Save(root));
            Assert.IsTrue(File.Exists(Path.Combine(root, DesktopWindowsFile.FileName)));

            var read = DesktopWindowsFile.Load(root);
            Assert.IsFalse(read.RestoreAtStartup);
            Assert.AreEqual("#336699", read.Main?.Color);
            var window = read.Windows.Single();
            Assert.AreEqual("Reviews", window.Name);
            Assert.AreEqual("star", window.Icon);
            Assert.IsTrue(window.Saved && window.Open);
            Assert.AreEqual(@"\\?\DISPLAY#DEL41A8#1", window.PinnedDisplayId);
            CollectionAssert.AreEqual(new[] { "session:s1", "changes:p1" }, window.Tabs);
            Assert.AreEqual("{\"version\":1}", window.PageState);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TheWindowsFile_KeepsOnlyWhatItUnderstands()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-windows-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, DesktopWindowsFile.FileName);
            // A file that is not one: as if there were none.
            File.WriteAllText(path, "{\"windows\":");
            Assert.AreEqual(0, DesktopWindowsFile.Load(root).Windows.Count);

            // A window without a valid id, a second one with the same id, an icon and a color that are none.
            File.WriteAllText(path, """
                {"version":1,"windows":[
                  {"id":"../etc","name":"Bad"},
                  {"id":"abc123","name":"  Kept  ","icon":"not an icon!","color":"red","tabs":["session:a","session:a",""]},
                  {"id":"abc123","name":"Twice"}
                ]}
                """);
            var window = DesktopWindowsFile.Load(root).Windows.Single();
            Assert.AreEqual("abc123", window.Id);
            Assert.AreEqual("Kept", window.Name);
            Assert.IsNull(window.Icon);
            Assert.IsNull(window.Color);
            CollectionAssert.AreEqual(new[] { "session:a" }, window.Tabs);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ARequest_GoesToThePagesOfOneWindow()
    {
        var watchers = new DesktopPageWatchers<string>();
        var main = new List<string>();
        var second = new List<string>();
        using var mainPage = watchers.Watch("main", main.Add);
        using var secondPage = watchers.Watch("window-a1", second.Add);

        Assert.IsTrue(watchers.Publish("to the second", "window-a1"));
        Assert.IsTrue(watchers.Publish("to the main", "main"));
        // A window whose page does not watch (it is loading): its request goes to the main window rather than nowhere.
        Assert.IsTrue(watchers.Publish("to one that does not watch", "window-b2"));
        // No window named: every page, as with one window.
        Assert.IsTrue(watchers.Publish("to every page", null));
        CollectionAssert.AreEqual(new[] { "to the main", "to one that does not watch", "to every page" }, main);
        CollectionAssert.AreEqual(new[] { "to the second", "to every page" }, second);
    }

    [TestMethod]
    public void AnEditorRequest_GoesToTheWindowTheRouterChooses()
    {
        var view = new DesktopEditorView();
        var main = new List<ProjectFileShowEvent>();
        var second = new List<ProjectFileShowEvent>();
        using var mainPage = view.Watch("main", main.Add);
        using var secondPage = view.Watch("window-a1", second.Add);
        var asked = new List<(string Tab, string? Session)>();
        view.Router = (tab, session) => { asked.Add((tab, session)); return session == "s2" ? "window-a1" : "main"; };

        Assert.IsTrue(view.Open("p1", "src/A.cs", 3, null, callerSessionId: "s2"));
        Assert.IsTrue(view.Open("p2", null, null, null));
        Assert.AreEqual(("editor:p1", (string?)"s2"), asked[0]);
        Assert.AreEqual(("editor:p2", (string?)null), asked[1]);
        Assert.AreEqual("src/A.cs", second.Single().Path);
        Assert.AreEqual("p2", main.Single().ProjectId);

        // The changes and the guide are routed the same way, by the tab they open.
        var changes = new DesktopChangesView { Router = (tab, _) => { asked.Add((tab, null)); return "main"; } };
        using var changesPage = changes.Watch("main", _ => { });
        Assert.IsTrue(changes.Show("p1", null, null, "s1"));
        var documentation = new DesktopDocumentationView { Router = (tab, _) => { asked.Add((tab, null)); return "main"; } };
        using var documentationPage = documentation.Watch("main", _ => { });
        Assert.IsTrue(documentation.Show("readme.md", null, "s1"));
        CollectionAssert.AreEqual(new[] { "editor:p1", "editor:p2", "changes:p1", "documentation" }, asked.Select(static value => value.Tab).ToArray());
    }

    [TestMethod]
    public void EachWindowShowsItsSpace_AndOnlyTheMainOneSwitches()
    {
        var view = new DesktopSpaceView();
        var focused = "main";
        view.FocusedLabel = () => focused;
        var shown = new List<(string, string)>();
        view.ShownChanged += (label, space) => shown.Add((label, space));
        view.SetShown("main", "default");
        view.SetShown("window-a1", "work");

        Assert.AreEqual("default", view.ShownSpaceId);
        focused = "window-a1";
        Assert.AreEqual("work", view.ShownSpaceId, "the space of a new session is the one of the window the user used last");
        focused = "window-b2";
        Assert.AreEqual("default", view.ShownSpaceId, "a window that has not said its space yet: the main one's");
        CollectionAssert.AreEqual(new[] { ("main", "default"), ("window-a1", "work") }, shown);

        var main = new List<SpacesEvent>();
        var second = new List<SpacesEvent>();
        using var mainPage = view.Watch("main", main.Add);
        using var secondPage = view.Watch("window-a1", second.Add);
        Assert.IsTrue(view.Show("work"));
        view.NotifyChanged();
        CollectionAssert.AreEqual(new[] { "show", "changed" }, main.Select(static value => value.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "changed" }, second.Select(static value => value.Kind).ToArray(), "a secondary window keeps its space");
    }

    [TestMethod]
    public async Task ASecondaryPage_ClaimsItsSessions_WithoutDecidingTheRestoration()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        var rpc = new SessionRetentionProtectionService("epoch", (ids, ready) => protection.Publish("window", ids, ready),
            reason => protection.MarkUnknown("window", reason));
        using var cancellation = new CancellationTokenSource();
        await using var main = rpc.WatchLifetime(new("epoch", "main-page"), "main-document", cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await main.MoveNextAsync());
        Assert.IsTrue(main.Current.Accepted);

        // The page of a secondary window has its own lifetime beside the main one's.
        using var secondaryCancellation = new CancellationTokenSource();
        await using (var secondary = rpc.WatchSecondaryLifetime(new("epoch", "second-page"), "second-document", secondaryCancellation.Token).GetAsyncEnumerator())
        {
            Assert.IsTrue(await secondary.MoveNextAsync());
            Assert.IsTrue(secondary.Current.Accepted);
            Assert.IsTrue((await rpc.PublishForDocumentAsync(new("epoch", "second-page", 1, ["moved-out"], true), "second-document", default)).Accepted);
            Assert.IsNull(protection.TryReserveRetirement("other"), "a secondary page does not finish the restoration");
            Assert.IsTrue((await rpc.PublishForDocumentAsync(new("epoch", "main-page", 1, ["open"], true), "main-document", default)).Accepted);
            Assert.IsNull(protection.TryReserveRetirement("moved-out"));
            Assert.IsNull(protection.TryReserveRetirement("open"));
            using (var other = protection.TryReserveRetirement("other")) Assert.IsNotNull(other, "the main page restored");
            secondaryCancellation.Cancel();
        }

        // Its end releases nothing, and leaves the restoration as the main page made it.
        Assert.IsNull(protection.TryReserveRetirement("moved-out"));
        using (var other = protection.TryReserveRetirement("other")) Assert.IsNotNull(other);
    }

    [TestMethod]
    public async Task TheSessionsOfTheSecondaryWindows_AreProtectedBeforeTheirPagesClaimThem()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        var rpc = new SessionRetentionProtectionService("epoch", (ids, ready) => protection.Publish("window", ids, ready),
            reason => protection.MarkUnknown("window", reason)) { HostHeld = () => ["restored-in-a-window", " not canonical "] };
        using var cancellation = new CancellationTokenSource();
        await using var main = rpc.WatchLifetime(new("epoch", "main-page"), "main-document", cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await main.MoveNextAsync());
        Assert.IsTrue((await rpc.PublishForDocumentAsync(new("epoch", "main-page", 1, [], true), "main-document", default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("restored-in-a-window"));
        using (var other = protection.TryReserveRetirement("other")) Assert.IsNotNull(other);
    }
}
