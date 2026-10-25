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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
