using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// An endpoint without the binding control of the Claude API (Copilot) refuses a request that replays a thinking block
// it cannot verify, and the conversation of the session replays it on every request: the request is asked again with
// that thinking as text, and the thinking it refused is sent so from then on.
[TestClass]
public sealed class RefusedThinkingReplayTests
{
    private const string TokenVariable = "CODEALTA_TEST_COPILOT_REFUSED_THINKING_TOKEN";
    private const string MessagesAddress = "https://api.individual.githubcopilot.com/v1/messages";
    private const string Refusal = """{"type":"error","error":{"type":"invalid_request_error","message":"messages.1.content.0: Invalid `signature` in `thinking` block"},"request_id":"req_test"}""";
    private const string End = """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":3}}""";

    [TestMethod]
    public async Task ARefusedSignature_IsAskedAgainWithTheThinkingAsText_AndIsNotSentAgain()
    {
        var bodies = new List<string>();
        var answers = new Queue<Func<HttpResponseMessage>>([
            static () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Refusal, Encoding.UTF8, "application/json") },
            static () => Ok(AnthropicStopReasonTests.Stream(Thinking(0, "read the tests", "sig-new"), AnthropicStopReasonTests.TextBlock(1, "Second."), End)),
            static () => Ok(AnthropicStopReasonTests.Stream(AnthropicStopReasonTests.TextBlock(0, "Third."), End)),
        ]);
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = TokenVariable,
            },
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                Assert.AreEqual(new Uri(MessagesAddress), request.RequestUri);
                bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return answers.Dequeue()();
            })),
        });

        Environment.SetEnvironmentVariable(TokenVariable, "test-token");
        try
        {
            // The thinking of the first answer, written by the same provider and model: it is replayed as it came.
            var request = CreateCopilotRequest([
                User("First."),
                new AgentConversationMessage(AgentConversationRole.Assistant, [
                    new AgentMessagePart.Reasoning("plan the change", "sig-old", new AgentReasoningProvenance("anthropic-messages", "github-copilot", AgentTransportKind.AnthropicMessages, "claude-haiku-5.5")),
                    new AgentMessagePart.Text("First answer."),
                ]),
                User("Second."),
            ]);
            var warnings = new List<AgentTurnSessionUpdate>();
            var response = await executor.ExecuteTurnAsync(
                request,
                static (_, _) => ValueTask.CompletedTask,
                (update, _) =>
                {
                    warnings.Add(update);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            Assert.AreEqual(2, bodies.Count, "the refused request is asked once more");
            CollectionAssert.AreEqual(new[] { ("thinking", (string?)"sig-old"), ("text", null) }, Blocks(bodies[0], 1));
            var retried = Blocks(bodies[1], 1);
            CollectionAssert.AreEqual(new[] { "text", "text" }, retried.Select(static block => block.Type).ToArray());
            StringAssert.Contains(Texts(bodies[1], 1)[0], "plan the change");
            StringAssert.Contains(warnings.Single(static update => update.Kind == AgentSessionUpdateKind.Warning).Message, "thinking");
            Assert.AreEqual("sig-new", response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single().ProtectedData);

            // The next request of the session, through the executor Copilot creates for it: the refused thinking goes
            // as text at once, the thinking written since keeps its signature.
            var next = request with { Conversation = [.. request.Conversation, response.AssistantMessage, User("Third.")] };
            _ = await executor.ExecuteTurnAsync(next, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

            Assert.AreEqual(3, bodies.Count, "the next request is not refused first");
            CollectionAssert.AreEqual(new[] { "text", "text" }, Blocks(bodies[2], 1).Select(static block => block.Type).ToArray());
            CollectionAssert.AreEqual(new[] { ("thinking", (string?)"sig-new"), ("text", null) }, Blocks(bodies[2], 3));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [TestMethod]
    public async Task AnotherBadRequest_IsNotAskedAgain()
    {
        var requests = 0;
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "gateway",
            ApiKey = "test-key",
            BaseUri = new Uri("https://gateway.test"),
            HttpClient = new HttpClient(new StubHandler(_ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: 900000 > 64000, which is the maximum"}}""", Encoding.UTF8, "application/json"),
                };
            })),
        });
        var request = AnthropicStopReasonTests.CreateRequest() with
        {
            Provider = AnthropicStopReasonTests.CreateRequest().Provider with { ProviderKey = "gateway", BaseUri = new Uri("https://gateway.test") },
        };
        request = request with
        {
            Conversation = [
                User("First."),
                new AgentConversationMessage(AgentConversationRole.Assistant, [
                    new AgentMessagePart.Reasoning("plan", "sig-old", AgentReasoningReplay.CreateProvenance(request)),
                    new AgentMessagePart.Text("Done."),
                ]),
                User("Next."),
            ],
        };

        await Assert.ThrowsAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public void TheRefusal_IsRecognizedInTheBodyOrInAnErrorOfTheStream()
    {
        Assert.IsTrue(AnthropicModelProviderRuntime.IsRefusedThinkingSignature(new InvalidOperationException("messages.5.content.0: Invalid `signature` in `thinking` block")));
        Assert.IsTrue(AnthropicModelProviderRuntime.IsRefusedThinkingSignature(new InvalidOperationException("outer", new InvalidOperationException("Invalid signature in thinking block"))));
        Assert.IsFalse(AnthropicModelProviderRuntime.IsRefusedThinkingSignature(new InvalidOperationException("messages.1.content.0.thinking.signature: Field required")));
        Assert.IsFalse(AnthropicModelProviderRuntime.IsRefusedThinkingSignature(new InvalidOperationException("max_tokens: invalid value")));
    }

    private static AgentTurnRequest CreateCopilotRequest(IReadOnlyList<AgentConversationMessage> conversation)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                ProviderKey = "github-copilot",
                DisplayName = "Copilot",
                TransportKind = AgentTransportKind.AnthropicMessages,
                BaseUri = new Uri("https://api.individual.githubcopilot.com"),
            },
            ProviderId = new ModelProviderId("github-copilot"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = "claude-haiku-5.5",
            ModelInfo = new AgentModelInfo(
                "claude-haiku-5.5",
                Provider: "github-copilot",
                Capabilities: new Dictionary<string, object?>
                {
                    ["copilotEndpointKind"] = "AnthropicMessages",
                    ["family"] = "claude",
                    ["outputTokenLimit"] = 32000L,
                }),
            Conversation = conversation,
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-test",
                ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                ProviderKey = "github-copilot",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private static AgentConversationMessage User(string text)
        => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static HttpResponseMessage Ok(string stream)
        => new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };

    private static string Thinking(int index, string text, string signature)
        => $$$"""
             {"type":"content_block_start","index":{{{index}}},"content_block":{"type":"thinking","thinking":"","signature":""}}
             {"type":"content_block_delta","index":{{{index}}},"delta":{"type":"thinking_delta","thinking":"{{{text}}}"}}
             {"type":"content_block_delta","index":{{{index}}},"delta":{"type":"signature_delta","signature":"{{{signature}}}"}}
             {"type":"content_block_stop","index":{{{index}}}}
             """;

    // The type and the signature of each block of a message of a request.
    private static (string? Type, string? Signature)[] Blocks(string body, int message)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages")[message].GetProperty("content").EnumerateArray()
            .Select(static block => (block.GetProperty("type").GetString(), block.TryGetProperty("signature", out var signature) ? signature.GetString() : null))
            .ToArray();
    }

    private static string[] Texts(string body, int message)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages")[message].GetProperty("content").EnumerateArray()
            .Where(static block => block.GetProperty("type").GetString() == "text")
            .Select(static block => block.GetProperty("text").GetString() ?? string.Empty)
            .ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
