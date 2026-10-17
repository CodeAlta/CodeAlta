using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Search;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The Search canvas as a canvas of the built-in Search plugin: what it declares, how it opens, and the command that opens it.</summary>
[TestClass]
public sealed class DesktopSearchPluginTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public void ThePlugin_DeclaresTheCanvas_ItsCommand_AndItsLandingCard()
    {
        var definition = DesktopSearchPlugin.Definition(new DesktopSearchRelay());
        Assert.AreEqual(("search", "builtin:search"), (definition.Id, DesktopSearchPlugin.PluginKey));
        var plugin = definition.Factory();

        var canvas = plugin.GetCanvases().Single();
        Assert.AreEqual(("search", "Search", PluginCanvasScope.Application, "search"), (canvas.Id, canvas.Title, canvas.Scope, canvas.Icon));
        Assert.IsNotNull(canvas.Describe);
        // `/search` is the search window of the page: the canvas is `/search_index`.
        var command = plugin.GetCommands().Single();
        Assert.AreEqual(("search_index", "Search index", true), (command.Name, command.Label, command.ShowInCommandPalette));
        StringAssert.Contains(command.SearchText, "search index");
        var card = plugin.GetLandingCards().Single();
        Assert.AreEqual(("index", "Search"), (card.Id, card.Title));
        // Before the window has its search, there is nothing to say: no card, and the description says so.
        Assert.IsNull(DesktopSearchPlugin.LandingCard(null));
        StringAssert.StartsWith(DesktopSearchPlugin.Describe(null), "# Search\n\nThe search index is not available");
    }

    [TestMethod]
    public async Task TheCanvas_IsDrawnByTheModuleOfTheApplication_OnePerSpace_AndDescribesItself()
    {
        await using var fixture = await Fixture.CreateAsync();
        var canvases = new CanvasesService(fixture.Broker, Epoch);
        Assert.IsTrue(canvases.List(new(Epoch)).Canvases.Any(static canvas => canvas.PluginKey == "builtin:search" && canvas.Id == "search"));

        var opened = await canvases.OpenAsync(new(Epoch, DesktopSearchPlugin.PluginKey, DesktopSearchPlugin.CanvasId, "work", null, null, null, true), default);
        Assert.AreEqual(("ok", "Search", "/lib/app/search.js"), (opened.Status, opened.Title, opened.Script));
        Assert.IsNull(opened.ScriptProblem);
        Assert.AreEqual(opened.InstanceId, (await canvases.OpenAsync(new(Epoch, DesktopSearchPlugin.PluginKey, DesktopSearchPlugin.CanvasId, "work", null, null, null, true), default)).InstanceId);
        Assert.AreNotEqual(opened.InstanceId, (await canvases.OpenAsync(new(Epoch, DesktopSearchPlugin.PluginKey, DesktopSearchPlugin.CanvasId, "other", null, null, null, false), default)).InstanceId,
            "each space has its own tab");

        var described = await canvases.DescribeAsync(new(Epoch, opened.InstanceId), default);
        Assert.AreEqual("ok", described.Status);
        StringAssert.StartsWith(described.Markdown, "# Search");
        StringAssert.Contains(described.Markdown, "alta search");
    }

    [TestMethod]
    public async Task TheCommand_AsksTheWindowForTheTab_InTheSpaceOfThePane_AndTheCardIsAbsentWithoutAnIndex()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commands = (await fixture.Ui.ContributionsAsync(new(Epoch, null), default)).Commands;
        var open = commands.Single(static command => command.Name == "search_index");
        Assert.AreEqual(("builtin:search", "Search index", true, false, false), (open.PluginKey, open.Label, open.Palette, open.NeedsProject, open.NeedsSession));

        Assert.AreEqual("started", (await fixture.Ui.InvokeCommandAsync(new(Epoch, open.Id, null, null, false, null, "work"), default)).Status);
        var request = await fixture.NextCanvasEventAsync("open");
        Assert.AreEqual(("builtin:search", "search", "work", true, "Search", "search"), (request.PluginKey, request.CanvasId, request.SpaceId, request.Focus, request.Title, request.Icon));

        var cards = await fixture.Ui.LandingCardsAsync(new(Epoch, "work"), default);
        Assert.AreEqual("ok", cards.Status);
        Assert.IsFalse(cards.Cards.Any(static card => card.PluginKey == "builtin:search"), "nothing to say, no card");
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
            // Started on this thread, not on Task.Run: the page's watch is registered before the fixture is returned.
            _pump = PumpAsync();

            async Task PumpAsync()
            {
                try { await foreach (var value in canvases.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            }
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-search-canvas-" + Guid.NewGuid().ToString("N"));
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
                BuiltIns = [DesktopSearchPlugin.Definition(new DesktopSearchRelay())],
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
}
