using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

// OpenCode Zen and Go (#420): built in, with the header that keeps a conversation on one backend (x-opencode-session), and
// without the free models, which OpenCode answers with 403 outside its own app.
[TestClass]
public sealed class OpenCodeGatewayTests
{
    [TestMethod]
    public async Task AnAnthropicProvider_SendsTheSessionInTheHeaderThatNamesIt()
    {
        var sent = new List<HttpRequestMessage>();
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "opencode-go-anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://opencode.ai/inference/go/anthropic"),
            ExtraHeaders = new Dictionary<string, string> { ["x-opencode-session"] = "${session.id}", ["x-fixed"] = "kept", ["x-run"] = "run-${run.id}" },
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                sent.Add(request);
                var stream = AnthropicStopReasonTests.Stream(
                    AnthropicStopReasonTests.TextBlock(0, "ok"),
                    """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            })),
        });

        _ = await executor.ExecuteTurnAsync(AnthropicStopReasonTests.CreateRequest(), static (_, _) => ValueTask.CompletedTask);

        var request = sent.Single();
        Assert.AreEqual("session-test", request.Headers.GetValues("x-opencode-session").Single());
        Assert.AreEqual("run-run-test", request.Headers.GetValues("x-run").Single());
        Assert.AreEqual("kept", request.Headers.GetValues("x-fixed").Single());
    }

    [TestMethod]
    public void TheHeadersOfATurn_AreTheOnesThatNameAPlaceholder_Filled()
    {
        var headers = new Dictionary<string, string> { ["x-opencode-session"] = "${session.id}", ["x-fixed"] = "kept" };

        var turn = AgentRequestHeaderPlaceholders.ForTurn(headers, AnthropicStopReasonTests.CreateRequest());

        Assert.IsNotNull(turn);
        CollectionAssert.AreEqual(new[] { "x-opencode-session" }, turn.Keys.ToArray());
        Assert.AreEqual("session-test", turn["x-opencode-session"]);
        Assert.IsNull(AgentRequestHeaderPlaceholders.ForTurn(new Dictionary<string, string> { ["x-fixed"] = "kept" }, AnthropicStopReasonTests.CreateRequest()));
        CollectionAssert.AreEqual(new[] { "x-fixed" }, AgentRequestHeaderPlaceholders.OutsideTurn(headers)!.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("opencode", "OpenCode Zen", "https://opencode.ai/inference/openai/v1")]
    [DataRow("opencode-go", "OpenCode Go", "https://opencode.ai/inference/go/openai/v1")]
    public void TheDefaultConfiguration_OffersOpenCode_WithoutItsFreeModels(string key, string name, string url)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-opencode-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), CodeAltaConfigStore.GetDefaultGlobalConfigContent());
            var provider = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).LoadGlobal().Providers![key];

            Assert.AreEqual(false, provider.Enabled);
            Assert.AreEqual(name, provider.DisplayName);
            Assert.AreEqual("openai-chat", provider.ProviderType);
            Assert.AreEqual(url, provider.ApiUrl);
            Assert.AreEqual(key, provider.ModelsDevProviderId);
            Assert.AreEqual("glm-5.3", provider.Model);
            Assert.AreEqual("${session.id}", provider.Request!.Headers!["x-opencode-session"]);

            IReadOnlyList<AgentModelInfo> listed = [new("big-pickle"), new("glm-5.3"), new("kimi-k2.5-free"), new("grok-code"), new("claude-sonnet-5-5"), new("free-model"), new("grok-code-fast")];
            var shown = AgentModelCatalogFilter.ApplyIncludeRegex(listed, provider.ModelsIncludeRegex);
            CollectionAssert.AreEqual(new[] { "glm-5.3", "claude-sonnet-5-5", "free-model", "grok-code-fast" }, shown.Select(static model => model.Id).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
