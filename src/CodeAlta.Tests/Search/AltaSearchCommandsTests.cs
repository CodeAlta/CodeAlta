using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Search;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests.Search;

[TestClass]
public sealed class AltaSearchCommandsTests
{
    private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

    private static IEnumerable<JsonElement> Records(AltaCommandResult result)
        => result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(static line => JsonDocument.Parse(line).RootElement);

    private static string Type(JsonElement record) => record.GetProperty("type").GetString()!;

    private static async Task<(SearchFixture Fixture, AltaCommandDispatcher Dispatcher)> CreateAsync(bool withSearch = true)
    {
        var tools = new ListSearchSource("tools", [SearchKind.Tool, SearchKind.Mcp], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>(
        [
            new("mcp:slack/post_message", SearchKind.Mcp, "post_message (slack)", "Posts a message to a Slack channel.", DateTimeOffset.UtcNow) { Extra = "{\"server\":\"slack\",\"tool\":\"post_message\"}" },
            new("tool:ui/take_snapshot", SearchKind.Tool, "take_snapshot", "Lists the elements of the window.", DateTimeOffset.UtcNow) { Extra = "{\"activate\":\"alta ui activate\"}" },
            new("tool:builtin/read_file", SearchKind.Tool, "read_file", "Reads a file.", DateTimeOffset.UtcNow) { Extra = "{\"state\":\"loaded\"}" },
            new("doc:worktrees.md#cleanup", SearchKind.Doc, "Worktrees › Cleanup", "A worktree is removed once its branch is merged.", DateTimeOffset.UtcNow) { Extra = "{\"page\":\"worktrees.md\",\"anchor\":\"cleanup\"}" },
        ]));
        var settings = new ListSearchSource("settings", [SearchKind.Setting], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>(
        [
            new("setting:sessions.title", SearchKind.Setting, "Agent & models › Sessions › Title of a session you did not name", "How a session nobody named is titled.\nAlso: rename",
                DateTimeOffset.UnixEpoch) { Extra = "{\"id\":\"sessions.title\",\"page\":\"sessions\"}" },
        ]));
        var fixture = new SearchFixture(tools, settings);
        fixture.Host.Sessions["s1"] = new("s1", "Release work", "p1", "default", false);
        fixture.Journals.Write(new JournalWriter("s1", DateTimeOffset.UtcNow.AddHours(-2)).User("r1", "user:1", "Prepare the release notes").Assistant("r1", "a:1", "The release notes are written.").Idle("r1"));
        await fixture.PassAsync();
        var options = new CatalogOptions { GlobalRoot = Path.Combine(fixture.Root, "home") };
        var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options));
        if (withSearch) services.Add(fixture.Service).Add(fixture.Indexer).Add(fixture.Store);
        var registry = new AltaCommandRegistry();
        services.Add(registry);
        return (fixture, new AltaCommandDispatcher(registry, services));
    }

    [TestMethod]
    public async Task Search_GivesHitsWithTheirNextCommand_AndASummary()
    {
        var (fixture, dispatcher) = await CreateAsync();
        await using var _ = fixture;
        var result = await dispatcher.InvokeAsync(["search", "release", "notes"], caller: Mcp);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var records = Records(result).ToList();
        var hits = records.Where(static record => Type(record) == "alta.search.hit").ToList();
        Assert.IsNotEmpty(hits);
        var first = hits.First(static hit => hit.GetProperty("kind").GetString() == "message");
        StringAssert.Contains(first.GetProperty("snippet").GetString(), "**release**");
        StringAssert.StartsWith(first.GetProperty("next").GetString(), "alta search show msg:s1/");
        var summary = records.Single(static record => Type(record) == "alta.search.summary");
        Assert.AreEqual("off", summary.GetProperty("meaning").GetString());

        var mcp = Records(await dispatcher.InvokeAsync(["search", "slack", "--kind", "mcp"], caller: Mcp)).First(static record => Type(record) == "alta.search.hit");
        StringAssert.StartsWith(mcp.GetProperty("next").GetString(), "alta mcp tool call --server slack --tool post_message");
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["search", "x", "--kind", "nothing"], caller: Mcp)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["search", "x", "--since", "soon"], caller: Mcp)).ExitCode);
    }

    [TestMethod]
    public async Task ASetting_IsFound_AndItsNextCommandReadsIt()
    {
        var (fixture, dispatcher) = await CreateAsync();
        await using var _ = fixture;
        var result = await dispatcher.InvokeAsync(["search", "rename", "--kind", "setting"], caller: Mcp);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var hit = Records(result).Single(static record => Type(record) == "alta.search.hit");
        Assert.AreEqual(("setting", "setting:sessions.title"), (hit.GetProperty("kind").GetString(), hit.GetProperty("ref").GetString()));
        StringAssert.StartsWith(hit.GetProperty("next").GetString(), "alta settings get sessions.title");
        Assert.AreEqual("setting", SearchNames.Of(SearchKind.Setting));
    }

    [TestMethod]
    public async Task Subcommands_AreNotTakenForAQuery()
    {
        var (fixture, dispatcher) = await CreateAsync();
        await using var _ = fixture;
        var status = Records(await dispatcher.InvokeAsync(["search", "status"], caller: Mcp)).Single();
        Assert.AreEqual("alta.search.status", Type(status));
        Assert.IsTrue(status.GetProperty("enabled").GetBoolean());

        var hit = Records(await dispatcher.InvokeAsync(["search", "release", "--kind", "message"], caller: Mcp)).First(static record => Type(record) == "alta.search.hit");
        var shown = Records(await dispatcher.InvokeAsync(["search", "show", hit.GetProperty("ref").GetString()!], caller: Mcp)).Single();
        Assert.AreEqual("alta.search.item", Type(shown));
        Assert.AreEqual(2, shown.GetProperty("turns")[0].GetProperty("messages").GetArrayLength());
        Assert.AreEqual(AltaExitCodes.NotFound, (await dispatcher.InvokeAsync(["search", "show", "msg:none/none"], caller: Mcp)).ExitCode);

        var reindex = Records(await dispatcher.InvokeAsync(["search", "reindex", "--kind", "doc"], caller: Mcp)).Single();
        Assert.IsTrue(reindex.GetProperty("started").GetBoolean());
        StringAssert.Contains((await dispatcher.InvokeAsync(["search", "--help"], caller: Mcp)).Stdout, "alta search show <ref>");
        var policies = (await dispatcher.InvokeAsync(["tool", "list"], caller: Mcp)).Stdout;
        StringAssert.Contains(policies, "search");
    }

    [TestMethod]
    public async Task DocumentationSearch_ReadsTheIndex_WithTheRecordsOfTheGuide()
    {
        var (fixture, _) = await CreateAsync();
        await using var __ = fixture;
        var guide = Directory.CreateDirectory(Path.Combine(fixture.Root, "guide")).FullName;
        File.WriteAllText(Path.Combine(guide, "worktrees.md"), "---\ntitle: Worktrees\n---\n\n# Worktrees\n\nA checkout per session.\n");
        var options = new CatalogOptions { GlobalRoot = Path.Combine(fixture.Root, "home") };
        var registry = new AltaCommandRegistry();
        var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(new CodeAlta.Catalog.Documentation.ShippedDocumentation(guide))
            .Add(fixture.Service).Add(fixture.Indexer).Add(fixture.Store).Add(registry);
        var dispatcher = new AltaCommandDispatcher(registry, services);

        // "merged" is only in the section the index has: the place comes from the index, as a place of the guide.
        var hit = Records(await dispatcher.InvokeAsync(["documentation", "search", "merged"], caller: Mcp)).First(static record => Type(record) == "alta.documentation.hit");
        Assert.AreEqual("worktrees.md", hit.GetProperty("page").GetString());
        Assert.AreEqual("Worktrees", hit.GetProperty("title").GetString());
        Assert.AreEqual("Cleanup", hit.GetProperty("heading").GetString());
        // Without a hit in the index, the pages are read as before.
        var plain = Records(await dispatcher.InvokeAsync(["documentation", "search", "checkout per"], caller: Mcp)).First(static record => Type(record) == "alta.documentation.hit");
        Assert.AreEqual("worktrees.md", plain.GetProperty("page").GetString());
    }

    [TestMethod]
    public async Task Load_GivesTheMcpToolsOfHitsToTheMcpActivation_AndSaysWhatOtherHitsNeed()
    {
        var (fixture, _) = await CreateAsync();
        await using var __ = fixture;
        var mcp = new FakeMcpContributor();
        var registry = new AltaCommandRegistry([new BuiltInAltaCommandContributor(), mcp]);
        var options = new CatalogOptions { GlobalRoot = Path.Combine(fixture.Root, "home") };
        var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(fixture.Service).Add(fixture.Indexer).Add(fixture.Store).Add(registry);
        var dispatcher = new AltaCommandDispatcher(registry, services);
        var agent = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s2", RunTools = new AgentRunTools() };

        // An agent's hit says how to load it.
        var hit = Records(await dispatcher.InvokeAsync(["search", "slack", "--kind", "mcp"], caller: agent)).First(static record => Type(record) == "alta.search.hit");
        StringAssert.StartsWith(hit.GetProperty("next").GetString(), "alta search load mcp:slack/post_message");

        var result = await dispatcher.InvokeAsync(["search", "load", "mcp:slack/post_message", "tool:ui/take_snapshot", "tool:builtin/read_file", "mcp:none/none"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        CollectionAssert.AreEqual(new[] { "slack/post_message" }, mcp.Activated);
        Assert.IsTrue(mcp.HadRunTools, "The activation runs for the calling agent, with the tools of its run.");
        var load = Records(result).Single(static record => Type(record) == "alta.search.load");
        Assert.AreEqual("now", load.GetProperty("toolsAvailable").GetString());
        Assert.AreEqual(1, load.GetProperty("loadedCount").GetInt32());
        var items = load.GetProperty("items").EnumerateArray().ToDictionary(static item => item.GetProperty("ref").GetString()!, static item => item);
        Assert.AreEqual("loaded", items["mcp:slack/post_message"].GetProperty("status").GetString());
        Assert.AreEqual("mcp__slack__post_message", items["mcp:slack/post_message"].GetProperty("alias").GetString());
        Assert.AreEqual("activate", items["tool:ui/take_snapshot"].GetProperty("status").GetString());
        Assert.AreEqual("alta ui activate", items["tool:ui/take_snapshot"].GetProperty("next").GetString());
        Assert.AreEqual("already_loaded", items["tool:builtin/read_file"].GetProperty("status").GetString());
        Assert.AreEqual("not_found", items["mcp:none/none"].GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Load_IsRefused_ToACallerThatIsNoAgentRun_AndWhileTheUserReviewsCommands()
    {
        var (fixture, _) = await CreateAsync();
        await using var __ = fixture;
        var mcp = new FakeMcpContributor();
        var registry = new AltaCommandRegistry([new BuiltInAltaCommandContributor(), mcp]);
        var options = new CatalogOptions { GlobalRoot = Path.Combine(fixture.Root, "home") };
        var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(fixture.Service).Add(fixture.Indexer).Add(fixture.Store).Add(registry)
            .Add(new AltaCommandReviewPolicy(AcceptsCommands: true) { AcceptsCommandsOf = session => session != "reviewed" });
        var dispatcher = new AltaCommandDispatcher(registry, services);

        Assert.AreEqual(AltaExitCodes.Unsupported, (await dispatcher.InvokeAsync(["search", "load", "mcp:slack/post_message"], caller: Mcp)).ExitCode);
        var reviewed = await dispatcher.InvokeAsync(["search", "load", "mcp:slack/post_message"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "reviewed", RunTools = new AgentRunTools() });
        Assert.AreEqual(AltaExitCodes.PolicyDenied, reviewed.ExitCode);
        StringAssert.Contains(reviewed.Stdout + reviewed.Stderr, "search.loadDenied");
        Assert.IsEmpty(mcp.Activated);
        StringAssert.Contains((await dispatcher.InvokeAsync(["tool", "list"], caller: Mcp)).Stdout, "search load");
    }

    // The `alta mcp activate` of the MCP plugin, as `alta search load` calls it.
    private sealed class FakeMcpContributor : IAltaCommandContributor
    {
        public List<string> Activated { get; } = [];

        public bool HadRunTools { get; private set; }

        public IEnumerable<XenoAtom.CommandLine.CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
        {
            var ids = new List<string>();
            var activate = new XenoAtom.CommandLine.Command("activate", "Activate.");
            activate.Add("<id>*", "Ids.", value => ids.Add(value!));
            activate.Add((_, _) =>
            {
                Activated.AddRange(ids);
                HadRunTools = context.Invocation.Caller.RunTools is not null;
                context.Invocation.Stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "alta.mcp.activate", toolsAvailable = "now", tools = ids.Select(static id => "mcp__" + id.Replace("/", "__", StringComparison.Ordinal)).ToArray(),
                }));
                return ValueTask.FromResult(0);
            });
            yield return new XenoAtom.CommandLine.Command("mcp", "MCP.") { activate };
        }

        public IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context)
        {
            yield return new AltaCommandPolicy { Path = "mcp", RequiresInProcessRuntime = true, IsMutating = true };
        }
    }

    [TestMethod]
    public async Task AHostWithoutTheIndex_HasNoSearchGroup()
    {
        var (fixture, dispatcher) = await CreateAsync(withSearch: false);
        await using var _ = fixture;
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["search", "anything"], caller: Mcp)).ExitCode);
        Assert.DoesNotContain("search", (await dispatcher.InvokeAsync(["tool", "list"], caller: Mcp)).Stdout);
    }
}
