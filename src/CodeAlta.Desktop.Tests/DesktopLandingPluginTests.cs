using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The landing page as a plugin of the window: its canvas, the command that opens it, and <c>alta landing</c> over the real broker.</summary>
[TestClass]
public sealed class DesktopLandingPluginTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public void TheLandingPage_IsAPluginOfTheWindowOnly_WithTheNamesTheLiveToolUses()
    {
        var definition = DesktopLandingPlugin.Definition();

        Assert.AreEqual(("landing", typeof(DesktopLandingPlugin)), (definition.Id, definition.PluginType));
        Assert.AreEqual(("landing", "builtin:landing", "landing"), (AltaLanding.PluginId, AltaLanding.PluginKey, AltaLanding.CanvasId));
        Assert.IsFalse(DesktopPlugins.BuiltIns.Any(static plugin => plugin.Id == "landing"), "the plugins shared with the terminal application do not have it");
        var plugin = definition.Factory();
        var canvas = plugin.GetCanvases().Single();
        Assert.AreEqual(("landing", "Welcome", PluginCanvasScope.Application, "house"), (canvas.Id, canvas.Title, canvas.Scope, canvas.Icon));
        Assert.IsNotNull(canvas.Describe);
        Assert.AreEqual(0, canvas.Actions.Count);
        var command = plugin.GetCommands().Single();
        Assert.AreEqual(("landing", "Welcome", true), (command.Name, command.Label, command.ShowInCommandPalette));
        Assert.AreEqual(0, plugin.GetLandingCards().Count(), "the page pins no card on itself");
        Assert.AreEqual(0, plugin.GetAltaCommands().Count(), "`alta landing` is a command of the application, not of a plugin");
    }

    [TestMethod]
    public async Task TheCanvas_IsDeclaredFirst_DrawnByTheModuleOfTheApplication_AndDescribesItself()
    {
        await using var fixture = await Fixture.CreateAsync();
        var canvases = new CanvasesService(fixture.Broker, Epoch);

        var listed = canvases.List(new(Epoch));
        Assert.AreEqual("ok", listed.Status);
        Assert.AreEqual(("builtin:landing", "landing"), (listed.Canvases[0].PluginKey, listed.Canvases[0].Id), "before the canvases of the other plugins");

        var opened = await canvases.OpenAsync(new(Epoch, AltaLanding.PluginKey, AltaLanding.CanvasId, "work", null, null, null, true), default);
        Assert.AreEqual(("ok", "Welcome", "/lib/app/landing.js"), (opened.Status, opened.Title, opened.Script));
        Assert.IsNull(opened.ScriptProblem);
        Assert.IsFalse(opened.Actions);
        // One page for a space: opening it again is the same instance, and another space has its own.
        Assert.AreEqual(opened.InstanceId, (await canvases.OpenAsync(new(Epoch, AltaLanding.PluginKey, AltaLanding.CanvasId, "work", "ignored-project", null, null, true), default)).InstanceId);
        Assert.AreNotEqual(opened.InstanceId, (await canvases.OpenAsync(new(Epoch, AltaLanding.PluginKey, AltaLanding.CanvasId, "other", null, null, null, false), default)).InstanceId);

        var described = await canvases.DescribeAsync(new(Epoch, opened.InstanceId), default);
        Assert.AreEqual("ok", described.Status);
        StringAssert.StartsWith(described.Markdown, "# Welcome");

        // The tab is closed: the page keeps nothing, and opens again.
        Assert.AreEqual("ok", (await canvases.CloseAsync(new(Epoch, opened.InstanceId), default)).Status);
        Assert.AreEqual("ok", (await canvases.OpenAsync(new(Epoch, AltaLanding.PluginKey, AltaLanding.CanvasId, "work", null, null, null, true), default)).Status);
    }

    [TestMethod]
    public async Task TheLandingCommand_AsksTheWindowForTheTab_InTheSpaceOfThePane()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commands = (await fixture.Ui.ContributionsAsync(new(Epoch, null), default)).Commands;
        var landing = commands.Single(static command => command.Name == "landing");
        Assert.AreEqual(("builtin:landing", "Welcome", true, false, false), (landing.PluginKey, landing.Label, landing.Palette, landing.NeedsProject, landing.NeedsSession));

        Assert.AreEqual("started", (await fixture.Ui.InvokeCommandAsync(new(Epoch, landing.Id, null, null, false, null, "work"), default)).Status);

        var request = await fixture.NextCanvasEventAsync("open");
        Assert.AreEqual(("builtin:landing", "landing", "work", (string?)null, (string?)null, true, "Welcome", "house"),
            (request.PluginKey, request.CanvasId, request.SpaceId, request.ProjectId, request.SessionId, request.Focus, request.Title, request.Icon));
    }

    [TestMethod]
    public async Task AltaLandingOpen_AsksTheWindowForTheTab_AndSaysWhenThePluginIsOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var view = new DesktopAltaCanvases(fixture.Broker);
        Assert.IsTrue(view.List().Any(static canvas => canvas.PluginKey == AltaLanding.PluginKey && canvas.Id == AltaLanding.CanvasId && canvas.Scope == AltaCanvasScopes.Application));
        var services = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = fixture.Global }).Add(fixture.Projects).Add<IAltaCanvasView>(view);
        var registry = new AltaCommandRegistry();
        services.Add(registry);
        var dispatcher = new AltaCommandDispatcher(registry, services);
        // The page watches: a request reaches a window.
        await fixture.NextCanvasEventAsync("plugins");

        var result = await dispatcher.InvokeAsync(["landing", "open"], caller: new AltaCallerIdentity { Kind = "mcp" });

        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        StringAssert.Contains(result.Stdout, "\"type\":\"alta.landing.opened\"");
        var request = await fixture.NextCanvasEventAsync("open");
        Assert.AreEqual(("builtin:landing", "landing", "work", true), (request.PluginKey, request.CanvasId, request.SpaceId, request.Focus));

        // A host whose plugins do not have the page (it is turned off): the command says so, and asks the window for nothing.
        await using var without = await Fixture.CreateAsync(landing: false);
        var bare = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = without.Global }).Add(without.Projects).Add<IAltaCanvasView>(new DesktopAltaCanvases(without.Broker));
        var bareRegistry = new AltaCommandRegistry();
        bare.Add(bareRegistry);
        var refused = await new AltaCommandDispatcher(bareRegistry, bare).InvokeAsync(["landing", "open"], caller: new AltaCallerIdentity { Kind = "mcp" });
        Assert.AreEqual(AltaExitCodes.Unsupported, refused.ExitCode);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "landing.unavailable");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _watching = new();
        private readonly Channel<CanvasEvent> _events = Channel.CreateUnbounded<CanvasEvent>();
        private readonly Task _pump;

        private Fixture(string root, string global, ProjectCatalog projects, PluginRuntimeManager runtime, DesktopPluginUi ui, DesktopCanvases broker)
        {
            _root = root;
            Global = global;
            Projects = projects;
            Runtime = runtime;
            Broker = broker;
            Ui = new PluginUiService(projects, runtime, ui, Epoch, broker);
            var canvases = new CanvasesService(broker, Epoch);
            _pump = Task.Run(async () =>
            {
                try { await foreach (var value in canvases.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            });
        }

        public static async Task<Fixture> CreateAsync(bool landing = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-landing-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var ui = new DesktopPluginUi();
            var broker = new DesktopCanvases(ui);
            var runtime = new PluginRuntimeManager();
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global,
                Frontend = PluginFrontends.Desktop,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker),
                BuiltIns = landing ? [new BuiltInPluginDefinition { Id = "other", DisplayName = "Other", PluginType = typeof(OtherPlugin), Factory = static () => new OtherPlugin() }, DesktopLandingPlugin.Definition()]
                    : [new BuiltInPluginDefinition { Id = "other", DisplayName = "Other", PluginType = typeof(OtherPlugin), Factory = static () => new OtherPlugin() }],
            });
            broker.Attach(runtime, () => "work");
            return new Fixture(root, global, projects, runtime, ui, broker);
        }

        public string Global { get; }
        public ProjectCatalog Projects { get; }
        public PluginRuntimeManager Runtime { get; }
        public DesktopCanvases Broker { get; }
        public PluginUiService Ui { get; }

        public async Task<CanvasEvent> NextCanvasEventAsync(string kind)
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
            await Ui.CloseAsync();
            _watching.Cancel();
            await _pump;
            Broker.Dispose();
            await Runtime.DisposeAsync();
            _watching.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    /// <summary>Another plugin of the application with a canvas, listed after the landing page.</summary>
    public sealed class OtherPlugin : PluginBase
    {
        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return new PluginCanvasContribution { Id = "board", Title = "Board", Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>board</p>")) };
        }
    }
}
