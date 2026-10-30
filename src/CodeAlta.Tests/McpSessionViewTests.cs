using System.Text.Json;
using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Tests;

// What the MCP window of a session shows: the servers it uses, the tools it has and the ones it could activate.
[TestClass]
public sealed class McpSessionViewTests
{
    [TestMethod]
    public async Task DescribeSession_ShowsTheToolsListedBefore_AndWhatTheSessionActivated()
    {
        using var home = TempDirectory.Create();
        using var project = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(project.Path, ".alta"));
        File.WriteAllText(Path.Combine(project.Path, ".alta", "mcp.json"), """
            { "mcpServers": { "docs": { "command": "docs-server" }, "git": { "command": "git-server" }, "web": { "url": "https://example.test/mcp" }, "off": { "command": "off-server" } } }
            """);
        File.WriteAllText(Path.Combine(project.Path, ".alta", "config.toml"), """
            [plugins.mcp.servers.git]
            disabled_tools = ["blame"]

            [plugins.mcp.servers.off]
            enabled = false
            """);
        // The lists CodeAlta keeps of the tools of docs and git; web was never listed.
        var definitions = new McpConfigCache().Discover(project.Path, new McpHome(home.Path, null)).EffectiveServers.ToDictionary(static server => server.Definition.Key, static server => server.Definition);
        var cache = new McpToolListCache(McpToolListCache.FolderOf(home.Path));
        cache.Write(definitions["docs"], [Tool("docs", "search", "Searches the documentation"), Tool("docs", "fetch", null), Tool("docs", "view", null, ["app"])]);
        cache.Write(definitions["git"], [Tool("git", "log", "Shows the history"), Tool("git", "blame", null)]);

        await using var plugin = new McpPlugin(createPresentation: null, home.Path);
        var scope = McpActivationState.ResolveScopeKey("session-a", project.Path);
        plugin.ActivationState.ActivateServers(scope, ["docs"]);
        plugin.ActivationState.ActivateTools(scope, [("git", "log")]);
        plugin.ActivationState.UpdateToolCounts(scope, new Dictionary<string, int>(StringComparer.Ordinal) { ["docs"] = 2, ["git"] = 1 });
        var snapshot = new McpManagementService(home.Path).RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });

        var servers = plugin.DescribeSession(snapshot, "session-a").ToDictionary(static server => server.Server.Key);

        CollectionAssert.AreEqual(new[] { "docs", "git", "off", "web" }, plugin.DescribeSession(snapshot, "session-a").Select(static server => server.Server.Key).ToArray());
        // The whole server: every tool of the model, not the one only its view calls.
        Assert.IsTrue(servers["docs"].Activated);
        Assert.AreEqual(2, servers["docs"].ActiveToolCount);
        CollectionAssert.AreEqual(new[] { "fetch:True", "search:True" }, servers["docs"].Tools.Select(static tool => $"{tool.Name}:{tool.Active}").ToArray());
        Assert.AreEqual("Searches the documentation", servers["docs"].Tools.Single(static tool => tool.Name == "search").Description);
        Assert.AreEqual("query", servers["docs"].Tools[0].Arguments.Single().Name);
        Assert.IsNotNull(servers["docs"].ListedAt);
        // One tool of a server: the server is used, not activated; a tool the policy turns off is neither enabled nor active.
        Assert.IsFalse(servers["git"].Activated);
        Assert.IsTrue(servers["git"].InUse);
        CollectionAssert.AreEqual(new[] { "blame:False:False", "log:True:True" }, servers["git"].Tools.Select(static tool => $"{tool.Name}:{tool.Enabled}:{tool.Active}").ToArray());
        Assert.IsFalse(servers["web"].InUse);
        Assert.IsNull(servers["web"].ListedAt);
        Assert.AreEqual(0, servers["web"].Tools.Count);
        Assert.AreEqual(McpManagementServerState.Disabled, servers["off"].Server.State);

        // Another session activated nothing; without the plugin, nothing is activated either.
        Assert.IsFalse(plugin.DescribeSession(snapshot, "session-b").Any(static server => server.InUse || server.Tools.Any(static tool => tool.Active)));
        var described = McpPlugin.DescribeServers(snapshot, home.Path);
        Assert.IsFalse(described.Any(static server => server.InUse));
        Assert.AreEqual(2, described.Single(static server => server.Server.Key == "docs").Tools.Count);
    }

    private static McpRuntimeTool Tool(string server, string name, string? description, IReadOnlyList<string>? visibility = null)
        => new()
        {
            Server = server, Name = name, Alias = $"mcp__{server}__{name}", Description = description, Visibility = visibility,
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string","description":"What to look for"}}}""").RootElement.Clone(),
        };

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
            => new(Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.McpSessionViewTests." + Guid.NewGuid().ToString("N"))).FullName);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
