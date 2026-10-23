using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugin.Mcp;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class McpAppsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public void Policy_IsTheRestrictiveOneOfMcpAppsWithoutDeclarations()
    {
        var policy = DesktopMcpAppPolicy.Build(McpAppResourceMeta.Empty);

        Assert.AreEqual(
            "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
            "media-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'",
            policy);
    }

    [TestMethod]
    public void Policy_AddsTheDeclaredOriginsAndDropsWhatIsNotAnOrigin()
    {
        var policy = DesktopMcpAppPolicy.Build(new McpAppResourceMeta
        {
            ConnectDomains = ["https://api.example.com", "wss://live.example.com:8443", "*", "'unsafe-eval'", "https://a.example.com; script-src *", "data:"],
            ResourceDomains = ["https://*.cdn.example.com", "https://cdn.example.com/lib/", "javascript:alert(1)", "https://x.example.com 'unsafe-eval'"],
            FrameDomains = ["https://www.youtube.com"],
            BaseUriDomains = ["http://localhost:3000"],
        });

        StringAssert.Contains(policy, "connect-src https://api.example.com wss://live.example.com:8443;");
        StringAssert.Contains(policy, "script-src 'self' 'unsafe-inline' https://*.cdn.example.com https://cdn.example.com/lib/;");
        StringAssert.Contains(policy, "frame-src https://www.youtube.com;");
        StringAssert.Contains(policy, "base-uri http://localhost:3000;");
        Assert.IsFalse(policy.Contains("unsafe-eval", StringComparison.Ordinal));
        Assert.IsFalse(policy.Contains("javascript:", StringComparison.Ordinal));
        Assert.IsFalse(policy.Contains("data:;", StringComparison.Ordinal) && policy.Contains("connect-src data:", StringComparison.Ordinal));
        Assert.AreEqual(11, policy.Split("; ").Length, "No declared value adds a directive.");
    }

    [TestMethod]
    public void Views_ServeTheDocumentOfAnOpenViewOnlyAtItsAddress()
    {
        var views = new DesktopMcpAppViews();
        var view = views.Open("session", "tiny", "show_view", null, new McpAppView("<!DOCTYPE html><p>hi</p>", McpAppResourceMeta.Empty, Tool: null));
        var other = views.Open("session", "other", "show_view", null, new McpAppView("<p>other</p>", McpAppResourceMeta.Empty, Tool: null));

        var served = views.GetResponse(Request(view.FrameUrl));
        var elsewhere = views.GetResponse(Request($"mcpapp://{other.Origin}/view/{view.Token}"));
        var post = views.GetResponse(Request(view.FrameUrl, "POST"));
        var script = views.GetResponse(Request($"mcpapp://{view.Origin}/app.js"));

        Assert.AreEqual(200, served!.StatusCode);
        Assert.AreEqual(DesktopMcpAppPolicy.Build(McpAppResourceMeta.Empty), served.Headers["Content-Security-Policy"]);
        Assert.AreEqual("no-store", served.Headers["Cache-Control"]);
        Assert.AreEqual(404, elsewhere!.StatusCode, "A token is served on the origin of its own view only.");
        Assert.AreEqual(404, post!.StatusCode);
        Assert.AreEqual(404, script!.StatusCode);
        Assert.AreNotEqual(view.Origin, other.Origin);
        Assert.AreEqual(view.Origin, views.Open("session", "tiny", "show_view", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null)).Origin);

        Assert.IsTrue(views.Close(view.Token));
        Assert.AreEqual(404, views.GetResponse(Request(view.FrameUrl))!.StatusCode);
    }

    [TestMethod]
    public void Views_ForgetTheOldestBeyondTheirLimit()
    {
        var views = new DesktopMcpAppViews();
        var first = views.Open("session", "tiny", "t", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null));
        for (var index = 0; index < DesktopMcpAppViews.MaximumViews; index++) views.Open("session", "tiny", "t", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null));

        Assert.AreEqual(DesktopMcpAppViews.MaximumViews, views.Count);
        Assert.IsNull(views.Find(first.Token));
    }

    [TestMethod]
    public void Views_OpenedAndClosedOneAfterTheOtherKeepNoTokenOfTheClosedOnes()
    {
        var views = new DesktopMcpAppViews();
        for (var index = 0; index < 1000; index++)
        {
            var view = views.Open("session", "tiny", "t", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null));
            Assert.IsTrue(views.Close(view.Token));
        }

        var kept = views.Open("session", "tiny", "t", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null));
        for (var index = 0; index < DesktopMcpAppViews.MaximumViews - 1; index++) views.Open("session", "tiny", "t", null, new McpAppView("<p/>", McpAppResourceMeta.Empty, null));

        Assert.AreEqual(DesktopMcpAppViews.MaximumViews, views.Count);
        Assert.IsNotNull(views.Find(kept.Token), "Closed views do not take the places of open ones.");
        Assert.IsTrue(views.QueuedTokens <= 2 * DesktopMcpAppViews.MaximumViews + 8);
    }

    [TestMethod]
    public void ReadCall_ReadsTheViewOfACallFromItsRecord()
    {
        var activity = Completed("""
            {"toolName":"mcp__tiny__show_view","arguments":{"arguments_json":"{\"title\":\"Hi\"}"},
             "result":{"success":true,"items":[{"$type":"text","value":"Showed the view."}],
                       "app":{"server":"tiny","tool":"show_view","resourceUri":"ui://tiny/view","structuredContent":{"items":["a"]},"meta":{"at":1},
                              "content":[{"type":"text","text":"{\"apiKey\":\"raw\"}"}]}}}
            """);
        var withoutContent = Completed("""
            {"result":{"success":false,"items":[{"$type":"text","value":"[redacted] text"}],"app":{"server":"tiny","tool":"t","resourceUri":"ui://tiny/view"}}}
            """);
        var plain = Completed("""{"toolName":"echo","arguments":{},"result":{"success":true,"items":[]}}""");
        var notAView = Completed("""{"result":{"app":{"server":"tiny","tool":"t","resourceUri":"https://example.com"}}}""");

        var call = McpAppsService.ReadCall(activity)!;

        Assert.AreEqual(("tiny", "show_view", "ui://tiny/view"), (call.Server, call.Tool, call.ResourceUri));
        Assert.AreEqual("""{"title":"Hi"}""", call.Input);
        using var result = JsonDocument.Parse(call.Result);
        // The view gets the content blocks the server sent, not the redacted text of the model.
        Assert.AreEqual("""{"apiKey":"raw"}""", result.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        using var fallback = JsonDocument.Parse(McpAppsService.ReadCall(withoutContent)!.Result);
        Assert.AreEqual("[redacted] text", fallback.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.IsTrue(fallback.RootElement.GetProperty("isError").GetBoolean());
        Assert.AreEqual("a", result.RootElement.GetProperty("structuredContent").GetProperty("items")[0].GetString());
        Assert.AreEqual(1, result.RootElement.GetProperty("_meta").GetProperty("at").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("isError").GetBoolean());
        Assert.IsNull(McpAppsService.ReadCall(plain));
        Assert.IsNull(McpAppsService.ReadCall(notAView));
        Assert.AreEqual("tiny", HistoryToolProjection.Project(activity).App);
        Assert.IsNull(HistoryToolProjection.Project(plain).App);
    }

    [TestMethod]
    public async Task Service_RefusesWithoutAHostAnotherEpochAndAClosedView()
    {
        var unavailable = new McpAppsService();
        Assert.AreEqual("unavailable", (await unavailable.OpenAsync(new(Epoch, "session", "1"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.CallToolAsync(new(Epoch, "token", "x", null), default)).Status);

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.OpenAsync(new("another", "session", "1"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, "session", "-1"), default)).Status);
        Assert.AreEqual("not_a_view", (await fixture.Service.OpenAsync(new(Epoch, "session", "2"), default)).Status);
        Assert.AreEqual("closed", (await fixture.Service.CallToolAsync(new(Epoch, "unknown", "view_refresh", null), default)).Status);
        Assert.AreEqual("closed", fixture.Service.Close(new(Epoch, "unknown")).Status);
    }

    [TestMethod]
    public async Task Service_OpensAViewAndRunsItsRequestsOnItsServerOnly()
    {
        await using var fixture = await Fixture.CreateAsync();

        var opened = await fixture.Service.OpenAsync(new(Epoch, "session", "1"), default);
        Assert.AreEqual("ok", opened.Status, opened.Error);
        var view = opened.View!;
        var refresh = await fixture.Service.CallToolAsync(new(Epoch, view.Token, "view_refresh", "{}"), default);
        var refused = await fixture.Service.CallToolAsync(new(Epoch, view.Token, "model_only", null), default);
        var invalid = await fixture.Service.CallToolAsync(new(Epoch, view.Token, "view_refresh", "[1,"), default);
        var read = await fixture.Service.ReadResourceAsync(new(Epoch, view.Token, "ui://tiny/plain"), default);
        var document = fixture.Views.GetResponse(Request(view.FrameUrl))!;

        Assert.AreEqual("tiny", view.Server);
        StringAssert.StartsWith(view.FrameUrl, "mcpapp://");
        Assert.AreEqual(true, view.PrefersBorder);
        Assert.AreEqual("show_view", JsonDocument.Parse(view.ToolDefinition!).RootElement.GetProperty("name").GetString());
        StringAssert.Contains(Encoding.UTF8.GetString(document.Bytes.Span), "<title>Tiny view</title>");
        StringAssert.Contains(document.Headers["Content-Security-Policy"], "connect-src https://api.example.com;");
        Assert.AreEqual("ok", refresh.Status, refresh.Error);
        Assert.AreEqual(1, JsonDocument.Parse(refresh.Json!).RootElement.GetProperty("structuredContent").GetProperty("count").GetInt32());
        Assert.AreEqual(("failed", "tool_not_for_app"), (refused.Status, refused.Code));
        Assert.AreEqual("invalid", invalid.Status);
        Assert.AreEqual("ok", read.Status, read.Error);
        Assert.AreEqual("ok", fixture.Service.Close(new(Epoch, view.Token)).Status);
        Assert.AreEqual("closed", (await fixture.Service.CallToolAsync(new(Epoch, view.Token, "view_refresh", null), default)).Status);
    }

    private static NeoResourceRequest Request(string uri, string method = "GET")
        => new(new Uri(uri), method, new Dictionary<string, string>(), null, NeoResourceKind.Document, false, ReadOnlyMemory<byte>.Empty);

    private static AgentActivityEvent Completed(string details)
    {
        using var document = JsonDocument.Parse(details);
        return new AgentActivityEvent(new ModelProviderId("test"), "session", DateTimeOffset.UtcNow, new AgentRunId("run"), AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed, "call", null, "mcp__tiny__show_view", null, document.RootElement.Clone());
    }

    // A project whose MCP configuration starts the tiny server with its view, and a service whose record 1 is a call of
    // `show_view` and whose record 2 is a call without a view.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, McpPlugin plugin, DesktopMcpAppViews views, McpAppsService service)
        {
            _root = root;
            Plugin = plugin;
            Views = views;
            Service = service;
        }

        public McpPlugin Plugin { get; }

        public DesktopMcpAppViews Views { get; }

        public McpAppsService Service { get; }

        public static Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.McpAppsRpcTests." + Guid.NewGuid().ToString("N"));
            var project = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(Path.Combine(project, ".alta"));
            Directory.CreateDirectory(home);
            File.WriteAllText(McpConfigDiscovery.GetProjectConfigPath(project), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["tiny"] = new
                    {
                        command = "dotnet",
                        args = new[] { Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.TinyMcpServer.dll") },
                        env = new Dictionary<string, string> { ["MCP_TEST_UI_TOOLS"] = "1" },
                    },
                },
            }));
            var plugin = new McpPlugin(createPresentation: null, home, new McpPluginOptions { ShowsApps = true });
            var views = new DesktopMcpAppViews();
            var records = new Dictionary<long, AgentEvent>
            {
                [1] = Completed("""
                    {"toolName":"mcp__tiny__show_view","arguments":{},
                     "result":{"success":true,"items":[{"$type":"text","value":"Showed the view."}],
                               "app":{"server":"tiny","tool":"show_view","resourceUri":"ui://tiny/view"}}}
                    """),
                [2] = Completed("""{"toolName":"echo","arguments":{},"result":{"success":true,"items":[]}}"""),
            };
            var service = new McpAppsService((_, offset, _) => Task.FromResult(records.GetValueOrDefault(offset)), () => plugin, views, Epoch)
            {
                SessionFolder = (_, _) => ValueTask.FromResult<string?>(project),
            };
            return Task.FromResult(new Fixture(root, plugin, views, service));
        }

        public async ValueTask DisposeAsync()
        {
            await Plugin.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* A server that has not exited yet still holds the folder: the temporary folder is cleaned later. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
