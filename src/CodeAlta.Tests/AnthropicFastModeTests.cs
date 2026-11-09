using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// The fast mode of the Claude API (speed: "fast"): Claude Opus 5.5 has it, off until a session asks for it, and only the
// Claude API is sent the field.
[TestClass]
public sealed class AnthropicFastModeTests
{
    [TestMethod]
    public async Task ASessionThatAsksForFastMode_IsSentSpeedFast()
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5", fastMode: true, AgentReasoningEffort.High).ConfigureAwait(false);

        Assert.AreEqual("fast", sent.RootElement.GetProperty("speed").GetString());
        Assert.AreEqual("adaptive", sent.RootElement.GetProperty("thinking").GetProperty("type").GetString(), "The other changes of the request stay.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(null)]
    public async Task ASessionThatDoesNotAskForIt_IsSentNoSpeed(bool? fastMode)
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5", fastMode).ConfigureAwait(false);

        Assert.IsFalse(sent.RootElement.TryGetProperty("speed", out _));
    }

    [TestMethod]
    public async Task AnotherEndpointOfTheProtocol_IsSentNoSpeed()
    {
        var sent = await SendAsync(new Uri("https://api.minimax.example/anthropic"), "claude-opus-5-5", fastMode: true).ConfigureAwait(false);

        Assert.IsFalse(sent.RootElement.TryGetProperty("speed", out _));
    }

    [TestMethod]
    public void TheModelsOfTheClaudeApi_ThatRunFast_HaveFastModeOff()
    {
        static IReadOnlyList<AgentModelInfo> Enrich(Uri? baseUri)
            => AnthropicModelProviderRuntime.EnrichModels(
                new AnthropicProviderOptions { ProviderKey = "anthropic", ApiKey = "test-key", BaseUri = baseUri },
                new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "anthropic-messages", ProviderKey = "anthropic", DisplayName = "Anthropic",
                    TransportKind = AgentTransportKind.AnthropicMessages, BaseUri = baseUri,
                },
                [new("claude-opus-5-5"), new("claude-opus-5-5-20261015"), new("claude-opus-5"), new("claude-opus-4-8"), new("claude-sonnet-5-5")]);

        var claudeApi = Enrich(null);
        CollectionAssert.AreEqual(new bool?[] { false, false, null, null, null }, claudeApi.Select(static model => model.FastMode).ToArray());
        Assert.IsTrue(Enrich(new Uri("https://gateway.example/anthropic")).All(static model => model.FastMode is null), "Another endpoint is not known to have it.");
    }

    private static async Task<JsonDocument> SendAsync(Uri baseUri, string modelId, bool? fastMode, AgentReasoningEffort? reasoningEffort = null)
    {
        var bodies = new List<string>();
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "ok"),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = baseUri,
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            })),
        });

        var request = AnthropicStopReasonTests.CreateRequest() with
        {
            Provider = AnthropicStopReasonTests.CreateRequest().Provider with { BaseUri = baseUri },
            ModelId = modelId,
            ModelInfo = new AgentModelInfo(modelId),
            ReasoningEffort = reasoningEffort,
            FastMode = fastMode,
        };
        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(1, bodies.Count, "One request was sent.");
        return JsonDocument.Parse(bodies[0]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
