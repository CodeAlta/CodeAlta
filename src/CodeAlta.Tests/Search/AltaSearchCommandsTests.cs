using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Search;

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
            new("doc:worktrees.md#cleanup", SearchKind.Doc, "Worktrees › Cleanup", "A worktree is removed once its branch is merged.", DateTimeOffset.UtcNow) { Extra = "{\"page\":\"worktrees.md\",\"anchor\":\"cleanup\"}" },
        ]));
        var fixture = new SearchFixture(tools);
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
        var first = hits[0];
        Assert.AreEqual("message", first.GetProperty("kind").GetString());
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
    public async Task AHostWithoutTheIndex_HasNoSearchGroup()
    {
        var (fixture, dispatcher) = await CreateAsync(withSearch: false);
        await using var _ = fixture;
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["search", "anything"], caller: Mcp)).ExitCode);
        Assert.DoesNotContain("search", (await dispatcher.InvokeAsync(["tool", "list"], caller: Mcp)).Stdout);
    }
}
