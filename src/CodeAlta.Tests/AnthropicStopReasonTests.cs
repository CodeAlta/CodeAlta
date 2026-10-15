using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// How a response of the Messages API that stops for another reason than the end of the turn ends the turn.
[TestClass]
public sealed class AnthropicStopReasonTests
{
    [TestMethod]
    public async Task Refusal_FailsTheTurnWithItsCategory_AndKeepsNoPartialAnswer()
    {
        // The model began to answer, then a safety classifier stopped it.
        var stream = Stream(
            TextBlock(0, "Here is how the exploit"),
            """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null,"stop_details":{"type":"refusal","category":"cyber","explanation":"This request may enable cyber harm."}},"usage":{"output_tokens":5}}""");
        var executor = CreateExecutor(stream);

        var failure = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
            () => executor.ExecuteTurnAsync(CreateRequest(), static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        StringAssert.Contains(failure.Message, "declined");
        StringAssert.Contains(failure.Message, "cyber");
        StringAssert.Contains(failure.Message, "This request may enable cyber harm.");
        Assert.IsFalse(failure.Failure.IsContextOverflow);
        // A refusal is the model declining: neither a quota nor an outage, so no fallback to another model.
        Assert.AreEqual(AgentFailureKind.Other, failure.Failure.Failure?.Kind);
    }

    [TestMethod]
    public async Task Refusal_WithoutDetails_StillFailsTheTurn()
    {
        var stream = Stream("""{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":0}}""");
        var executor = CreateExecutor(stream);

        var failure = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
            () => executor.ExecuteTurnAsync(CreateRequest(), static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        StringAssert.Contains(failure.Message, "declined");
        Assert.AreEqual(AgentFailureKind.Other, failure.Failure.Failure?.Kind);
    }

    internal static string TextBlock(int index, string text)
        => $$$"""
             {"type":"content_block_start","index":{{{index}}},"content_block":{"type":"text","text":""}}
             {"type":"content_block_delta","index":{{{index}}},"delta":{"type":"text_delta","text":"{{{text}}}"}}
             {"type":"content_block_stop","index":{{{index}}}}
             """;

    // A stream of the Messages API: message_start, the events given (one JSON object per line), message_stop.
    internal static string Stream(params string[] events)
    {
        var builder = new StringBuilder();
        Append("""{"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}""");
        foreach (var line in events.SelectMany(static value => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            Append(line);
        }

        Append("""{"type":"message_stop"}""");
        return builder.ToString();

        void Append(string data)
        {
            var type = System.Text.Json.JsonDocument.Parse(data).RootElement.GetProperty("type").GetString();
            builder.Append("event: ").Append(type).Append('\n').Append("data: ").Append(data).Append("\n\n");
        }
    }

    internal static IModelProviderTurnExecutor CreateExecutor(string stream)
        => AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") })),
        });

    internal static AgentTurnRequest CreateRequest(IReadOnlyList<AgentToolDefinition>? tools = null)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                DisplayName = "Anthropic",
                TransportKind = AgentTransportKind.AnthropicMessages,
                BaseUri = new Uri("https://api.anthropic.test"),
            },
            ProviderId = new ModelProviderId("anthropic"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = "claude-sonnet-5-5",
            ModelInfo = new AgentModelInfo("claude-sonnet-5-5"),
            MaxOutputTokens = 4096,
            SystemMessage = "You are a test agent.",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Write the tool.")])],
            Tools = tools ?? [],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
