using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

// A gateway such as OpenCode (#420): it lists all of its models at each endpoint but serves each one through one API, and
// asks for a header that stays the same for a conversation (x-opencode-session).
[TestClass]
public sealed class OpenCodeGatewayTests
{
    private static readonly string[] Listed = ["chat-model", "responses-model", "anthropic-model", "gemini-model", "mistral-model", "unknown-model"];

    [TestMethod]
    [DataRow(AgentTransportKind.OpenAIChatCompletions, new[] { "chat-model", "mistral-model", "unknown-model" })]
    [DataRow(AgentTransportKind.OpenAIResponses, new[] { "responses-model", "unknown-model" })]
    [DataRow(AgentTransportKind.AnthropicMessages, new[] { "anthropic-model", "unknown-model" })]
    [DataRow(AgentTransportKind.GoogleGeminiApi, new[] { "gemini-model", "unknown-model" })]
    public async Task AGateway_ListsOnlyTheModelsItServesThroughTheApiOfTheProvider(AgentTransportKind transport, string[] expected)
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());

        var kept = AgentModelApiFilter.KeepOwnApi([.. Listed.Select(static id => new AgentModelInfo(id))], catalog, "gateway", transport);

        CollectionAssert.AreEqual(expected, kept.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task AProviderOfOneApi_KeepsAllItsModels_WhateverTheApiItIsConfiguredWith()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());
        IReadOnlyList<AgentModelInfo> models = [new("chat-a"), new("chat-b")];

        Assert.AreSame(models, AgentModelApiFilter.KeepOwnApi(models, catalog, "uniform", AgentTransportKind.OpenAIResponses));
        Assert.AreSame(models, AgentModelApiFilter.KeepOwnApi(models, catalog, "gateway", transport: null), "an API that is not known keeps every model");
        Assert.AreSame(models, AgentModelApiFilter.KeepOwnApi(models, catalog, "missing", AgentTransportKind.OpenAIChatCompletions));
        Assert.AreSame(models, AgentModelApiFilter.KeepOwnApi(models, null, "gateway", AgentTransportKind.OpenAIChatCompletions));
    }

    [TestMethod]
    public async Task TheModelsOfAProvider_AreFilteredWhenTheyAreListed()
    {
        await using var catalog = new ModelsDevCatalogService(CreateDatabase(), new ModelsDevCatalogServiceOptions());

        var listed = AnthropicModelProviderRuntime.EnrichModels(
            new AnthropicProviderOptions { ProviderKey = "opencode-anthropic", ApiKey = "key", ModelCatalog = catalog, ModelsDevProviderId = "gateway" },
            new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages", ProviderKey = "opencode-anthropic", DisplayName = "OpenCode",
                TransportKind = AgentTransportKind.AnthropicMessages,
            },
            [.. Listed.Select(static id => new AgentModelInfo(id))]);

        CollectionAssert.AreEqual(new[] { "anthropic-model", "unknown-model" }, listed.Select(static model => model.Id).ToArray());
    }

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
    public void TheDefaultConfiguration_OffersOpenCode(string key, string name, string url)
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
            Assert.AreEqual("${session.id}", provider.Request!.Headers!["x-opencode-session"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A gateway as models.dev describes OpenCode: the API of a model is the package of its own AI SDK, else the provider's.
    private static ModelsDevDatabase CreateDatabase()
        => ModelsDevDatabaseJson.Deserialize(
            """
            {
              "gateway": {
                "id": "gateway", "name": "Gateway", "npm": "@ai-sdk/openai-compatible", "api": "https://gateway.example/openai/v1",
                "models": {
                  "chat-model": { "id": "chat-model" },
                  "responses-model": { "id": "responses-model", "provider": { "npm": "@ai-sdk/openai" } },
                  "anthropic-model": { "id": "anthropic-model", "provider": { "npm": "@ai-sdk/anthropic", "api": "https://gateway.example/anthropic/v1" } },
                  "gemini-model": { "id": "gemini-model", "provider": { "npm": "@ai-sdk/google" } },
                  "mistral-model": { "id": "mistral-model", "provider": { "npm": "@ai-sdk/mistral" } }
                }
              },
              "uniform": {
                "id": "uniform", "name": "Uniform", "npm": "@ai-sdk/openai-compatible",
                "models": { "chat-a": { "id": "chat-a" }, "chat-b": { "id": "chat-b" } }
              }
            }
            """);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
