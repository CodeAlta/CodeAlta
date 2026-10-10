using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The cards that plugins pin on the landing page, over a real plugin runtime that runs fixture plugins.</summary>
[TestClass]
public sealed class PluginLandingCardsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task Requests_AreRefusedWithoutPluginsForAnotherStartAndForABadSpace()
    {
        Assert.AreEqual("unavailable", (await new PluginUiService().LandingCardsAsync(new(Epoch, null), default)).Status);

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.LandingCardsAsync(new("other", null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.LandingCardsAsync(new(Epoch, "bad\nspace"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.LandingCardsAsync(new(Epoch, string.Empty), default)).Status);
        var refused = await fixture.Service.LandingCardsAsync(new("other", "space-1"), default);
        Assert.AreEqual(("space-1", 0), (refused.SpaceId, refused.Cards.Length));
    }

    [TestMethod]
    public async Task Cards_ListTheirTitleIconContentAndActions_InTheOrderOfTheContributions()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.LandingCardsAsync(new(Epoch, "space-1"), default);

        Assert.AreEqual(("ok", "space-1"), (response.Status, response.SpaceId));
        CollectionAssert.AreEqual(new[] { "first", "numbers", "throws" }, response.Cards.Select(static card => card.CardId).ToArray(), "a card its plugin leaves out is not listed");
        var numbers = response.Cards.Single(static card => card.CardId == "numbers");
        Assert.AreEqual(("builtin:fixture", "fixture", "Fixture", "Numbers", "chart-column", "ok"), (numbers.PluginKey, numbers.PluginId, numbers.Plugin, numbers.Title, numbers.Icon, numbers.State));
        Assert.AreEqual(("<p>3 open</p>", "3 open", "Warning"), (numbers.Html, numbers.StatusText, numbers.Tone));
        Assert.IsNull(numbers.IconData);
        Assert.AreEqual(((string?)null, (string?)null), (numbers.ProjectId, numbers.ProjectName), "a plugin of the application is about no project");
        Assert.AreEqual(new PluginLandingCardContext("space-1", null), fixture.Plugin.LastContext);

        // The actions that name a command or a canvas the plugin has, three at most.
        CollectionAssert.AreEqual(new[] { "Refresh", "Open", "Open one" }, numbers.Actions.Select(static action => action.Label).ToArray());
        var refresh = numbers.Actions[0];
        Assert.IsNotNull(refresh.CommandId);
        Assert.AreEqual(((string?)null, (string?)null, true, false, "refresh-cw"), (refresh.Canvas, refresh.Key, refresh.Primary, refresh.Disabled, refresh.Icon));
        var open = numbers.Actions[1];
        Assert.AreEqual(("board", "Application", (string?)null, (string?)null, false), (open.Canvas, open.CanvasScope, open.CommandId, open.Key, open.Disabled));
        Assert.AreEqual(("board", "item:1"), (numbers.Actions[2].Canvas, numbers.Actions[2].Key));

        // What an element of the fragment can name: the commands of the plugin of the card, and of no other plugin.
        CollectionAssert.AreEquivalent(new[] { "numbers.refresh", "release.open" }, numbers.Commands.Select(static command => command.Name).ToArray());
        Assert.AreEqual(refresh.CommandId, numbers.Commands.Single(static command => command.Name == "numbers.refresh").Id);
        Assert.AreEqual(0, response.Cards.Single(static card => card.CardId == "throws").Commands.Length, "a card that failed names nothing");

        var first = response.Cards[0];
        Assert.AreEqual(("ok", (string?)null, "Info", 0), (first.State, first.StatusText, first.Tone, first.Actions.Length));
        Assert.IsNull(first.Icon);
    }

    [TestMethod]
    public async Task ACardThatFails_IsListedWithoutContent_AndTheOthersAreNotAffected()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.LandingCardsAsync(new(Epoch, null), default);

        var failed = response.Cards.Single(static card => card.CardId == "throws");
        Assert.AreEqual(("failed", (string?)null, (string?)null, 0, "Throws"), (failed.State, failed.Html, failed.StatusText, failed.Actions.Length, failed.Title));
        Assert.AreEqual("ok", response.Cards.Single(static card => card.CardId == "numbers").State);
        Assert.IsFalse(response.Cards.Any(static card => card.Html?.Contains("boom", StringComparison.Ordinal) == true), "the text of a failure does not cross to the page");
    }

    [TestMethod]
    public async Task ACardThatDoesNotAnswer_FailsAtTheLimit_AndTheOthersAreListed()
    {
        await using var fixture = await Fixture.CreateAsync(TimeSpan.FromSeconds(2));
        fixture.Plugin.Stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var response = await fixture.Service.LandingCardsAsync(new(Epoch, null), default).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual("failed", response.Cards.Single(static card => card.CardId == "first").State);
            Assert.AreEqual("ok", response.Cards.Single(static card => card.CardId == "numbers").State);
        }
        finally { fixture.Plugin.Stuck.TrySetResult(); }
    }

    [TestMethod]
    public async Task WhatACardShows_IsCutAndItsActionsAreChecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Shown = new PluginLandingCard
        {
            Html = new string('x', PluginLandingCardLimits.MaximumHtmlLength + 50), Status = "line one\nline two" + new string('s', 100),
            Actions =
            [
                new PluginLandingCardAction { Label = "Both", Command = "release.open", Canvas = "board" },
                PluginLandingCardAction.RunCommand("Gone", "not.there"),
                PluginLandingCardAction.OpenCanvas("Gone canvas", "not-there"),
                PluginLandingCardAction.OpenCanvas("Bad key", "board", "bad\nkey"),
                PluginLandingCardAction.RunCommand("Needs a project", "release.open"),
                PluginLandingCardAction.OpenCanvas("Of a session", "run"),
                PluginLandingCardAction.OpenCanvas("Of a project", "plan"),
                PluginLandingCardAction.OpenCanvas("One too many", "board"),
            ],
        };

        var card = (await fixture.Service.LandingCardsAsync(new(Epoch, null), default)).Cards.Single(static card => card.CardId == "numbers");

        Assert.AreEqual(PluginLandingCardLimits.MaximumHtmlLength, card.Html!.Length);
        Assert.IsTrue(card.StatusText!.Length <= PluginLandingCardLimits.MaximumLabelLength && !card.StatusText.Contains('\n'), "the status is one short line");
        CollectionAssert.AreEqual(new[] { "Needs a project", "Of a session", "Of a project" }, card.Actions.Select(static action => action.Label).ToArray(),
            "an action that is not valid, or names what the plugin does not have, is left out, and three are kept");
        Assert.IsTrue(card.Actions.All(static action => action.Disabled), "the landing page of the application has no project and no session");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ACardWhoseContentCannotBeRead_Failed_AndTheOthersAreListed(bool unsolicitedCancellation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = unsolicitedCancellation ? new OperationCanceledException("unsolicited") : (Exception)new InvalidOperationException("boom");
        fixture.Plugin.Shown = new PluginLandingCard { Html = "<p>numbers</p>", Actions = new ThrowingActions(() => failure) };

        var response = await fixture.Service.LandingCardsAsync(new(Epoch, null), default);

        Assert.AreEqual("ok", response.Status, "what one plugin gave does not make the whole answer fail");
        var numbers = response.Cards.Single(static card => card.CardId == "numbers");
        Assert.AreEqual(("failed", (string?)null, 0), (numbers.State, numbers.Html, numbers.Actions.Length));
        Assert.AreEqual("ok", response.Cards.Single(static card => card.CardId == "first").State);
    }

    [TestMethod]
    public async Task ACardWhoseActionsCancelTheRequest_PropagatesRequestedCancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Plugin.Shown = new PluginLandingCard
        {
            Html = "<p>numbers</p>",
            Actions = new ThrowingActions(() => { cancellation.Cancel(); return new OperationCanceledException(cancellation.Token); }),
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fixture.Service.LandingCardsAsync(new(Epoch, null), cancellation.Token));
    }

    [TestMethod]
    public async Task ACommandOfACard_RunsForTheSpaceOfThePage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var card = (await fixture.Service.LandingCardsAsync(new(Epoch, "space-3"), default)).Cards.Single(static card => card.CardId == "numbers");
        fixture.Plugin.Ran = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = await fixture.Service.InvokeCommandAsync(new(Epoch, card.Actions[0].CommandId, null, null, false, null, "space-3"), default);

        Assert.AreEqual("started", started.Status);
        Assert.AreEqual("||space-3", await fixture.Plugin.Ran.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        // A command of a plugin that ends tells the page to read what plugins show again: the cards among it.
        Assert.AreEqual("refresh", (await fixture.NextAsync("refresh")).Kind);
    }

    [TestMethod]
    public async Task TheCardOfAPluginOfAProject_IsShownInTheSpacesOfItsProject_WithActionsAboutThatProject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var spaces = new SpaceCatalog(fixture.Projects);
        var work = (await spaces.CreateAsync("Work")).Space!;
        var other = (await spaces.CreateAsync("Other")).Space!;
        Assert.IsTrue((await spaces.AssignAsync(fixture.Project.Id, [work.Id], null)).Succeeded);
        var descriptor = fixture.Runtime.ActivePlugins.Single(static plugin => plugin.Descriptor.RuntimeKey == "builtin:fixture").Descriptor;
        var projectCalls = 0;
        fixture.Runtime.Registry.Register(descriptor, PluginScope.Project, fixture.Project.Id, fixture.Project.ProjectPath, PluginPoint.LandingCard,
        [
            new PluginLandingCardContribution
            {
                Id = "of-project", Title = "Of the project", Order = 50,
                GetCard = (context, _) =>
                {
                    Interlocked.Increment(ref projectCalls);
                    return new ValueTask<PluginLandingCard?>(PluginLandingCard.Of($"<p>{context.ProjectId}</p>",
                        PluginLandingCardAction.RunCommand("Release", "release.open"), PluginLandingCardAction.OpenCanvas("Plan", "plan"), PluginLandingCardAction.OpenCanvas("Run", "run")));
                },
            },
        ], 1);

        var inWork = (await fixture.Service.LandingCardsAsync(new(Epoch, work.Id), default)).Cards.Single(static card => card.CardId == "of-project");
        var inDefault = (await fixture.Service.LandingCardsAsync(new(Epoch, SpaceDescriptor.DefaultId), default)).Cards;
        var withoutSpaces = (await fixture.Service.LandingCardsAsync(new(Epoch, null), default)).Cards;
        var inOther = (await fixture.Service.LandingCardsAsync(new(Epoch, other.Id), default)).Cards;

        Assert.AreEqual((fixture.Project.Id, fixture.Project.DisplayName, $"<p>{fixture.Project.Id}</p>"), (inWork.ProjectId, inWork.ProjectName, inWork.Html));
        Assert.AreEqual((false, false, true), (inWork.Actions[0].Disabled, inWork.Actions[1].Disabled, inWork.Actions[2].Disabled),
            "a command and a canvas about a project have the project of the plugin; a canvas about a session has no session");
        Assert.AreEqual("Project", inWork.Actions[1].CanvasScope);
        Assert.IsTrue(inDefault.Any(static card => card.CardId == "of-project"), "the default space has every project");
        Assert.IsTrue(withoutSpaces.Any(static card => card.CardId == "of-project"));
        Assert.IsFalse(inOther.Any(static card => card.CardId == "of-project"), "a space without the project does not show the card of its plugin");
        Assert.IsTrue(inOther.Any(static card => card.CardId == "numbers"), "the cards of the application are in every space");
        Assert.AreEqual(3, projectCalls, "the excluded space never invokes the project callback");

        // An archived project is not on the page: neither is the card of its plugin, in any space.
        var archive = (await fixture.Projects.ReadArchiveAsync(fixture.Project.Id, fixture.Project.ProjectPath))!;
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated,
            await fixture.Projects.SetArchivedAsync(fixture.Project.Id, fixture.Project.ProjectPath, archive.SourcePath, archive.Revision, expectedArchived: false, archived: true));
        projectCalls = 0;
        foreach (var space in new[] { work.Id, SpaceDescriptor.DefaultId, null })
        {
            var archived = (await fixture.Service.LandingCardsAsync(new(Epoch, space), default)).Cards;
            Assert.IsFalse(archived.Any(static card => card.CardId == "of-project"), space ?? "no space");
            Assert.IsTrue(archived.Any(static card => card.CardId == "numbers"));
            Assert.AreEqual(0, projectCalls, "an archived project's callback must not run, including in the default space or without spaces");
        }
    }

    [TestMethod]
    public async Task ACardOfAProjectMissingFromTheCatalog_IsNeverAskedInAnySpace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var descriptor = fixture.Runtime.ActivePlugins.Single(static plugin => plugin.Descriptor.RuntimeKey == "builtin:fixture").Descriptor;
        var calls = 0;
        fixture.Runtime.Registry.Register(descriptor, PluginScope.Project, "missing-project", fixture.Project.ProjectPath, PluginPoint.LandingCard,
        [
            new PluginLandingCardContribution
            {
                Id = "missing-project", Title = "Missing project",
                GetCard = (_, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult<PluginLandingCard?>(PluginLandingCard.Of("<p>invisible</p>")); },
            },
        ], 1);

        foreach (var space in new[] { "work", SpaceDescriptor.DefaultId, null })
        {
            var response = await fixture.Service.LandingCardsAsync(new(Epoch, space), default);
            Assert.AreEqual("ok", response.Status);
            Assert.IsFalse(response.Cards.Any(static card => card.CardId == "missing-project"));
            Assert.IsTrue(response.Cards.Any(static card => card.CardId == "numbers"));
            Assert.AreEqual(0, calls, "an invisible project's callback must not run");
        }
    }

    [TestMethod]
    public async Task InvalidatingTheCards_SendsAnEventToThePage_ForThePluginThatAsks()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Ui.InvalidateLandingCards();
        Assert.AreEqual("landing", (await fixture.NextAsync("landing")).Kind);

        // What a plugin gets as its UI service does the same.
        fixture.Ui.ForPlugin("builtin:fixture").InvalidateLandingCards();
        Assert.AreEqual("landing", (await fixture.NextAsync("landing")).Kind);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task TheLandingCardSample_PinsItsCard_AndAReloadOrAStopOfItsPluginChangesTheCards()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-landing-cards-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
        var folder = Path.Combine(global, "plugins", "landing-card");
        CopySample(Path.Combine(AppContext.BaseDirectory, "BuiltinSkills", "codealta-plugin-runtime", "samples", "landing-card"), folder);
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
        var ui = new DesktopPluginUi();
        var canvases = new DesktopCanvases(ui);
        var runtime = new PluginRuntimeManager();
        var changes = 0;
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global, Frontend = PluginFrontends.Desktop, IsHeadless = true,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, canvases),
            });
            var package = runtime.GetPackages().Single(static candidate => candidate.Package.PackageId == "landing-card");
            if (package.Build is { Succeeded: false } build && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
                Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
            Assert.IsTrue(package.Build is not { Succeeded: false }, package.Build?.StandardOutput + package.Build?.StandardError);
            canvases.Attach(runtime, () => "work");
            // The page reads the cards again when the plugins change: the window hears it from this event.
            runtime.Changed += (_, _) => Interlocked.Increment(ref changes);
            var service = new PluginUiService(projects, runtime, ui, Epoch, canvases);

            var card = (await service.LandingCardsAsync(new(Epoch, "work"), default)).Cards.Single();
            Assert.AreEqual(("today", "Today", "list-todo", "ok", "landing-card"), (card.CardId, card.Title, card.Icon, card.State, card.PluginId));
            StringAssert.Contains(card.Html, "Nothing planned for today.");
            Assert.AreEqual("Add", card.Actions.Single().Label);
            Assert.IsNotNull(card.Actions.Single().CommandId, "the command of the action is one the plugin has");

            // A new version of the plugin: the card is the one the new version writes.
            var source = Path.Combine(folder, "plugin.cs");
            File.WriteAllText(source, File.ReadAllText(source).Replace("Nothing planned for today.", "A quiet day.", StringComparison.Ordinal).Replace("Title = \"Today\", Icon", "Title = \"This day\", Icon", StringComparison.Ordinal));
            Assert.AreEqual(PluginPackageChange.Reloaded, (await runtime.ReloadPackageAsync(package.Package)).Change);
            Assert.IsTrue(Volatile.Read(ref changes) > 0, "the window is told that plugins changed");
            var reloaded = (await service.LandingCardsAsync(new(Epoch, "work"), default)).Cards.Single();
            Assert.AreEqual("This day", reloaded.Title);
            StringAssert.Contains(reloaded.Html, "A quiet day.");

            // The plugin stops: its card is gone, and nothing of it is asked for.
            var before = Volatile.Read(ref changes);
            await runtime.StopPackageAsync(runtime.GetPackages().Single(static candidate => candidate.Package.PackageId == "landing-card").Package);
            Assert.IsTrue(Volatile.Read(ref changes) > before);
            Assert.AreEqual(0, (await service.LandingCardsAsync(new(Epoch, "work"), default)).Cards.Length);
            await service.CloseAsync();
        }
        finally
        {
            canvases.Dispose();
            await runtime.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private static void CopySample(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _watching = new();
        private readonly System.Threading.Channels.Channel<PluginUiEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<PluginUiEvent>();
        private readonly Task _pump;

        private Fixture(string root, ProjectDescriptor project, PluginRuntimeManager runtime, DesktopPluginUi ui, DesktopCanvases canvases, ProjectCatalog projects, FixturePlugin plugin, TimeSpan? timeout)
        {
            _root = root;
            Plugin = plugin;
            Project = project;
            Projects = projects;
            Runtime = runtime;
            Ui = ui;
            Canvases = canvases;
            Service = timeout is { } limit ? new PluginUiService(projects, runtime, ui, Epoch, canvases) { LandingCardTimeout = limit } : new PluginUiService(projects, runtime, ui, Epoch, canvases);
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var value in Service.WatchAsync(new(Epoch), _watching.Token)) _events.Writer.TryWrite(value);
                }
                catch (OperationCanceledException) { /* The fixture is going away. */ }
            });
        }

        public static async Task<Fixture> CreateAsync(TimeSpan? timeout = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-landing-cards-" + Guid.NewGuid().ToString("N"));
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
                BuiltIns =
                [
                    new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = () => plugin },
                    new BuiltInPluginDefinition { Id = "failing", DisplayName = "Failing", PluginType = typeof(FailingPlugin), Factory = static () => new FailingPlugin() },
                ],
            });
            canvases.Attach(runtime, () => "space-1");
            return new Fixture(root, project, runtime, ui, canvases, projects, plugin, timeout);
        }

        public ProjectDescriptor Project { get; }
        public ProjectCatalog Projects { get; }
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
        /// <summary>The context the card was last asked for.</summary>
        public PluginLandingCardContext? LastContext { get; private set; }

        /// <summary>What the card of the numbers shows, when a test sets it.</summary>
        public PluginLandingCard? Shown { get; set; }

        /// <summary>While set and not completed, the first card does not answer.</summary>
        public TaskCompletionSource? Stuck { get; set; }

        public TaskCompletionSource<string>? Ran { get; set; }

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return Command.Shell("numbers.refresh", "Reads the numbers again.", (context, _) =>
            {
                Ran?.TrySetResult($"{context.Workspace.SelectedProjectId}|{context.Sessions.SelectedSessionId}|{context.Workspace.SelectedSpaceId}");
                return ValueTask.FromResult(PluginCommandResult.Handled);
            });
            yield return Command.Session("release.open", "Opens the release.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled))
                with { Availability = PluginCommandAvailability.Always with { RequiresProject = true } };
        }

        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return new PluginCanvasContribution { Id = "board", Title = "Board", Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>board</p>")) };
            yield return new PluginCanvasContribution { Id = "plan", Title = "Plan", Scope = PluginCanvasScope.Project, Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>plan</p>")) };
            yield return new PluginCanvasContribution { Id = "run", Title = "Run", Scope = PluginCanvasScope.Session, Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>run</p>")) };
        }

        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return new PluginLandingCardContribution
            {
                Id = "numbers", Title = "Numbers", Icon = "chart-column", Order = 2,
                GetCard = (context, _) =>
                {
                    LastContext = context;
                    return new ValueTask<PluginLandingCard?>(Shown ?? PluginLandingCard.Of("<p>3 open</p>",
                        PluginLandingCardAction.RunCommand("Refresh", "NUMBERS.REFRESH", "refresh-cw") with { Primary = true },
                        PluginLandingCardAction.OpenCanvas("Open", "board"),
                        PluginLandingCardAction.OpenCanvas("Open one", "board", "item:1"),
                        PluginLandingCardAction.OpenCanvas("A fourth", "board")) with { Status = "3 open", Tone = PluginStatusTone.Warning });
                },
            };
            yield return new PluginLandingCardContribution
            {
                Id = "first", Title = "First", Order = 1,
                GetCard = async (_, _) =>
                {
                    if (Stuck is { } stuck) await stuck.Task;
                    return PluginLandingCard.Of("<p>first</p>");
                },
            };
        }
    }

    /// <summary>A list of actions that cannot be read.</summary>
    private sealed class ThrowingActions(Func<Exception> failure) : IReadOnlyList<PluginLandingCardAction>
    {
        public int Count => throw failure();

        public PluginLandingCardAction this[int index] => throw failure();

        public IEnumerator<PluginLandingCardAction> GetEnumerator() => throw failure();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A second plugin, whose cards fail or are left out: a plugin pins two cards at most.</summary>
    public sealed class FailingPlugin : PluginBase
    {
        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return new PluginLandingCardContribution { Id = "throws", Title = "Throws", Order = 5, GetCard = static (_, _) => throw new InvalidOperationException("boom") };
            yield return new PluginLandingCardContribution { Id = "hidden", Title = "Hidden", Order = 6, GetCard = static (_, _) => new ValueTask<PluginLandingCard?>((PluginLandingCard?)null) };
        }
    }
}
