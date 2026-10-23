using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AnthropicEndpointHeadersTests
{
    [TestMethod]
    public async Task ClaudeApi_GetsTheKeyAsABearerToken_AndTheVersionOfTheSdk()
    {
        var headers = await SendAsync(new Uri("https://api.anthropic.com"), apiKey: "test-key", authToken: null);

        Assert.AreEqual("Bearer test-key", Single(headers, "Authorization"));
        Assert.IsFalse(headers.ContainsKey("X-Api-Key"));
        Assert.AreEqual("2026-10-21", Single(headers, "anthropic-version"));
    }

    [TestMethod]
    public async Task AnotherEndpoint_GetsTheKeyInXApiKey_AndTheVersionItTookBefore()
    {
        // As the SDK sent them before its version 13: a gateway may read only X-Api-Key and refuse a version it does not know.
        var headers = await SendAsync(new Uri("https://api.kimi.com/coding/"), apiKey: "test-key", authToken: null);

        Assert.AreEqual("test-key", Single(headers, "X-Api-Key"));
        Assert.IsFalse(headers.ContainsKey("Authorization"), "The key goes out once.");
        Assert.AreEqual(AnthropicCompatibleEndpointHandler.ApiVersion, Single(headers, "anthropic-version"));
    }

    [TestMethod]
    public async Task AnotherEndpoint_KeepsABearerTokenThatIsNoApiKey()
    {
        // The token of Copilot is a bearer token on its Messages route.
        var headers = await SendAsync(new Uri("https://api.githubcopilot.com"), apiKey: null, authToken: "tid=1;exp=2;sku=x");

        Assert.AreEqual("Bearer tid=1;exp=2;sku=x", Single(headers, "Authorization"));
        Assert.IsFalse(headers.ContainsKey("X-Api-Key"));
        Assert.AreEqual(AnthropicCompatibleEndpointHandler.ApiVersion, Single(headers, "anthropic-version"));
    }

    private static string Single(Dictionary<string, string[]> headers, string name)
    {
        Assert.IsTrue(headers.TryGetValue(name, out var values), $"{name} is sent.");
        Assert.HasCount(1, values, $"{name}: {string.Join(" | ", values)}");
        return values[0];
    }

    private static async Task<Dictionary<string, string[]>> SendAsync(Uri baseUri, string? apiKey, string? authToken)
    {
        Dictionary<string, string[]>? headers = null;
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "ok"),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = apiKey,
            AuthToken = authToken,
            BaseUri = baseUri,
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                headers = request.Headers.ToDictionary(static header => header.Key, static header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            })),
        });
        var request = new AgentTurnRequest
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
            ModelId = "claude-haiku-4-5",
            ModelInfo = new AgentModelInfo("claude-haiku-4-5"),
            MaxOutputTokens = 1024,
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello.")])],
            Tools = [],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask);

        Assert.IsNotNull(headers, "The request was sent.");
        return headers;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
