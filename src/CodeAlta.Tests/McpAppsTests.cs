using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

// MCP Apps (io.modelcontextprotocol/ui): the capability, the audience of the tools, the view of a call and the requests of a view.
public sealed partial class McpRuntimeServiceTests
{
    private static readonly Dictionary<string, string> UiTools = new() { ["MCP_TEST_UI_TOOLS"] = "1" };

    [TestMethod]
    public async Task Apps_AreAdvertisedOnlyByAHostThatShowsThem()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: UiTools);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        await using (var hiding = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false))
        {
            await hiding.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        await using (var showing = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true))
        {
            await showing.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        CollectionAssert.AreEqual(new[] { "CLIENT_UI no", "CLIENT_UI yes" }, File.ReadAllLines(log).Where(static line => line.StartsWith("CLIENT_UI", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public async Task Apps_ToolsOfTheViewOnlyAreNeitherOfferedNorCallableByTheModel()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        await using var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var search = await service.SearchToolsAsync(request, "tiny", query: null, CancellationToken.None, useCache: false);
        var listed = await service.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        var direct = await service.ListDirectToolsAsync(request, CancellationToken.None);
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var call = await service.CallToolAsync(request, "tiny", "view_refresh", new Dictionary<string, object?>(), diagnostics, CancellationToken.None);

        foreach (var tools in new[] { search.Tools, listed.Tools, direct.Tools })
        {
            var names = tools.Select(static tool => tool.Name).ToArray();
            CollectionAssert.Contains(names, "show_view");
            CollectionAssert.Contains(names, "model_only");
            CollectionAssert.DoesNotContain(names, "view_refresh");
        }

        var shown = listed.Tools.Single(static tool => tool.Name == "show_view");
        Assert.AreEqual("ui://tiny/view", shown.UiResourceUri);
        Assert.IsNull(shown.Visibility);
        CollectionAssert.AreEqual(new[] { "model" }, listed.Tools.Single(static tool => tool.Name == "model_only").Visibility!.ToArray());
        Assert.IsNull(call);
        Assert.AreEqual("tool_app_only", diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Apps_TheCachedListKeepsTheViewAndLeavesOutToolsOfTheViewOnly()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        await using (var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true))
        {
            await service.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        var list = Directory.EnumerateFiles(McpToolListCache.FolderOf(_home.Path), "*.json").Single();
        var cached = JsonSerializer.Deserialize<McpCachedToolList>(File.ReadAllText(list), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await using var reader = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true);
        var tools = reader.ListCachedTools(request);

        Assert.AreEqual("ui://tiny/view", cached.Tools.Single(static tool => tool.Name == "show_view").UiResourceUri);
        CollectionAssert.AreEqual(new[] { "app" }, cached.Tools.Single(static tool => tool.Name == "view_refresh").Visibility!.ToArray());
        var names = tools.Loaded.Concat(tools.Deferred).Select(static tool => tool.Name).ToArray();
        CollectionAssert.Contains(names, "show_view");
        CollectionAssert.DoesNotContain(names, "view_refresh");
        Assert.AreEqual("ui://tiny/view", tools.Loaded.Concat(tools.Deferred).Single(static tool => tool.Name == "show_view").UiResourceUri);
    }

    [TestMethod]
    public async Task Apps_TheResultOfACallKeepsForItsViewWhatTheModelGetsRedacted()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        await using var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        var diagnostics = new List<McpRuntimeDiagnostic>();

        var shown = await service.CallToolAsync(request, "tiny", "show_view", new Dictionary<string, object?>(), diagnostics, CancellationToken.None);
        var echo = await service.CallToolAsync(request, "tiny", "echo", new Dictionary<string, object?>(), diagnostics, CancellationToken.None);

        Assert.AreEqual(0, diagnostics.Count, string.Join(" | ", diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("[redacted]", shown!.StructuredContent!.Value.GetProperty("apiKey").GetString());
        Assert.AreEqual("ui://tiny/view", shown.App!.ResourceUri);
        Assert.AreEqual("kept-for-the-view", shown.App.StructuredContent!.Value.GetProperty("apiKey").GetString());
        Assert.IsNull(echo!.App);
    }

    [TestMethod]
    public async Task Apps_AViewCallsOnlyToolsOfTheViewAndReadsOnlyViews()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        await using var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true);
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        var refreshDiagnostics = new List<McpRuntimeDiagnostic>();
        var modelOnlyDiagnostics = new List<McpRuntimeDiagnostic>();
        var plainDiagnostics = new List<McpRuntimeDiagnostic>();
        var fileDiagnostics = new List<McpRuntimeDiagnostic>();
        var viewDiagnostics = new List<McpRuntimeDiagnostic>();

        var refresh = await service.CallToolForViewAsync(request, "tiny", "view_refresh", new Dictionary<string, object?>(), refreshDiagnostics, CancellationToken.None);
        var modelOnly = await service.CallToolForViewAsync(request, "tiny", "model_only", new Dictionary<string, object?>(), modelOnlyDiagnostics, CancellationToken.None);
        var view = await service.ReadAppResourceAsync(request, "tiny", "ui://tiny/view", viewDiagnostics, CancellationToken.None);
        var plain = await service.ReadAppResourceAsync(request, "tiny", "ui://tiny/plain", plainDiagnostics, CancellationToken.None);
        var file = await service.ReadAppResourceAsync(request, "tiny", "file:///etc/hosts", fileDiagnostics, CancellationToken.None);

        Assert.AreEqual(0, refreshDiagnostics.Count, string.Join(" | ", refreshDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(1, refresh!.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.IsNull(modelOnly);
        Assert.AreEqual("tool_not_for_app", modelOnlyDiagnostics.Single().Code);
        Assert.AreEqual(0, viewDiagnostics.Count, string.Join(" | ", viewDiagnostics.Select(static diagnostic => diagnostic.Message)));
        StringAssert.Contains(view!.Html, "<title>Tiny view</title>");
        // The content declares nothing: what the listing of the resource declares applies.
        CollectionAssert.AreEqual(new[] { "https://api.example.com" }, view.Meta.ConnectDomains.ToArray());
        Assert.AreEqual(true, view.Meta.PrefersBorder);
        Assert.IsNull(plain);
        Assert.AreEqual("resource_not_a_view", plainDiagnostics.Single().Code);
        Assert.IsNull(file);
        Assert.AreEqual("resource_not_a_view", fileDiagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Apps_ADirectToolOfAHostThatShowsViewsCarriesItsView()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        await using var showing = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { ShowsApps = true });
        await using var hiding = new McpPlugin(createPresentation: null, _home.Path);

        var (shownTools, shown) = await CallShowViewAsync(showing, project.Path);
        var (_, hidden) = await CallShowViewAsync(hiding, project.Path);

        CollectionAssert.DoesNotContain(shownTools, "mcp__tiny__view_refresh");
        Assert.IsTrue(shown.Success);
        Assert.AreEqual(new AgentToolAppResult("tiny", "show_view", "ui://tiny/view", shown.App!.StructuredContent, shown.App.Meta), shown.App);
        Assert.AreEqual("kept-for-the-view", shown.App.StructuredContent!.Value.GetProperty("apiKey").GetString());
        // The model's text does not carry the view.
        Assert.IsFalse(AgentSession.RenderToolResult(shown).Contains("kept-for-the-view", StringComparison.Ordinal));
        Assert.IsNull(hidden.App);
    }

    [TestMethod]
    public async Task Apps_TheRequestsOfAViewUseTheConnectionOfItsSession()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: UiTools);
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { ShowsApps = true });
        await using var hiding = new McpPlugin(createPresentation: null, _home.Path);
        var request = new McpAppRequest("one", project.Path, SpaceId: null);
        using var none = JsonDocument.Parse("{}");

        var view = await plugin.OpenAppViewAsync(request, "tiny", "show_view", "ui://tiny/view", CancellationToken.None);
        var first = await plugin.CallAppToolAsync(request, "tiny", "view_refresh", none.RootElement, CancellationToken.None);
        var second = await plugin.CallAppToolAsync(request, "tiny", "view_refresh", none.RootElement, CancellationToken.None);
        var refused = await plugin.CallAppToolAsync(request, "tiny", "model_only", none.RootElement, CancellationToken.None);
        var resource = await plugin.ReadAppResourceAsync(request, "tiny", "ui://tiny/plain", CancellationToken.None);
        var notShown = await hiding.OpenAppViewAsync(request, "tiny", "show_view", "ui://tiny/view", CancellationToken.None);

        Assert.IsTrue(view.Succeeded, view.Error);
        StringAssert.Contains(view.Value!.Html, "Tiny view");
        Assert.AreEqual("show_view", view.Value.Tool!.Value.GetProperty("name").GetString());
        // The same server answers both calls: its state goes on from one to the next.
        Assert.AreEqual(1, JsonDocument.Parse(first.Value!).RootElement.GetProperty("structuredContent").GetProperty("count").GetInt32());
        Assert.AreEqual(2, JsonDocument.Parse(second.Value!).RootElement.GetProperty("structuredContent").GetProperty("count").GetInt32());
        Assert.AreEqual("tool_not_for_app", refused.ErrorCode);
        Assert.AreEqual("not a view", JsonDocument.Parse(resource.Value!).RootElement.GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.AreEqual("apps_not_shown", notShown.ErrorCode);
    }

    [TestMethod]
    public void Apps_TheViewOfACallIsRecordedWithItsEndOnly()
    {
        using var arguments = JsonDocument.Parse("{}");
        var call = new AgentMessagePart.ToolCall("call", "mcp__tiny__show_view", arguments.RootElement.Clone());
        using var data = JsonDocument.Parse("""{"items":["alpha"]}""");
        var result = new AgentToolResult(true, [new AgentToolResultItem.Text("Showed the view.")])
        {
            App = new AgentToolAppResult("tiny", "show_view", "ui://tiny/view", data.RootElement.Clone()),
        };

        var details = AgentSession.CreateToolResultDetails(call, result, workingDirectory: null);
        var without = AgentSession.CreateToolResultDetails(call, result with { App = null }, workingDirectory: null);

        var app = details.GetProperty("result").GetProperty("app");
        Assert.AreEqual("ui://tiny/view", app.GetProperty("resourceUri").GetString());
        Assert.AreEqual("alpha", app.GetProperty("structuredContent").GetProperty("items")[0].GetString());
        Assert.IsFalse(without.GetProperty("result").TryGetProperty("app", out _));
        Assert.AreEqual("Showed the view.", AgentSession.RenderToolResult(result));
    }

    private async Task<(string[] Tools, AgentToolResult Result)> CallShowViewAsync(McpPlugin plugin, string projectPath)
    {
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test")
        {
            plugin.GetAltaCommands().Single().CreateCommandNode(CreateAltaContext(new StringWriter(CultureInfo.InvariantCulture), stderr, projectPath, "one")),
        };
        Assert.AreEqual(0, await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stderr.ToString());
        var before = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = projectPath, SessionId = "one",
        });
        var tool = before!.AdditionalTools.Single(tool => tool.Spec.Name == "mcp__tiny__show_view");
        using var arguments = JsonDocument.Parse("{}");
        var result = await tool.Handler(new AgentToolInvocation(new ModelProviderId("test"), "one", "call", tool.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);
        return ([.. before.AdditionalTools.Select(static definition => definition.Spec.Name)], result);
    }
}
