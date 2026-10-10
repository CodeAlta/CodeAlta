using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The buttons that plugins put in the window, over a real plugin runtime that runs one fixture plugin.</summary>
[TestClass]
public sealed class PluginButtonsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task Requests_AreRefusedWithoutPluginsForAnotherStartAndForABadPlaceOrContext()
    {
        Assert.AreEqual("unavailable", (await new PluginUiService().ButtonsAsync(new(Epoch, null, null, null), default)).Status);

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ButtonsAsync(new("other", null, null, null), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ButtonsAsync(new(Epoch, null, null, "missing"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ButtonsAsync(new(Epoch, "Sideways", null, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ButtonsAsync(new(Epoch, "TitleBar", "bad\nspace", null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ButtonsAsync(new(Epoch, null, null, null, "bad\nsession"), default)).Status);
    }

    [TestMethod]
    public async Task Buttons_ListTheirPlaceIconLabelAndWhatTheyDo_InTheOrderOfTheContributions()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.ButtonsAsync(new(Epoch, null, "space-1", fixture.Project.Id), default);

        Assert.AreEqual("ok", response.Status);
        CollectionAssert.AreEqual(new[] { "stats", "run", "dot", "menu", "throws", "session" }, response.Buttons.Select(static button => button.ButtonId).ToArray(),
            "a button of a command or a canvas that its plugin does not have is left out");
        var stats = response.Buttons[0];
        Assert.AreEqual(("builtin:fixture", "fixture", "Fixture", "TitleBar", "chart-column", "Statistics"), (stats.PluginKey, stats.PluginId, stats.Plugin, stats.Place, stats.Icon, stats.Label));
        Assert.AreEqual(("board", "Application", null), (stats.Canvas, stats.CanvasScope, stats.CommandId));
        Assert.IsNull(stats.IconData);
        var run = response.Buttons.Single(static button => button.ButtonId == "run");
        Assert.IsNotNull(run.CommandId);
        Assert.IsNull(run.Canvas);
        Assert.AreEqual(("count", 3, "Warning"), (run.Badge, run.Count, run.Tone));
        Assert.AreEqual(("dot", false), (response.Buttons.Single(static button => button.ButtonId == "dot").Badge, false));

        var rail = await fixture.Service.ButtonsAsync(new(Epoch, "rail", null, null), default);
        Assert.AreEqual("dot", rail.Buttons.Single().ButtonId, "the place is read without regard to case");
        var menu = await fixture.Service.ButtonsAsync(new(Epoch, "ProjectMenu", null, fixture.Project.Id), default);
        CollectionAssert.AreEqual(new[] { "menu", "throws" }, menu.Buttons.Select(static button => button.ButtonId).ToArray());
    }

    [TestMethod]
    public async Task TheState_IsGivenTheContextOfTheCaller_AndTheContextDecidesWhatCanBeUsed()
    {
        await using var fixture = await Fixture.CreateAsync();

        var window = await fixture.Service.ButtonsAsync(new(Epoch, "TitleBar", "space-9", fixture.Project.Id, "session-9"), default);
        Assert.AreEqual("TitleBar:space-9:" + fixture.Project.Id + ":session-9", fixture.Plugin.LastContext);
        Assert.IsFalse(window.Buttons.Single(static button => button.ButtonId == "run").Disabled);

        // The title bar without a project: a command that needs one cannot be used, and a canvas about the application can.
        var withoutProject = await fixture.Service.ButtonsAsync(new(Epoch, null, "space-9", null), default);
        Assert.IsTrue(withoutProject.Buttons.Single(static button => button.ButtonId == "run").Disabled);
        Assert.IsFalse(withoutProject.Buttons.Single(static button => button.ButtonId == "stats").Disabled);
        var sessionMenu = await fixture.Service.ButtonsAsync(new(Epoch, "SessionMenu", null, fixture.Project.Id), default);
        var noSession = sessionMenu.Buttons.Single();
        Assert.AreEqual(("Session", true), (noSession.CanvasScope, noSession.Disabled), "a canvas about a session needs one");
        var withSession = await fixture.Service.ButtonsAsync(new(Epoch, "SessionMenu", null, fixture.Project.Id, "session-1"), default);
        Assert.IsFalse(withSession.Buttons.Single().Disabled);
    }

    [TestMethod]
    public async Task APluginThatHidesAButtonOrThrowsFromItsState_IsReportedWithoutBreakingTheList()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Hide = true;
        try
        {
            var response = await fixture.Service.ButtonsAsync(new(Epoch, null, null, fixture.Project.Id), default);
            Assert.IsTrue(response.Buttons.Single(static button => button.ButtonId == "dot").Hidden);
            Assert.AreEqual("ok", response.Status);
            Assert.AreEqual("none", response.Buttons.Single(static button => button.ButtonId == "throws").Badge, "a failing callback leaves the default state");
        }
        finally { fixture.Plugin.Hide = false; }
    }

    [TestMethod]
    public async Task ACommandOfAButton_RunsForTheContextOfTheButton_WithTheSpaceOfTheWindow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = (await fixture.Service.ButtonsAsync(new(Epoch, "ProjectMenu", "space-3", fixture.Project.Id), default)).Buttons.First();
        fixture.Plugin.Ran = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The project of the row, not the selected one: the page sends the context of the button.
        var started = await fixture.Service.InvokeCommandAsync(new(Epoch, run.CommandId, fixture.Project.Id, "session-4", false, null, "space-3"), default);

        Assert.AreEqual("started", started.Status);
        Assert.AreEqual($"{fixture.Project.Id}|session-4|space-3", await fixture.Plugin.Ran.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.AreEqual("invalid_request", (await fixture.Service.InvokeCommandAsync(new(Epoch, run.CommandId, null, null, false, null, "bad\nspace"), default)).Status);
    }

    [TestMethod]
    public async Task InvalidatingTheButtons_SendsAnEventToThePage()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Ui.InvalidateButtons();

        Assert.AreEqual("buttons", (await fixture.NextAsync("buttons")).Kind);
    }

    [TestMethod]
    public void TheIconOfAFile_IsRebuiltFromAShortListOfShapes_WithNothingThatRunsOrFetches()
    {
        const string dirty = """
            <?xml version="1.0"?>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24" onload="alert(1)" fill="none" stroke="currentColor">
              <script>alert(1)</script>
              <style>path { fill: url(https://evil.example/x) }</style>
              <image href="https://evil.example/p.png" width="24" height="24" />
              <foreignObject><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
              <use xlink:href="other.svg#a" />
              <a href="javascript:alert(1)"><path d="M1 1h4" /></a>
              <g stroke-width="2" style="fill:url(https://evil.example/y)" class="x">
                <path d="M2 2L8 8" fill="url(https://evil.example/z)" />
                <circle cx="12" cy="12" r="3" onclick="alert(2)" />
                <rect x="1" y="1" width="3" height="3" mask="url(#m)" />
              </g>
            </svg>
            """;

        var url = PluginIcons.Sanitize(dirty);

        Assert.IsNotNull(url);
        StringAssert.StartsWith(url, "data:image/svg+xml;base64,");
        var markup = Encoding.UTF8.GetString(Convert.FromBase64String(url["data:image/svg+xml;base64,".Length..]));
        foreach (var forbidden in new[] { "script", "style", "image", "foreignObject", "use", "href", "onload", "onclick", "evil", "javascript", "alert", "class", "xlink" })
            Assert.IsFalse(markup.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{forbidden} survived: {markup}");
        StringAssert.Contains(markup, "<circle");
        StringAssert.Contains(markup, "viewBox=\"0 0 24 24\"");
        StringAssert.Contains(markup, "mask=\"url(#m)\"", "a reference to a part of the same document stays");
        StringAssert.Contains(markup, "stroke-width=\"2\"");
    }

    [TestMethod]
    public void AnIconThatIsNotACleanSvg_IsRefused()
    {
        Assert.IsNull(PluginIcons.Sanitize("not xml"));
        Assert.IsNull(PluginIcons.Sanitize("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>"));
        Assert.IsNull(PluginIcons.Sanitize("<svg viewBox=\"0 0 1 1\"/>"), "no namespace");
        Assert.IsNull(PluginIcons.Sanitize("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><title>&x;</title></svg>"));
        Assert.IsNull(PluginIcons.Sanitize("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>"), "no box to scale");
        Assert.IsNotNull(PluginIcons.Sanitize("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><path d=\"M0 0\"/></svg>"), "the box comes from the size");
        Assert.IsNull(PluginIcons.Sanitize("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\">" + string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", PluginIcons.MaximumElements + 1)) + "</svg>"));
        Assert.IsNull(PluginIcons.Sanitize(new string(' ', PluginIcons.MaximumFileBytes + 1)));
    }

    [TestMethod]
    public void TheIconOfAPluginFile_IsReadFromThePackageFolderOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-plugin-icons-" + Guid.NewGuid().ToString("N"));
        var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        Directory.CreateDirectory(Path.Combine(package, "icons"));
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M0 0h24\"/></svg>";
        File.WriteAllText(Path.Combine(package, "icons", "statistics.svg"), svg);
        File.WriteAllText(Path.Combine(root, "outside.svg"), svg);
        File.WriteAllText(Path.Combine(package, "icons", "notes.txt"), svg);
        File.WriteAllText(Path.Combine(package, "icons", "big.svg"), svg + new string(' ', PluginIcons.MaximumFileBytes));
        var icons = new PluginIcons();
        try
        {
            Assert.IsNotNull(icons.Read(package, "icons/statistics.svg"));
            Assert.IsNotNull(icons.Read(package, "icons\\statistics.svg"));
            Assert.IsNotNull(icons.Read(package, "icons/statistics.svg"), "a second read is the same");
            Assert.IsNull(icons.Read(package, "../outside.svg"), "never outside the package");
            Assert.IsNull(icons.Read(package, Path.Combine(root, "outside.svg")), "never an absolute path");
            Assert.IsNull(icons.Read(package, "icons/notes.txt"), "svg files only");
            Assert.IsNull(icons.Read(package, "icons/big.svg"), "a size limit");
            Assert.IsNull(icons.Read(package, "icons/missing.svg"));
            Assert.IsNull(icons.Read(null, "icons/statistics.svg"), "a plugin of the application has no package folder");
            Assert.IsTrue(PluginIcons.IsFile("icons/a.svg"));
            Assert.IsTrue(PluginIcons.IsFile("a.SVG"));
            Assert.IsFalse(PluginIcons.IsFile("chart-column"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _watching = new();
        private readonly System.Threading.Channels.Channel<PluginUiEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<PluginUiEvent>();
        private readonly Task _pump;

        private Fixture(string root, ProjectDescriptor project, PluginRuntimeManager runtime, DesktopPluginUi ui, DesktopCanvases canvases, ProjectCatalog projects, FixturePlugin plugin)
        {
            _root = root;
            Plugin = plugin;
            Project = project;
            Runtime = runtime;
            Ui = ui;
            Canvases = canvases;
            Service = new PluginUiService(projects, runtime, ui, Epoch, canvases);
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var value in Service.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value);
                }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-plugin-buttons-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            var ui = new DesktopPluginUi();
            var canvases = new DesktopCanvases(ui);
            var runtime = new PluginRuntimeManager();
            var plugin = new FixturePlugin();
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global,
                Frontend = PluginFrontends.Desktop,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, canvases),
                BuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = () => plugin }],
            });
            canvases.Attach(runtime, () => "space-1");
            return new Fixture(root, project, runtime, ui, canvases, projects, plugin);
        }

        public ProjectDescriptor Project { get; }
        public FixturePlugin Plugin { get; }
        public PluginRuntimeManager Runtime { get; }
        public PluginUiService Service { get; }
        public DesktopPluginUi Ui { get; }
        public DesktopCanvases Canvases { get; }

        public async Task<PluginUiEvent> NextAsync(string kind)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var value = await _events.Reader.ReadAsync(timeout.Token);
                if (value.Kind == kind) return value;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.CloseAsync();
            _watching.Cancel();
            await _pump;
            Canvases.Dispose();
            await Runtime.DisposeAsync();
            _watching.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    public sealed class FixturePlugin : PluginBase
    {
        public string? LastContext { get; private set; }

        public bool Hide { get; set; }

        public TaskCompletionSource<string>? Ran { get; set; }

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return Command.Session("release.open", "Opens the release.", (context, _) =>
            {
                Ran?.TrySetResult($"{context.Workspace.SelectedProjectId}|{context.Sessions.SelectedSessionId}|{context.Workspace.SelectedSpaceId}");
                return ValueTask.FromResult(PluginCommandResult.Handled);
            }) with { Availability = PluginCommandAvailability.Always with { RequiresProject = true } };
        }

        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return new PluginCanvasContribution { Id = "board", Title = "Board", Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>board</p>")) };
            yield return new PluginCanvasContribution
            {
                Id = "run", Title = "Run", Scope = PluginCanvasScope.Session, Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>run</p>")),
            };
        }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "stats", "chart-column", "Statistics") with { Canvas = "board" };
            yield return PluginUi.Button(PluginButtonPlace.Rail, "dot", "bell", "Alerts", order: 2) with
            {
                Command = "release.open",
                GetState = _ => new PluginButtonState { Badge = PluginButtonBadge.Dot, Hidden = Hide },
            };
            yield return PluginUi.Button(PluginButtonPlace.TitleBar, "run", "play", "Run", order: 1) with
            {
                Command = "RELEASE.OPEN",
                GetState = context =>
                {
                    LastContext = $"{context.Place}:{context.SpaceId}:{context.ProjectId}:{context.SessionId}";
                    return new PluginButtonState { Badge = 3, Tone = PluginStatusTone.Warning };
                },
            };
            yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "menu", "box", "Release checklist", order: 3) with { Command = "release.open" };
            yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "throws", "box", "Throws", order: 4) with
            {
                Command = "release.open", GetState = static _ => throw new InvalidOperationException("boom"),
            };
            yield return PluginUi.Button(PluginButtonPlace.SessionMenu, "session", "box", "Run it", order: 5) with { Canvas = "run" };
            yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "gone", "box", "Gone", order: 6) with { Command = "not.there" };
            yield return PluginUi.Button(PluginButtonPlace.SessionMenu, "gone-canvas", "box", "Gone canvas", order: 7) with { Canvas = "not-there" };
        }
    }
}
