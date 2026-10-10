using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

/// <summary>The plugin service of the window over a real plugin runtime that runs one fixture plugin.</summary>
[TestClass]
public sealed class PluginUiRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task Requests_AreRefusedWithoutPluginsForAnotherStartAndForAnUnknownProject()
    {
        var unavailable = new PluginUiService();
        Assert.AreEqual("unavailable", (await unavailable.ContributionsAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.RegionsAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.InvokeCommandAsync(new(Epoch, "x", null, null, false, null), default)).Status);
        Assert.AreEqual("unavailable", unavailable.Respond(new("ui-1", "ok", false, null, null, null)).Status);
        Assert.AreEqual("unavailable", (await unavailable.DialogActionAsync(new("ui-1", "a", null, null), default)).Status);

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ContributionsAsync(new("other", null), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ContributionsAsync(new(Epoch, "missing"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.RegionsAsync(new(Epoch, null, "bad\nsession"), default)).Status);
        var events = new List<PluginUiEvent>();
        await foreach (var value in fixture.Service.WatchAsync(new("other"), default)) events.Add(value);
        Assert.AreEqual(0, events.Count, "a page of another start watches nothing");
    }

    [TestMethod]
    public async Task Contributions_ListTheCommandsAndPickersOfThePlugins()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual(fixture.Project.Id, response.ProjectId);
        var hello = response.Commands.Single(static command => command.Name == "hello");
        Assert.AreEqual("builtin:fixture", hello.PluginKey);
        Assert.AreEqual("Fixture", hello.Plugin);
        Assert.AreEqual("Say hello", hello.Label);
        Assert.AreEqual("F9", hello.Keys);
        Assert.IsTrue(hello.Palette);
        Assert.IsFalse(hello.NeedsSession);
        var draft = response.Commands.Single(static command => command.Name == "draft");
        Assert.IsTrue(draft.NeedsSession);
        Assert.IsNull(draft.Keys);
        Assert.IsFalse(response.Commands.Any(static command => command.Name == "hidden"), "a command placed nowhere is not offered");

        // One picker per character, and never on a character CodeAlta uses itself.
        var picker = response.Pickers.Single();
        Assert.AreEqual(("!", "Fruits", "Search fruits", "Fixture"), (picker.Trigger, picker.Title, picker.Placeholder, picker.Plugin));
        Assert.IsTrue(response.Regions);
    }

    [TestMethod]
    public async Task Regions_GiveTheContentInItsRichestForm()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.RegionsAsync(new(Epoch, fixture.Project.Id, "session"), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("session", response.SessionId);
        CollectionAssert.AreEqual(
            new[] { ("footer", (string?)"<b>footer</b>", (string?)null, (string?)null), ("bar", null, "**bar**", null), ("status", null, null, "plain") },
            response.Items.Select(static item => (item.Region, item.Html, item.Markdown, item.Text)).ToArray());
        Assert.IsTrue(response.Items.All(static item => item.PluginKey == "builtin:fixture"));
    }

    [TestMethod]
    public async Task InvokeCommand_StartsTheCommand_AndItsOutcomeArrivesOnTheWatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commands = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Commands;
        string Id(string name) => commands.Single(command => command.Name == name).Id;

        Assert.AreEqual("unknown_command", (await fixture.Service.InvokeCommandAsync(new(Epoch, "missing", fixture.Project.Id, null, false, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.InvokeCommandAsync(new(Epoch, "", fixture.Project.Id, null, false, null), default)).Status);
        Assert.AreEqual("unavailable", (await fixture.Service.InvokeCommandAsync(new(Epoch, Id("draft"), fixture.Project.Id, null, false, null), default)).Status);

        Assert.AreEqual("started", (await fixture.Service.InvokeCommandAsync(new(Epoch, Id("hello"), fixture.Project.Id, null, false, null), default)).Status);
        Assert.AreEqual("hello ran", (await fixture.NextAsync("notify")).Message);

        // A command sees the pane it was started from, and its prompt goes to that session.
        Assert.AreEqual("started", (await fixture.Service.InvokeCommandAsync(new(Epoch, Id("draft"), fixture.Project.Id, "session-7", false, "typed"), default)).Status);
        var written = await fixture.NextAsync("draft");
        Assert.AreEqual(("session-7", "typed!"), (written.SessionId, written.Text));
        var prompt = await fixture.NextAsync("prompt");
        Assert.AreEqual(("send", "from the plugin", "session-7"), (prompt.Mode, prompt.Text, prompt.SessionId));
        Assert.AreEqual("ok", fixture.Service.Respond(new(prompt.RequestId, "ok", false, null, null, null)).Status);

        // A command that waits in a dialog ends when the page answers.
        Assert.AreEqual("started", (await fixture.Service.InvokeCommandAsync(new(Epoch, Id("ask"), fixture.Project.Id, null, false, null), default)).Status);
        var ask = await fixture.NextAsync("ask");
        Assert.AreEqual("confirm", ask.Dialog);
        Assert.AreEqual("unknown", fixture.Service.Respond(new("ui-0", "yes", false, null, null, null)).Status);
        Assert.AreEqual("invalid_request", fixture.Service.Respond(new(null, "yes", false, null, null, null)).Status);
        Assert.AreEqual("ok", fixture.Service.Respond(new(ask.RequestId, "yes", false, null, null, null)).Status);
        Assert.AreEqual("confirmed", (await fixture.NextAsync("notify")).Message);

        Assert.AreEqual("started", (await fixture.Service.InvokeCommandAsync(new(Epoch, Id("fail"), fixture.Project.Id, null, false, null), default)).Status);
        var failure = await fixture.NextAsync("notify");
        Assert.AreEqual("warning", failure.Tone);
        Assert.IsFalse(failure.Message!.Contains("secret", StringComparison.Ordinal), "the text of a plugin failure stays out of the page");
    }

    [TestMethod]
    public async Task ACommandOrADialogActionThatEnded_TellsThePageToReadAgainWhatPluginsShow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commands = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Commands;
        string Id(string name) => commands.Single(command => command.Name == name).Id;

        // The status item of a plugin usually shows what its command just changed, whether the command ended well or not.
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("hello"), fixture.Project.Id, null, false, null), default);
        Assert.AreEqual("hello ran", (await fixture.NextAsync("notify")).Message);
        await fixture.NextAsync("refresh");
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("fail"), fixture.Project.Id, null, false, null), default);
        await fixture.NextAsync("refresh");

        // So does an action of a dialog that stays open.
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("form"), fixture.Project.Id, null, false, null), default);
        var form = await fixture.NextAsync("ask");
        var action = await fixture.Service.DialogActionAsync(new(form.RequestId, "count", null, null), default);
        Assert.AreEqual(("ok", "<b>1</b>", false), (action.Status, action.Html, action.Closed));
        await fixture.NextAsync("refresh");
        Assert.AreEqual("ok", fixture.Service.Respond(new(form.RequestId, "ok", false, null, null, null)).Status);
        await fixture.NextAsync("refresh");
    }

    [TestMethod]
    public async Task TheScriptOfADialog_ARegionAndACard_IsServedForThePluginThatGaveIt()
    {
        await using var fixture = await Fixture.CreateAsync(scripted: true);
        var commands = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Commands;
        string Id(string name) => commands.Single(command => command.Name == name).Id;
        string Source(string? path) => Encoding.UTF8.GetString(fixture.Modules.GetResponse(new NeoResourceRequest(new Uri("app://codealta" + path), "GET", new Dictionary<string, string>(), null, NeoResourceKind.Script, false, default))!.Bytes.Span);

        // A dialog with a script: the page is given the path of the module of the plugin that asked, and answers as it always did.
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("dialog"), fixture.Project.Id, null, false, null), default);
        var ask = await fixture.NextAsync("ask");
        Assert.AreEqual("html", ask.Dialog);
        StringAssert.StartsWith(ask.Script, "/plugin/");
        Assert.IsNull(ask.ScriptProblem);
        Assert.AreEqual(ScriptedPlugin.DialogScript, Source(ask.Script));
        Assert.AreEqual("ok", fixture.Service.Respond(new(ask.RequestId, "ok", false, null, null, null)).Status);

        // A script the host cannot serve (a file of a plugin that has no package) is told to the page, with no path.
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("missing"), fixture.Project.Id, null, false, null), default);
        var missing = await fixture.NextAsync("ask");
        Assert.IsNull(missing.Script);
        Assert.AreEqual("The script of the dialog could not be found.", missing.ScriptProblem);
        Assert.AreEqual("ok", fixture.Service.Respond(new(missing.RequestId, "ok", false, null, null, null)).Status);

        // A dialog without a script, from the same plugin, has neither.
        await fixture.Service.InvokeCommandAsync(new(Epoch, Id("plain"), fixture.Project.Id, null, false, null), default);
        var plain = await fixture.NextAsync("ask");
        Assert.IsNull(plain.Script);
        Assert.IsNull(plain.ScriptProblem);
        Assert.AreEqual("ok", fixture.Service.Respond(new(plain.RequestId, "ok", false, null, null, null)).Status);

        // A region: the content that has a script says where it is; the one that has none does not.
        var regions = await fixture.Service.RegionsAsync(new(Epoch, fixture.Project.Id, "session"), default);
        var footer = regions.Items.Single(static item => item.PluginKey == "builtin:scripted" && item.Region == "footer");
        Assert.AreEqual(ScriptedPlugin.RegionScript, Source(footer.Script));
        Assert.IsNull(footer.ScriptProblem);
        Assert.IsTrue(regions.Items.Where(static item => item.PluginKey == "builtin:scripted" && item.Region != "footer").All(static item => item.Script is null));
        Assert.IsTrue(regions.Items.Where(static item => item.PluginKey == "builtin:fixture").All(static item => item.Script is null));

        // A card of the timeline.
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        AgentEvent[] journal =
        [
            new AgentContentCompletedEvent(new("provider"), "session", start, new("run"), AgentContentKind.User, "c1", null, "hi"),
            new AgentSessionUpdateEvent(new("provider"), "session", start.AddSeconds(4), new("run"), AgentSessionUpdateKind.Idle, null),
        ];
        var service = new SessionPluginEventsService(
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([.. journal.Select(static (item, index) => new AgentSessionHistoryEntry(index + 1, item))], null, false)),
            static (_, _) => Task.FromResult(false), Epoch, fixture.Projects, fixture.Runtime, fixture.Modules);
        var cards = (await service.ReadAsync(new(Epoch, "session", fixture.Project.Id, start), default)).Events;
        var scripted = cards.Single(static card => card.PluginId == "builtin:scripted");
        Assert.AreEqual(ScriptedPlugin.CardScript, Source(scripted.Script));
        Assert.IsNull(scripted.ScriptProblem);
        Assert.IsNull(cards.Single(static card => card.PluginId == "builtin:fixture").Script);
    }

    [TestMethod]
    public async Task TheScriptOfAPluginThatIsNotActive_IsNotServed()
    {
        await using var fixture = await Fixture.CreateAsync(scripted: true);
        var script = PluginScript.Inline("export default () => null;");

        Assert.IsNotNull(fixture.Modules.PublishFor("builtin:scripted", script));
        Assert.IsNull(fixture.Modules.PublishFor("builtin:not-there", script));
    }

    [TestMethod]
    public async Task SearchPicker_ReturnsTheItemsOfThePlugin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var picker = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Pickers.Single();

        var found = await fixture.Service.SearchPickerAsync(new(Epoch, picker.Id, fixture.Project.Id, "session", "b"), default);

        Assert.AreEqual("ok", found.Status);
        CollectionAssert.AreEqual(new[] { new PluginUiPickerItem("banana", "6 letters", "fruit:banana "), new PluginUiPickerItem("blueberry", "9 letters", "fruit:blueberry ") }, found.Items);
        Assert.AreEqual("unknown_picker", (await fixture.Service.SearchPickerAsync(new(Epoch, "missing", fixture.Project.Id, null, ""), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.SearchPickerAsync(new(Epoch, picker.Id, fixture.Project.Id, null, new string('x', PluginUiService.MaximumQueryUnits + 1)), default)).Status);
        Assert.AreEqual("failed", (await fixture.Service.SearchPickerAsync(new(Epoch, picker.Id, fixture.Project.Id, null, "throw"), default)).Status);
    }

    [TestMethod]
    public async Task Close_EndsACommandThatWaitsInADialog()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var commands = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Commands;
            await fixture.Service.InvokeCommandAsync(new(Epoch, commands.Single(static command => command.Name == "ask").Id, fixture.Project.Id, null, false, null), default);
            await fixture.NextAsync("ask");

            await fixture.Service.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual("closed", (await fixture.Service.InvokeCommandAsync(new(Epoch, commands[0].Id, fixture.Project.Id, null, false, null), default)).Status);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ComposerStatus_NamesTheCommandOfAnItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commands = (await fixture.Service.ContributionsAsync(new(Epoch, fixture.Project.Id), default)).Commands;
        var service = new ComposerStatusService(fixture.Projects, Epoch, fixture.Home, fixture.Runtime);

        var response = await service.ReadAsync(new(Epoch, fixture.Project.Id), default);

        var item = response.Items.Single(static item => item.Label == "Fixture");
        Assert.AreEqual("ready", item.Text);
        Assert.AreEqual(commands.Single(static command => command.Name == "hello").Id, item.CommandId);
        Assert.IsNull(response.Items.Single(static item => item.Label == "Plain").CommandId, "an unknown command name gives no command");
    }

    [TestMethod]
    public async Task SessionEvents_IncludeTheCardsOfOtherPluginsWithTheirHtml()
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        AgentEvent[] journal =
        [
            new AgentContentCompletedEvent(new("provider"), "session", start, new("run"), AgentContentKind.User, "c1", null, "hi"),
            new AgentContentCompletedEvent(new("provider"), "session", start.AddSeconds(2), new("run"), AgentContentKind.Assistant, "c2", null, "hello"),
            new AgentSessionUpdateEvent(new("provider"), "session", start.AddSeconds(4), new("run"), AgentSessionUpdateKind.Idle, null),
        ];
        var service = new SessionPluginEventsService(
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([.. journal.Select(static (item, index) => new AgentSessionHistoryEntry(index + 1, item))], null, false)),
            static (_, _) => Task.FromResult(false), Epoch, fixture.Projects, fixture.Runtime);

        var response = await service.ReadAsync(new(Epoch, "session", fixture.Project.Id, start), default);

        Assert.AreEqual("ok", response.Status);
        var card = response.Events.Single();
        Assert.AreEqual("builtin:fixture", card.PluginId);
        Assert.AreEqual("**Fixture** · 3 events", card.Markdown);
        Assert.AreEqual("<b>3</b> events", card.Html);
        Assert.AreEqual(new SessionPluginEventDetail("Details", "three", "<i>three</i>"), card.Details.Single());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _watching = new();
        private readonly System.Threading.Channels.Channel<PluginUiEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<PluginUiEvent>();
        private readonly Task _pump;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, PluginRuntimeManager runtime, DesktopPluginUi ui, DesktopPluginModules modules)
        {
            _root = root;
            Projects = projects;
            Project = project;
            Runtime = runtime;
            Modules = modules;
            Service = new PluginUiService(projects, runtime, ui, Epoch, null, modules);
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var value in Service.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value);
                }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            });
        }

        public static async Task<Fixture> CreateAsync(bool scripted = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-plugin-ui-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            var modules = new DesktopPluginModules();
            var ui = new DesktopPluginUi { Modules = modules };
            var runtime = new PluginRuntimeManager();
            List<BuiltInPluginDefinition> builtIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = static () => new FixturePlugin() }];
            if (scripted) builtIns.Add(new BuiltInPluginDefinition { Id = "scripted", DisplayName = "Scripted", PluginType = typeof(ScriptedPlugin), Factory = static () => new ScriptedPlugin() });
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global,
                Frontend = PluginFrontends.Desktop,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui),
                BuiltIns = builtIns,
            });
            modules.Attach(runtime);
            return new Fixture(root, projects, project, runtime, ui, modules);
        }

        public DesktopPluginModules Modules { get; }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public PluginRuntimeManager Runtime { get; }
        public PluginUiService Service { get; }
        public string Home => Path.Combine(_root, "home");

        /// <summary>The next event of a kind, skipping the others.</summary>
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
            await Runtime.DisposeAsync();
            _watching.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    /// <summary>A plugin whose dialog, region content and card have a script.</summary>
    public sealed class ScriptedPlugin : PluginBase
    {
        public const string DialogScript = "export default function Dialog() { return null; }";
        public const string RegionScript = "export function mount() { }";
        public const string CardScript = "export default function Card() { return null; }";

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            var button = new PluginDialogButton { Name = "ok", Label = "Close", IsDefault = true };
            yield return Command.Shell("dialog", "Shows a dialog with a script.", static async (context, cancellationToken) =>
            {
                await context.Ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("Scripted", "<p>x</p>", new PluginDialogButton { Name = "ok", Label = "Close", IsDefault = true }) with { Script = PluginScript.Inline(DialogScript) }, cancellationToken);
                return PluginCommandResult.Handled;
            });
            yield return Command.Shell("missing", "Shows a dialog whose script is a file.", static async (context, cancellationToken) =>
            {
                await context.Ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("Missing", "<p>x</p>", new PluginDialogButton { Name = "ok", Label = "Close", IsDefault = true }) with { Script = PluginScript.File("ui/dialog.js") }, cancellationToken);
                return PluginCommandResult.Handled;
            });
            yield return Command.Shell("plain", "Shows a dialog without script.", static async (context, cancellationToken) =>
            {
                await context.Ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("Plain", "<p>x</p>", new PluginDialogButton { Name = "ok", Label = "Close", IsDefault = true }), cancellationToken);
                return PluginCommandResult.Handled;
            });
            _ = button;
        }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Content(PluginUiRegion.SessionFooter, static _ => PluginRenderResult.FromHtml("<b>scripted</b>", "scripted") with { Script = PluginScript.Inline(RegionScript) }, "footer");
            yield return PluginUi.Content(PluginUiRegion.SessionStatus, static _ => new PluginRenderResult { Text = "plain", Script = PluginScript.Inline(RegionScript) }, "status");
        }

        public override IEnumerable<PluginSessionEventProjectionContribution> GetSessionEventProjections()
        {
            yield return new PluginSessionEventProjectionContribution
            {
                Name = "card",
                ProjectAsync = static (context, _) => ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(
                [
                    new PluginDerivedSessionEvent { EventId = "scripted:" + context.SessionId, Markdown = "**Scripted**", Html = "<b>card</b>", Script = PluginScript.Inline(CardScript) },
                ]),
            };
        }
    }

    public sealed class FixturePlugin : PluginBase
    {
        private static readonly string[] Fruits = ["apple", "banana", "blueberry"];

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return Command.Shell("hello", "Says hello.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Message("hello ran")))
                with { Label = "Say hello", KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F9)) };
            yield return Command.Session("draft", "Edits the draft and sends a prompt.", static async (context, cancellationToken) =>
            {
                await context.Prompts.SetDraftTextAsync(context.Prompts.DraftText + "!", cancellationToken);
                return new PluginCommandResult { Disposition = PluginCommandDisposition.Handled, PromptText = "from the plugin" };
            });
            yield return Command.Shell("ask", "Asks.", static async (context, cancellationToken) =>
                PluginCommandResult.Message(await context.Ui.ConfirmAsync("Fixture", "Continue?", cancellationToken) ? "confirmed" : "refused"));
            yield return Command.Shell("fail", "Fails.", static (_, _) => throw new InvalidOperationException("secret"));
            yield return Command.Shell("form", "Counts in a dialog.", static async (context, cancellationToken) =>
            {
                var count = 0;
                await context.Ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("Form", "<b>0</b>", new PluginDialogButton { Name = "ok", Label = "Close", IsDefault = true }) with
                {
                    OnAction = (_, _) => ValueTask.FromResult(PluginDialogActionResult.Update($"<b>{++count}</b>")),
                }, cancellationToken);
                return PluginCommandResult.Handled;
            });
            yield return Command.Shell("hidden", "Placed nowhere.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled)) with { Placement = PluginCommandPlacement.None };
        }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return PluginUi.Content(PluginUiRegion.SessionFooter, static _ => PluginRenderResult.FromHtml("<b>footer</b>", "footer"), "footer");
            yield return PluginUi.Content(PluginUiRegion.CommandBar, static _ => new PluginRenderResult { Markdown = "**bar**", Text = "bar" }, "bar");
            yield return PluginUi.Content(PluginUiRegion.SessionStatus, static _ => new PluginRenderResult { Text = "plain" }, "status");
            yield return PluginUi.Content(PluginUiRegion.SessionStatus, static _ => null, "nothing");
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus, Name = "ready",
                GetStatus = static _ => new PluginStatusItem { Label = "Fixture", Text = "ready", Command = "HELLO" },
            };
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus, Name = "plain",
                GetStatus = static _ => new PluginStatusItem { Label = "Plain", Text = "text", Command = "missing" },
            };
        }

        public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
        {
            yield return PluginUi.PromptPicker("fruit", '!', "Fruits", static (context, _) => context.Query == "throw"
                ? throw new InvalidOperationException("secret")
                : ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([.. Fruits.Where(fruit => fruit.StartsWith(context.Query, StringComparison.Ordinal))
                    .Select(static fruit => new PluginPromptPickerItem { Label = fruit, Description = $"{fruit.Length} letters", InsertText = $"fruit:{fruit} " })]),
                "Search fruits");
            yield return PluginUi.PromptPicker("mention", '@', "Taken", static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([]));
        }

        public override IEnumerable<PluginSessionEventProjectionContribution> GetSessionEventProjections()
        {
            yield return new PluginSessionEventProjectionContribution
            {
                Name = "card",
                ProjectAsync = static (context, _) => ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(
                [
                    new PluginDerivedSessionEvent
                    {
                        EventId = "fixture:" + context.SessionId,
                        Markdown = $"**Fixture** · {context.Events.Count} events",
                        Html = $"<b>{context.Events.Count}</b> events",
                        DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "Details", Markdown = "three", Html = "<i>three</i>" }],
                    },
                ]),
            };
        }
    }
}
