using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// What a model list of /models says beyond the fields of OpenAI, as the Responses endpoint of Kimi for Coding answers it.
/// </summary>
[TestClass]
public sealed class OpenAIListedModelsTests
{
    private const string KimiModelsJson =
        """
        {
          "data": [
            {
              "id": "k3.1-pro", "created": 1761264000, "object": "model", "owned_by": "moonshot", "display_name": "K3.1 Pro",
              "context_length": 262144, "supports_reasoning": true, "supports_image_in": true, "supports_thinking_type": "only",
              "reasoning": { "type": "only", "effort": { "valid": ["low", "high", "max"], "default": "high" } }
            },
            {
              "id": "kimi-for-coding-highspeed", "created": 1761264000, "object": "model", "owned_by": "moonshot", "display_name": "K2.7 Code Highspeed",
              "context_length": 262144, "supports_image_in": true, "reasoning": { "type": "only" }
            },
            { "id": "plain-model", "created": 1761264000, "object": "model", "owned_by": "someone" }
          ],
          "object": "list"
        }
        """;

    [TestMethod]
    public async Task ListModels_ReadsTheEfforts_TheContextAndTheNameKimiForCodingLists()
    {
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "kimi",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.kimi.test/coding/v1"),
            HttpClient = new HttpClient(new StubHandler(static request =>
            {
                Assert.AreEqual("/coding/v1/models", request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(KimiModelsJson, Encoding.UTF8, "application/json") };
            })),
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            new ModelProviderRuntimeDescriptor { ProtocolFamily = "openai", ProviderKey = "kimi", DisplayName = "Kimi", TransportKind = AgentTransportKind.OpenAIResponses },
            CancellationToken.None).ConfigureAwait(false);

        // The efforts as the endpoint names them, sent as it names them: max is Max.
        var k31 = models.Single(static model => model.Id == "k3.1-pro");
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.Max },
            k31.SupportedReasoningEfforts!.ToArray());
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(k31));
        Assert.AreEqual(AgentReasoningEffort.High, k31.DefaultReasoningEffort);
        Assert.AreEqual("K3.1 Pro", k31.DisplayName);
        Assert.AreEqual(262_144L, k31.Capabilities!["contextWindow"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(k31));
        Assert.AreEqual("max", OpenAIResponsesTurnExecutor.MapReasoningEffortLevel(AgentReasoningEffort.Max)!.Value.ToString());

        // A model that always thinks and names no effort offers none.
        var highspeed = models.Single(static model => model.Id == "kimi-for-coding-highspeed");
        Assert.AreEqual(0, highspeed.SupportedReasoningEfforts!.Count);
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(highspeed));

        // A model of a list that says nothing more is left as the fields of OpenAI give it.
        var plain = models.Single(static model => model.Id == "plain-model");
        Assert.IsNull(plain.SupportedReasoningEfforts);
        Assert.AreEqual("plain-model", plain.DisplayName);
        Assert.IsFalse(AgentReasoningEffortCapability.IsListComplete(plain));
    }

    [TestMethod]
    public async Task ListModels_OfALlamaCppServer_ReadsItsContextAndWhetherItReadsImages()
    {
        // As llama-server 0.8.0 answers: the context in the meta of the data, the capabilities in a "models" array.
        const string llamaCppModelsJson =
            """
            {
              "models": [
                { "name": "qwen-vision", "model": "qwen-vision", "type": "model", "capabilities": ["completion", "multimodal"] },
                { "name": "qwen-text", "model": "qwen-text", "type": "model", "capabilities": ["completion"] }
              ],
              "object": "list",
              "data": [
                { "id": "qwen-vision", "name": "Qwen Vision · Q4_K_M", "aliases": ["qwen-vision"], "object": "model", "created": 1793273553, "owned_by": "llamacpp", "meta": { "n_ctx": 217088, "n_ctx_train": 262144 } },
                { "id": "qwen-text", "aliases": ["qwen-text"], "object": "model", "created": 1793273553, "owned_by": "llamacpp" }
              ]
            }
            """;
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "llama",
            ApiKey = "no-key",
            BaseUri = new Uri("http://127.0.0.1:9931/v1"),
            HttpClient = new HttpClient(new StubHandler(static request =>
            {
                Assert.AreEqual("/v1/models", request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(llamaCppModelsJson, Encoding.UTF8, "application/json") };
            })),
        };
        provider.ModelListAsync = cancellationToken => LlamaCppModelCatalog.ListModelsAsync(provider, cancellationToken);

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            new ModelProviderRuntimeDescriptor { ProtocolFamily = "openai", ProviderKey = "llama", DisplayName = "llama.cpp", TransportKind = AgentTransportKind.OpenAIChatCompletions },
            CancellationToken.None).ConfigureAwait(false);

        var vision = models.Single(static model => model.Id == "qwen-vision");
        Assert.AreEqual(217_088L, vision.Capabilities!["contextWindow"], "The context the server was started with, not the one the model was trained with.");
        Assert.AreEqual(true, AgentImageInputCapability.Read(vision));
        Assert.AreEqual("Qwen Vision · Q4_K_M", vision.DisplayName, "the name a server CodeAlta runs gives the model");
        var text = models.Single(static model => model.Id == "qwen-text");
        Assert.AreEqual("qwen-text", text.DisplayName, "a server that names nothing shows the id");
        Assert.AreEqual(false, AgentImageInputCapability.Read(text), "A model the server lists without multimodal reads no image.");
        Assert.IsFalse(text.Capabilities!.ContainsKey("contextWindow"));
        Assert.IsNull(text.SupportedReasoningEfforts, "The reasoning of the model stays the user's choice.");
    }

    // OpenRouter's shape, which TensorFold follows (#430): the efforts of the model, the one it defaults to, and whether
    // it reasons when a turn names none.
    private const string OpenRouterModelsJson =
        """
        {
          "data": [
            { "id": "GLM-5.3-Flash", "object": "model", "owned_by": "tensorfold",
              "reasoning": { "default_enabled": false, "default_effort": "xhigh", "supported_efforts": ["max", "high", "low"] } },
            { "id": "always-on", "object": "model", "owned_by": "tensorfold",
              "reasoning": { "default_enabled": true, "default_effort": "medium", "supported_efforts": ["minimal", "medium", "high"] } },
            { "id": "other-shape", "object": "model", "owned_by": "someone", "reasoning": { "levels": ["low", "high"] } },
            { "id": "no-selection", "object": "model", "owned_by": "openrouter", "reasoning": { "mandatory": true } },
            { "id": "any-effort", "object": "model", "owned_by": "openrouter", "reasoning": { "supported_efforts": null, "default_enabled": true } }
          ],
          "object": "list"
        }
        """;

    [TestMethod]
    public async Task ListModels_ReadsTheEffortsOfOpenRoutersShape_AndSendsThemAsListed()
    {
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "tensorfold",
            ApiKey = "test-key",
            BaseUri = new Uri("https://tensorfold.test/v1"),
            HttpClient = new HttpClient(new StubHandler(static _ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenRouterModelsJson, Encoding.UTF8, "application/json") })),
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            new ModelProviderRuntimeDescriptor { ProtocolFamily = "openai", ProviderKey = "tensorfold", DisplayName = "TensorFold", TransportKind = AgentTransportKind.OpenAIChatCompletions },
            CancellationToken.None).ConfigureAwait(false);

        // From the lowest effort; off by default, so a turn that names none is sent none; a default it does not list is no default.
        var glm = models.Single(static model => model.Id == "GLM-5.3-Flash");
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.Max }, glm.SupportedReasoningEfforts!.ToArray());
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(glm));
        Assert.IsNull(glm.DefaultReasoningEffort);

        var alwaysOn = models.Single(static model => model.Id == "always-on");
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Minimal, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, alwaysOn.SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual(AgentReasoningEffort.Medium, alwaysOn.DefaultReasoningEffort);

        // A shape the endpoint does not share with another says nothing: the efforts come from elsewhere, as for a plain model.
        var other = models.Single(static model => model.Id == "other-shape");
        Assert.IsNull(other.SupportedReasoningEfforts);
        Assert.IsFalse(AgentReasoningEffortCapability.IsListComplete(other));

        // OpenRouter: without supported_efforts the model does not expose effort selection; null accepts any effort.
        var noSelection = models.Single(static model => model.Id == "no-selection");
        Assert.AreEqual(0, noSelection.SupportedReasoningEfforts!.Count);
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(noSelection));
        var anyEffort = models.Single(static model => model.Id == "any-effort");
        Assert.IsNull(anyEffort.SupportedReasoningEfforts);
        Assert.IsFalse(AgentReasoningEffortCapability.IsListComplete(anyEffort));

        // Each effort the endpoint lists is sent as it names it.
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.Max, glm, out var level));
        Assert.AreEqual("max", level.ToString());
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.Low, glm, out level));
        Assert.AreEqual("low", level.ToString());
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.Minimal, alwaysOn, out level));
        Assert.AreEqual("minimal", level.ToString());
    }

    [TestMethod]
    public void TheHighestEffort_IsSentAsTheLevelTheCatalogGivesTheModel()
    {
        var maxOnly = new AgentModelInfo("max-only", Capabilities: new Dictionary<string, object?> { [AgentReasoningEffortCapability.HighestEffortKey] = "max" });
        var xhigh = new AgentModelInfo("xhigh", Capabilities: new Dictionary<string, object?> { [AgentReasoningEffortCapability.HighestEffortKey] = "xhigh" });
        var unknown = new AgentModelInfo("unknown");

        // Responses: xhigh, or max for a model that has no xhigh.
        Assert.AreEqual("max", OpenAIResponsesTurnExecutor.MapReasoningEffortLevel(AgentReasoningEffort.XHigh, maxOnly)!.Value.ToString());
        Assert.AreEqual("xhigh", OpenAIResponsesTurnExecutor.MapReasoningEffortLevel(AgentReasoningEffort.XHigh, xhigh)!.Value.ToString());
        Assert.AreEqual("xhigh", OpenAIResponsesTurnExecutor.MapReasoningEffortLevel(AgentReasoningEffort.XHigh, unknown)!.Value.ToString());

        // Chat completions: the level the model has, and high at most for a model the catalog says nothing of.
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.XHigh, maxOnly, out var level));
        Assert.AreEqual("max", level.ToString());
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.XHigh, xhigh, out level));
        Assert.AreEqual("xhigh", level.ToString());
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.Max, xhigh, out level));
        Assert.AreEqual("max", level.ToString());
        Assert.IsTrue(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.XHigh, unknown, out level));
        Assert.AreEqual("high", level.ToString());
        Assert.IsFalse(OpenAIChatTurnExecutor.TryMapReasoningEffort(AgentReasoningEffort.Max, unknown, out _));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
