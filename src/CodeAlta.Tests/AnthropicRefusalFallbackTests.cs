using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// The server-side fallback of the Claude API: a request a safety classifier declines is run again on another model
// instead of ending on a refusal (fallbacks: "default", beta server-side-fallback-2026-07-01).
[TestClass]
public sealed class AnthropicRefusalFallbackTests
{
    private const string Beta = "server-side-fallback-2026-07-01";

    [TestMethod]
    [DataRow("claude-fable-5-1")]
    [DataRow("claude-opus-5-5")]
    [DataRow("claude-opus-5")]
    [DataRow("claude-sonnet-5-5")]
    public async Task ClaudeApi_AModelThatTakesTheFallback_IsSentTheDefaultFallback(string modelId)
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), modelId, refusalFallback: true).ConfigureAwait(false);

        Assert.AreEqual("default", sent.Body.RootElement.GetProperty("fallbacks").GetString());
        CollectionAssert.Contains(sent.Betas, Beta);
    }

    [TestMethod]
    [DataRow("claude-haiku-5-5")]
    [DataRow("claude-sonnet-5")]
    [DataRow("claude-opus-4-8")]
    [DataRow("claude-fable-5")]
    public async Task ClaudeApi_AModelWithoutTheFallback_IsSentNone(string modelId)
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), modelId, refusalFallback: true).ConfigureAwait(false);

        Assert.IsFalse(sent.Body.RootElement.TryGetProperty("fallbacks", out _));
        CollectionAssert.DoesNotContain(sent.Betas, Beta);
    }

    [TestMethod]
    public async Task TheFallback_IsNotSent_WhenTheProviderTurnsItOff_OrToAnotherEndpoint()
    {
        var off = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5", refusalFallback: false).ConfigureAwait(false);
        Assert.IsFalse(off.Body.RootElement.TryGetProperty("fallbacks", out _));
        CollectionAssert.DoesNotContain(off.Betas, Beta);

        var gateway = await SendAsync(new Uri("https://gateway.example.test/anthropic"), "claude-opus-5-5", refusalFallback: true).ConfigureAwait(false);
        Assert.IsFalse(gateway.Body.RootElement.TryGetProperty("fallbacks", out _));
        CollectionAssert.DoesNotContain(gateway.Betas, Beta);
    }

    [TestMethod]
    public async Task TheFallback_IsSentWithTheThinkingAndItsOwnBetas()
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5", refusalFallback: true, AgentReasoningEffort.High).ConfigureAwait(false);

        Assert.AreEqual("default", sent.Body.RootElement.GetProperty("fallbacks").GetString());
        Assert.AreEqual("adaptive", sent.Body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        CollectionAssert.Contains(sent.Betas, Beta);
        CollectionAssert.Contains(sent.Betas, "thinking-binding-controls-2026-08-01");
    }

    [TestMethod]
    public async Task AFallbackInTheStream_SaysWhichModelAnswered_AndDropsTheReasoningOfTheDeclinedModel()
    {
        // The requested model thought, began to answer, was declined, and the fallback model went on.
        var stream = AnthropicStopReasonTests.Stream(
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"declined reasoning"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-of-the-declined-model"}}""",
            """{"type":"content_block_stop","index":0}""",
            AnthropicStopReasonTests.TextBlock(1, "Partial "),
            """{"type":"content_block_start","index":2,"content_block":{"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-4-8"}}}""",
            """{"type":"content_block_stop","index":2}""",
            AnthropicStopReasonTests.TextBlock(3, "answer."),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":9}}""");
        var updates = new List<AgentTurnSessionUpdate>();
        var executor = CreateExecutor(new Uri("https://api.anthropic.com"), stream, [], refusalFallback: true);

        var response = await executor.ExecuteTurnAsync(
            CreateRequest(new Uri("https://api.anthropic.com"), "claude-opus-5-5", null),
            static (_, _) => ValueTask.CompletedTask,
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var rerouted = updates.Single(static update => update.Kind == AgentSessionUpdateKind.ModelChanged);
        StringAssert.Contains(rerouted.Message, "claude-opus-4-8");
        Assert.AreEqual("claude-opus-5-5", rerouted.Details!.Value.GetProperty("requestedModel").GetString());
        Assert.AreEqual("claude-opus-4-8", rerouted.Details!.Value.GetProperty("effectiveModel").GetString());
        Assert.IsFalse(rerouted.Details!.Value.TryGetProperty("modelId", out _), "The session keeps the model it selected.");
        Assert.IsFalse(response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Any(), "The reasoning of the declined model is not replayed.");
        Assert.AreEqual("Partial answer.", string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)));
        Assert.AreEqual("claude-opus-4-8", response.Usage?.LastOperation?.Model);
    }

    [TestMethod]
    public async Task AModelIdThatOnlyGainsItsDate_IsNotAReroute()
    {
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "ok"),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""")
            .Replace("\"model\":\"claude-test\"", "\"model\":\"claude-haiku-4-5-20251001\"", StringComparison.Ordinal);
        var updates = new List<AgentTurnSessionUpdate>();
        var executor = CreateExecutor(new Uri("https://api.anthropic.com"), stream, [], refusalFallback: true);

        _ = await executor.ExecuteTurnAsync(
            CreateRequest(new Uri("https://api.anthropic.com"), "claude-haiku-4-5", null),
            static (_, _) => ValueTask.CompletedTask,
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.IsFalse(updates.Any(static update => update.Kind == AgentSessionUpdateKind.ModelChanged));
    }

    private static async Task<(JsonDocument Body, List<string> Betas)> SendAsync(Uri baseUri, string modelId, bool refusalFallback, AgentReasoningEffort? reasoningEffort = null)
    {
        var bodies = new List<(string Body, List<string> Betas)>();
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "ok"),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = CreateExecutor(baseUri, stream, bodies, refusalFallback);

        _ = await executor.ExecuteTurnAsync(CreateRequest(baseUri, modelId, reasoningEffort), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(1, bodies.Count, "One request was sent.");
        return (JsonDocument.Parse(bodies[0].Body), bodies[0].Betas);
    }

    private static IModelProviderTurnExecutor CreateExecutor(Uri baseUri, string stream, List<(string Body, List<string> Betas)> bodies, bool refusalFallback)
        => AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = baseUri,
            RefusalFallback = refusalFallback,
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                var betas = request.Headers.TryGetValues("anthropic-beta", out var values)
                    ? values.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
                    : [];
                bodies.Add((request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), betas));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            })),
        });

    private static AgentTurnRequest CreateRequest(Uri baseUri, string modelId, AgentReasoningEffort? reasoningEffort)
        => AnthropicStopReasonTests.CreateRequest() with
        {
            Provider = AnthropicStopReasonTests.CreateRequest().Provider with { BaseUri = baseUri },
            ModelId = modelId,
            ModelInfo = new AgentModelInfo(modelId),
            ReasoningEffort = reasoningEffort,
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
