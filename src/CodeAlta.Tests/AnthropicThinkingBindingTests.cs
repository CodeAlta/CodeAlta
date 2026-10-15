using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// A thinking block is signed for the conversation before it and for the account that produced it: on the Claude API, a
// block that no longer matches (another API key, an earlier part of the request changed) is dropped by the API instead
// of failing every later request of the session (#147).
[TestClass]
public sealed class AnthropicThinkingBindingTests
{
    private const string Beta = "thinking-binding-controls-2026-08-01";

    private const string MessageStream =
        """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [TestMethod]
    public async Task ClaudeApi_ReplaysTheThinkingOfThePreviousTurn_AndAsksToDropABlockItCannotMatch()
    {
        // The thinking of the first turn was signed when the provider had another API key.
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5", AgentReasoningEffort.High).ConfigureAwait(false);

        var thinking = sent.Body.RootElement.GetProperty("thinking");
        Assert.AreEqual("adaptive", thinking.GetProperty("type").GetString());
        Assert.AreEqual("drop_block", thinking.GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.AreEqual("summarized", thinking.GetProperty("display").GetString());
        Assert.AreEqual("high", sent.Body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        CollectionAssert.Contains(sent.Betas, Beta);

        // The block goes back as it came: dropping it is for the API, which alone can check the signature.
        var replayed = sent.Body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.AreEqual("thinking", replayed.GetProperty("type").GetString());
        Assert.AreEqual("signature-of-the-previous-key", replayed.GetProperty("signature").GetString());
    }

    [TestMethod]
    public async Task ClaudeApi_AModelThatThinksByDefault_IsSentItsDefaultThinkingWithTheBindingControl()
    {
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-fable-5-1", reasoningEffort: null).ConfigureAwait(false);

        var thinking = sent.Body.RootElement.GetProperty("thinking");
        Assert.AreEqual("adaptive", thinking.GetProperty("type").GetString());
        Assert.IsFalse(thinking.TryGetProperty("display", out _), "The display of the model stays its default.");
        Assert.AreEqual("drop_block", thinking.GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.IsFalse(sent.Body.RootElement.TryGetProperty("output_config", out _), "No effort is named when the session names none.");
        CollectionAssert.Contains(sent.Betas, Beta);
    }

    [TestMethod]
    public async Task ClaudeApi_AnOlderModelWithoutEffort_IsSentNoThinking()
    {
        // Claude Opus 4.8 runs without thinking when the request names none: that stays so.
        var sent = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-4-8", reasoningEffort: null).ConfigureAwait(false);

        Assert.IsFalse(sent.Body.RootElement.TryGetProperty("thinking", out _));
        CollectionAssert.DoesNotContain(sent.Betas, Beta);
    }

    [TestMethod]
    public async Task AnthropicCompatibleEndpoint_IsSentNeitherTheBetaNorTheBindingControl()
    {
        var sent = await SendAsync(new Uri("https://gateway.example.test/anthropic"), "claude-opus-5-5", AgentReasoningEffort.High).ConfigureAwait(false);

        var thinking = sent.Body.RootElement.GetProperty("thinking");
        Assert.AreEqual("adaptive", thinking.GetProperty("type").GetString());
        Assert.IsFalse(thinking.TryGetProperty("block_binding", out _));
        CollectionAssert.DoesNotContain(sent.Betas, Beta);

        var withoutEffort = await SendAsync(new Uri("https://gateway.example.test/anthropic"), "claude-opus-5-5", reasoningEffort: null).ConfigureAwait(false);
        Assert.IsFalse(withoutEffort.Body.RootElement.TryGetProperty("thinking", out _));
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("https://api.anthropic.com", true)]
    [DataRow("https://api.anthropic.com/v1/", true)]
    [DataRow("https://api.githubcopilot.com", false)]
    [DataRow("https://api.minimax.io/anthropic", false)]
    [DataRow("http://localhost:4000", false)]
    public void IsClaudeApi_OnlyForTheEndpointOfAnthropic(string? baseUri, bool expected)
        => Assert.AreEqual(expected, AnthropicModelProviderRuntime.IsClaudeApi(baseUri is null ? null : new Uri(baseUri)));

    private static async Task<(JsonDocument Body, List<string> Betas)> SendAsync(Uri? baseUri, string modelId, AgentReasoningEffort? reasoningEffort)
    {
        string? body = null;
        var betas = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/v1/messages", StringComparison.Ordinal), request.RequestUri.AbsolutePath);
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (request.Headers.TryGetValues("anthropic-beta", out var values))
            {
                betas.AddRange(values.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageStream, Encoding.UTF8, "text/event-stream") };
        }));
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = baseUri,
            HttpClient = httpClient,
        });

        _ = await executor.ExecuteTurnAsync(CreateRequest(baseUri, modelId, reasoningEffort), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(body, "One request was sent.");
        return (JsonDocument.Parse(body), betas);
    }

    private static AgentTurnRequest CreateRequest(Uri? baseUri, string modelId, AgentReasoningEffort? reasoningEffort)
    {
        var provenance = new AgentReasoningProvenance("anthropic-messages", "anthropic", AgentTransportKind.AnthropicMessages, modelId);
        return new AgentTurnRequest
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                DisplayName = "Anthropic",
                TransportKind = AgentTransportKind.AnthropicMessages,
                BaseUri = baseUri,
            },
            ProviderId = new ModelProviderId("anthropic"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = modelId,
            ModelInfo = new AgentModelInfo(modelId),
            ReasoningEffort = reasoningEffort,
            MaxOutputTokens = 4096,
            SystemMessage = "You are a test agent.",
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Read the file.")]),
                new AgentConversationMessage(AgentConversationRole.Assistant,
                [
                    new AgentMessagePart.Reasoning(string.Empty, "signature-of-the-previous-key", provenance),
                    new AgentMessagePart.Text("Done."),
                ]),
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Next.")]),
            ],
            Tools = [],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
