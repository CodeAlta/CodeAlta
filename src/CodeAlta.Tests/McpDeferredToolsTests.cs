using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task AProviderThatDefersTools_IsOfferedTheListedToolsOfEveryServer_Deferred_WithoutStartingAny()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "tiny.log");
        WriteTinyServerConfig(project.Path, "tiny", logPath: log);
        WriteProjectPolicy(project.Path, """
            [plugins.mcp.servers.tiny]
            direct_tools = ["echo"]
            disabled_tools = ["disabled"]
            """);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        int Lists() => File.Exists(log) ? File.ReadAllLines(log).Count(static line => line == "LIST") : 0;
        PluginBeforeAgentRunContext Run(bool defers) => new()
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = project.Path, SessionId = "session", ProviderDefersTools = defers,
        };

        // A server CodeAlta never listed has nothing to offer, and is not started for it.
        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(Run(defers: true)));
        Assert.AreEqual(0, Lists());

        await using (var service = new McpRuntimeService(_home.Path))
            await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", null, CancellationToken.None);
        var listed = Lists();

        var tools = (await plugin.OnBeforeAgentRunAsync(Run(defers: true)))!.AdditionalTools;
        Assert.AreEqual(listed, Lists(), "The tools come from the list CodeAlta keeps: no server starts.");
        // direct_tools stay loaded; every other enabled tool is deferred, in the group of its server.
        var echo = tools.Single(static tool => tool.Spec.Name == "mcp__tiny__echo");
        Assert.IsFalse(echo.Spec.Deferred);
        var deferred = tools.Where(static tool => tool.Spec.Deferred).ToArray();
        Assert.IsNotEmpty(deferred);
        Assert.IsTrue(deferred.All(static tool => tool.Spec.Group == "tiny"));
        Assert.IsFalse(tools.Any(static tool => tool.Spec.Name == "mcp__tiny__disabled"), "A tool the policy turns off is not offered.");
        CollectionAssert.AreEqual(tools.Select(static tool => tool.Spec.Name).ToArray(), (await plugin.OnBeforeAgentRunAsync(Run(defers: true)))!.AdditionalTools.Select(static tool => tool.Spec.Name).ToArray(),
            "The same tools in the same order from one run to the next.");

        // A provider that does not defer tools is offered none of them.
        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(Run(defers: false)));

        // A tool of the list starts its server at its first call.
        using var arguments = JsonDocument.Parse("""{"text":"hi"}""");
        var result = await echo.Handler(new AgentToolInvocation(new ModelProviderId("test"), "session", "call", echo.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);
        Assert.AreEqual("echo:hi", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value);
        await plugin.DisposeAsync();
    }

    [TestMethod]
    public async Task DirectExposure_DecidesWhichListedToolsStayLoaded()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        await using (var service = new McpRuntimeService(_home.Path))
            await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, "tiny", null, CancellationToken.None);

        async Task<McpCachedToolsResult> ListAsync(string policy)
        {
            WriteProjectPolicy(project.Path, policy);
            await using var service = new McpRuntimeService(_home.Path);
            return service.ListCachedTools(new McpRuntimeRequest { ProjectDirectory = project.Path });
        }

        var none = await ListAsync("[plugins.mcp]\ndirect_exposure = \"none\"\n[plugins.mcp.servers.tiny]\ndirect_tools = [\"echo\"]\n");
        Assert.IsEmpty(none.Loaded);
        Assert.IsTrue(none.Deferred.Any(static tool => tool.Name == "echo"));

        var all = await ListAsync("[plugins.mcp]\ndirect_exposure = \"all\"\n");
        Assert.IsEmpty(all.Deferred);
        Assert.IsTrue(all.Loaded.Count > 1);

        // At most direct_tool_threshold tools stay loaded, the first of the list.
        var capped = await ListAsync("[plugins.mcp]\ndirect_exposure = \"all\"\ndirect_tool_threshold = 1\n");
        Assert.HasCount(1, capped.Loaded);
        Assert.AreEqual(all.Loaded.Count - 1, capped.Deferred.Count);

        // The default (auto) keeps only the tools of direct_tools.
        var auto = await ListAsync("[plugins.mcp.servers.tiny]\ndirect_tools = [\"echo\"]\n");
        CollectionAssert.AreEqual(new[] { "echo" }, auto.Loaded.Select(static tool => tool.Name).ToArray());
    }
}
