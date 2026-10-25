using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task SessionServers_AreServersOfTheProcess_ActivatedForTheirSession_WithAKeyOfTheirOwn()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var tiny = new McpSessionServer { Name = "tiny", Command = "dotnet", Args = [TinyServerAssemblyPath] };

        var first = plugin.AddSessionServers("one", project.Path, [tiny, new McpSessionServer { Name = "web docs", Url = "http://127.0.0.1:1/mcp" }]);
        var second = plugin.AddSessionServers("two", project.Path, [tiny with { Name = "web docs" }]);
        var again = plugin.AddSessionServers("one", project.Path, [new McpSessionServer { Name = "web docs", Url = "http://127.0.0.1:2/mcp" }]);

        CollectionAssert.AreEqual(new[] { "tiny-one", "web-docs" }, first.ToArray(), "A configured server keeps its name; a name is made a key.");
        CollectionAssert.AreEqual(new[] { "web-docs-two" }, second.ToArray(), "The name of a server of another session is taken.");
        CollectionAssert.AreEqual(new[] { "web-docs" }, again.ToArray(), "A session replaces its own server.");
        var servers = plugin.ConfigCache.Discover(project.Path, _home.Path).EffectiveServers.ToDictionary(static server => server.Definition.Key, static server => server.Definition);
        Assert.AreEqual("http://127.0.0.1:2/mcp", servers["web-docs"].Url);
        Assert.AreEqual(Path.GetFullPath(project.Path), servers["tiny-one"].Cwd, "A stdio server starts in the folder of its session.");
        Assert.IsTrue(servers.ContainsKey("tiny"), "The configured server is still there.");
        Assert.IsFalse(File.Exists(McpConfigDiscovery.GetGlobalConfigPath(_home.Path)), "Nothing is written.");

        // The session has its servers active: their tools are offered to its runs.
        var before = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = project.Path, SessionId = "one",
        });
        CollectionAssert.Contains(before!.AdditionalTools.Select(static tool => tool.Spec.Name).ToArray(), "mcp__tiny_one__echo");

        Assert.ThrowsExactly<ArgumentException>(() => plugin.AddSessionServers("one", project.Path, [new McpSessionServer { Name = "both", Command = "x", Url = "http://a/" }]));
        Assert.ThrowsExactly<ArgumentException>(() => plugin.AddSessionServers("one", project.Path, [new McpSessionServer { Name = "none" }]));
        Assert.ThrowsExactly<ArgumentException>(() => plugin.AddSessionServers("one", project.Path, [new McpSessionServer { Name = "relative", Url = "mcp" }]));
    }
}
