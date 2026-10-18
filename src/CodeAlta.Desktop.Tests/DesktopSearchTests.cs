using System.Text.Json;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Search;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Tests;

/// <summary>The search of the window over a running host: what it indexes, and what <c>alta search</c> finds of it.</summary>
[TestClass]
public sealed class DesktopSearchTests
{
    private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

    [TestMethod]
    public async Task ThePromptAndTheFinalAnswer_TheGuide_ThePlansAndTheTools_AreFound()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var plans = Directory.CreateDirectory(Path.Combine(project, ".alta", "plans")).FullName;
        File.WriteAllText(Path.Combine(plans, "2026-10-17-pelican.md"), "---\ntitle: Pelican migration\n---\n\n# Pelican migration\n\nMove the pelican service to the new cluster.\n");
        var guide = Directory.CreateDirectory(Path.Combine(temp.Path, "guide")).FullName;
        File.WriteAllText(Path.Combine(guide, "readme.md"), "---\ntitle: CodeAlta\n---\n\n# CodeAlta\n\nThe guide.\n");
        File.WriteAllText(Path.Combine(guide, "worktrees.md"), "---\ntitle: Worktrees\n---\n\n# Worktrees\n\nA checkout per session.\n\n## Cleanup\n\nA merged branch loses its worktree.\n");
        var provider = new DesktopUiToolsTests.ScriptedProvider();
        var relay = new DesktopSearchRelay();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopSearchPlugin.Definition(relay)],
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        await using var search = DesktopSearch.Start(host, relay, DesktopPlugins.CreateJournalCatalog(host.CatalogOptions), new ShippedDocumentation(guide),
            temp.Path, [], () => [("take_snapshot", "Takes a snapshot of the elements of the page.")], null,
            new SearchIndexerOptions { StartDelay = TimeSpan.Zero, FileQuiet = TimeSpan.FromMilliseconds(10) });
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), search: search);
        search.AttachCommands(commands);

        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Flaky tests");
        provider.Script(DesktopUiToolsTests.Say("The retry test waits for the port before it connects."));
        await DesktopUiToolsTests.SendAsync(host, session, "Why does the retry test fail on macOS?");

        // The plugin signals the prompt and the end of the turn: both are found within moments, without a pass.
        var message = await FirstHitAsync(commands, ["search", "retry", "port", "--kind", "message"]);
        Assert.AreEqual("message", message.GetProperty("kind").GetString());
        StringAssert.StartsWith(message.GetProperty("ref").GetString(), $"msg:{session.SessionId}/");
        Assert.AreEqual("assistant", message.GetProperty("role").GetString());

        var doc = await FirstHitAsync(commands, ["search", "merged", "branch", "--kind", "doc"]);
        Assert.AreEqual("doc:worktrees.md#cleanup", doc.GetProperty("ref").GetString());
        Assert.AreEqual("Worktrees › Cleanup", doc.GetProperty("title").GetString());

        var plan = await FirstHitAsync(commands, ["search", "pelican", "--kind", "plan"]);
        Assert.AreEqual("alta plan show 2026-10-17-pelican", plan.GetProperty("next").GetString());

        var tool = await FirstHitAsync(commands, ["search", "read", "file", "--kind", "tool"]);
        Assert.AreEqual("tool:builtin/read_file", tool.GetProperty("ref").GetString());
        var window = await FirstHitAsync(commands, ["search", "snapshot", "elements", "--kind", "tool"]);
        Assert.AreEqual("alta ui activate", window.GetProperty("next").GetString());
        // The alta commands are indexed from their help.
        var alta = await FirstHitAsync(commands, ["search", "reminder", "create", "--kind", "tool"]);
        StringAssert.StartsWith(alta.GetProperty("ref").GetString(), "alta:reminder");

        // The window asks the same index, in the space it shows, and is led to the message in its journal.
        var rpc = new LocalSearchService(search, async (space, token) => await host.RuntimeService.GetSpaceFilterAsync(space, null, token),
            (_, _) => Task.FromResult<IReadOnlySet<string>?>(null), new SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()), "epoch");
        Assert.AreEqual("stale_epoch", (await rpc.QueryAsync(new("other", "retry", null, null, null, 10, false, null), default)).Status);
        var found = await rpc.QueryAsync(new("epoch", "retry port", ["message"], null, null, 10, false, null), default);
        Assert.AreEqual("ok", found.Status);
        var answer = found.Hits.First(static hit => hit.Role == "assistant");
        Assert.AreEqual("retry", answer.Snippet.Substring(answer.Marks[0].Start, answer.Marks[0].Length));
        var located = await rpc.LocateAsync(new("epoch", answer.Ref), default);
        Assert.AreEqual(("ok", session.SessionId), (located.Status, located.SessionId));
        Assert.IsNotNull(located.Offset);
        Assert.AreEqual("not_found", (await rpc.LocateAsync(new("epoch", "msg:none/none"), default)).Status);

        // The Search canvas: what the space sees, how the sources are read, and the items of a kind without a query.
        var overview = await rpc.OverviewAsync(new("epoch", null), default);
        Assert.AreEqual("ok", overview.Status);
        Assert.IsGreaterThanOrEqualTo(2, overview.Items.GetValueOrDefault("message"));
        Assert.AreEqual((1, true, true), (overview.Sessions, overview.Recent >= 2, overview.Kilobytes > 0));
        Assert.IsTrue(overview.Days.Sum(static day => day.Turns) >= 1);
        Assert.IsTrue(overview.Sources.Any(static source => source.Family == "journal" && source.LastCheck is not null), "the journals are a family of sources");
        Assert.IsTrue(overview.Sources.Any(static source => source.Family == "docs"));
        Assert.AreEqual("stale_epoch", (await rpc.OverviewAsync(new("other", null), default)).Status);
        var listed = await rpc.BrowseAsync(new("epoch", "plan", null, null, 10), default);
        Assert.AreEqual(("ok", "Pelican migration"), (listed.Status, listed.Hits.Single().Title));
        StringAssert.Contains(listed.Hits[0].Snippet, "pelican service");
        Assert.AreEqual("invalid_request", (await rpc.BrowseAsync(new("epoch", "nothing", null, null, 10), default)).Status);
        StringAssert.Contains(DesktopSearchPlugin.Describe(relay.Search), "alta search status");
        Assert.AreSame(search, relay.Search);

        // The commands of the window are found once the page gave them.
        Assert.AreEqual("ok", rpc.RegisterCommands(new("epoch", [new("palette", "Search everything", "Navigation", "Opens the search window.", "find", "Ctrl+P")])).Status);
        await UntilAsync(async () => (await rpc.QueryAsync(new("epoch", "search everything", ["command"], null, null, 5, false, null), default)).Hits.Any(static hit => hit.Ref == "command:palette"));
        // The word being typed matches the start of a word.
        await UntilAsync(async () => (await rpc.QueryAsync(new("epoch", "every", ["command"], null, null, 5, true, null), default)).Hits.Count == 1);

        // A project kept out of search loses its messages and its plans, and gets them back when it is let in again.
        Assert.AreEqual("ok", rpc.SetExcluded(new("epoch", "project", host.CurrentProject.Id, true)).Status);
        await UntilAsync(async () => (await rpc.QueryAsync(new("epoch", "retry", null, null, null, 10, false, null), default)).Hits.All(static hit => hit.Kind is not ("message" or "plan")));
        // Results leave out an excluded project at once; the pass that follows removes its items from the index.
        await UntilAsync(async () => (await search.Store.CountAsync()).Items.GetValueOrDefault(SearchKind.Plan) == 0);
        Assert.AreEqual("ok", rpc.SetExcluded(new("epoch", "project", host.CurrentProject.Id, false)).Status);
        await UntilAsync(async () => (await rpc.QueryAsync(new("epoch", "retry port", ["message"], null, null, 10, false, null), default)).Hits.Count > 0);

        // A deleted session takes its messages with it.
        Assert.AreEqual("ok", await host.Commands.DeleteCatalogSessionAsync(session.SessionId, host.CurrentProject.Id, project, session.Title));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await search.Store.CountAsync(timeout.Token)).Items.GetValueOrDefault(SearchKind.Message) > 0) await Task.Delay(20, timeout.Token);
    }

    [TestMethod]
    public async Task SearchByMeaning_IsOfferedOnTheLandingCard_OnceThereIsEnoughToSearch_PutOff_AndItsDialogIsAskedOfTheWindow()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var provider = new DesktopUiToolsTests.ScriptedProvider();
        var relay = new DesktopSearchRelay();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopSearchPlugin.Definition(relay)],
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        await using var search = DesktopSearch.Start(host, relay, DesktopPlugins.CreateJournalCatalog(host.CatalogOptions), null, temp.Path, [], null, null,
            new SearchIndexerOptions { StartDelay = TimeSpan.Zero, FileQuiet = TimeSpan.FromMilliseconds(10) });
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Without search by meaning in the window (no runtime for it), nothing is offered.
        Assert.IsFalse(await search.IsMeaningOfferedAsync(today));
        Assert.IsFalse(search.RequestMeaningDialog(), "no window listens");

        var engine = CodeAlta.Desktop.Speech.SpeechModelCatalog.FindEngine(CodeAlta.Desktop.Speech.SpeechModelCatalog.CurrentRuntimeIdentifier())!;
        var installer = new CodeAlta.Desktop.Search.Embedding.EmbeddingInstaller(Path.Combine(temp.Path, "models"), Path.Combine(temp.Path, "runtime"), Path.Combine(temp.Path, "speech"), engine, handler: null);
        search.AttachMeaning(new DesktopSearchMeaning(search.Host, search.Indexer, installer, new StoreMeaningVectors(search.Store, null),
            load => throw new InvalidOperationException("search by meaning is off"), null));

        // One session with one turn: not enough to search by meaning at first (ten sessions, or three days since the first turn).
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Flaky tests");
        provider.Script(DesktopUiToolsTests.Say("The retry test waits for the port before it connects."));
        await DesktopUiToolsTests.SendAsync(host, session, "Why does the retry test fail on macOS?");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while ((await search.Store.CountAsync(timeout.Token)).Days.Count == 0) await Task.Delay(20, timeout.Token);
        }

        Assert.IsNull(await DesktopSearchPlugin.LandingCardAsync(search, today, default), "never at the first start");
        Assert.AreEqual("offer", DesktopSearchPlugin.CardKey(search, today), "the card is asked again as the days pass");
        var later = today.AddDays(3);
        var card = await DesktopSearchPlugin.LandingCardAsync(search, later, default);
        Assert.IsNotNull(card);
        StringAssert.Contains(card.Html, "Search your work by meaning");
        Assert.AreEqual(("Learn more", "search_meaning", true), (card.Actions[0].Label, card.Actions[0].Command, card.Actions[0].Primary));
        Assert.AreEqual(("Not now", "search_meaning_later"), (card.Actions[1].Label, card.Actions[1].Command));

        // "Learn more" asks the window, which hears it with the next event of `localSearch.watch`.
        var rpc = new LocalSearchService(search, async (space, token) => await host.RuntimeService.GetSpaceFilterAsync(space, null, token),
            (_, _) => Task.FromResult<IReadOnlySet<string>?>(null), new SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()), "epoch");
        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var events = rpc.WatchAsync(new("epoch"), watching.Token).GetAsyncEnumerator(watching.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsNull(events.Current.Open);
        Assert.IsTrue(search.RequestMeaningDialog());
        do Assert.IsTrue(await events.MoveNextAsync()); while (events.Current.Open is null);
        Assert.AreEqual("meaning", events.Current.Open);
        Assert.IsFalse((await rpc.StatusAsync(new("epoch"), default)).Semantic!.Offer, "the window offers it no sooner than the card");

        // "Not now" puts it off for 30 days: no card, and no offer in the status, until then.
        search.AnswerMeaningOffer("later", later);
        Assert.IsNull(await DesktopSearchPlugin.LandingCardAsync(search, later.AddDays(29), default));
        Assert.AreEqual(string.Empty, DesktopSearchPlugin.CardKey(search, later.AddDays(29)));
        Assert.IsNotNull(await DesktopSearchPlugin.LandingCardAsync(search, later.AddDays(30), default));
        Assert.ThrowsExactly<ArgumentException>(() => search.AnswerMeaningOffer("maybe", later));
    }

    [TestMethod]
    public void SearchByMeaning_IsWorthOffering_WithTenSessions_OrThreeDaysAfterTheFirstTurn()
    {
        var today = new DateOnly(2026, 10, 17);
        SearchIndexCounts Counts(long sessions, params DateOnly[] days) => new()
        {
            Items = new Dictionary<SearchKind, long>(), Projects = new Dictionary<string, long>(), Sessions = sessions, Days = [.. days.Select(static day => (day, 1L))],
        };
        Assert.IsFalse(DesktopSearch.IsMeaningWorthOffering(Counts(0), today));
        Assert.IsFalse(DesktopSearch.IsMeaningWorthOffering(Counts(9, today.AddDays(-2), today), today));
        Assert.IsTrue(DesktopSearch.IsMeaningWorthOffering(Counts(10), today));
        Assert.IsTrue(DesktopSearch.IsMeaningWorthOffering(Counts(1, today, today.AddDays(-3)), today));
    }

    [TestMethod]
    public async Task TheBodyOfASkill_IsReadAgain_OnlyWhenItsFileChanges()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var skill = Directory.CreateDirectory(Path.Combine(global, "skills", "crossing")).FullName;
        var file = Path.Combine(skill, "SKILL.md");
        File.WriteAllText(file, "---\nname: crossing\ndescription: Helps with roads.\n---\n# Crossing\n\nLook for the zebra stripes.\n");
        var relay = new DesktopSearchRelay();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopSearchPlugin.Definition(relay)],
        }, CancellationToken.None);
        await using var search = DesktopSearch.Start(host, relay, DesktopPlugins.CreateJournalCatalog(host.CatalogOptions), null, temp.Path, [], null, null,
            new SearchIndexerOptions { StartDelay = TimeSpan.Zero, FileQuiet = TimeSpan.FromMilliseconds(10) });
        async Task<int> FoundAsync(string word)
            => (await search.Service.SearchAsync(new SearchRequest(word, new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Skill } }) { Mode = SearchMode.Keyword })).Hits.Count;

        await UntilAsync(async () => await FoundAsync("zebra") == 1);
        // A pass that finds the file as it was keeps its body; an edit is read again.
        search.Indexer.SignalPass();
        File.WriteAllText(file, "---\nname: crossing\ndescription: Helps with roads.\n---\n# Crossing\n\nWatch the giraffe cross.\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        search.Indexer.SignalPass();
        await UntilAsync(async () => await FoundAsync("giraffe") == 1);
        Assert.AreEqual(0, await FoundAsync("zebra"));
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }

    private static async Task<JsonElement> FirstHitAsync(AltaCommandDispatcher commands, string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var result = await commands.InvokeAsync(args, caller: Mcp, cancellationToken: timeout.Token);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var record = JsonDocument.Parse(line).RootElement;
                if (record.GetProperty("type").GetString() == "alta.search.hit") return record;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-search-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
