using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// The blocks of thinking of a response go back to the API as they came: each block with its own signature, in the
// order of the response, and a redacted block as redacted_thinking.
[TestClass]
public sealed class AnthropicThinkingReplayTests
{
    private const string End = """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":3}}""";

    [TestMethod]
    public async Task TwoBlocksOfThinking_StayTwoParts_EachWithItsSignature_AndAreReplayedSo()
    {
        var first = AnthropicStopReasonTests.Stream(
            Thinking(0, "plan the change", "sig-a"),
            Thinking(1, "check the tests", "sig-b"),
            AnthropicStopReasonTests.TextBlock(2, "Done."),
            End);
        var (response, replayed) = await RoundTripAsync(first).ConfigureAwait(false);

        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().ToArray();
        Assert.AreEqual(2, reasoning.Length);
        Assert.AreEqual(("plan the change", "sig-a"), (reasoning[0].Value, reasoning[0].ProtectedData));
        Assert.AreEqual(("check the tests", "sig-b"), (reasoning[1].Value, reasoning[1].ProtectedData));

        var blocks = replayed.Select(static block => (block.GetProperty("type").GetString(), block.TryGetProperty("signature", out var signature) ? signature.GetString() : null)).ToArray();
        CollectionAssert.AreEqual(new[] { ("thinking", (string?)"sig-a"), ("thinking", "sig-b"), ("text", null) }, blocks);
    }

    [TestMethod]
    public async Task TextBeforeThinking_KeepsItsPlace()
    {
        var first = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "Let me look."),
            Thinking(1, "the file is small", "sig-a"),
            AnthropicStopReasonTests.TextBlock(2, "Done."),
            End);
        var (response, replayed) = await RoundTripAsync(first).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { typeof(AgentMessagePart.Text), typeof(AgentMessagePart.Reasoning), typeof(AgentMessagePart.Text) },
            response.AssistantMessage.Parts.Select(static part => part.GetType()).ToArray());
        CollectionAssert.AreEqual(new[] { "text", "thinking", "text" }, replayed.Select(static block => block.GetProperty("type").GetString()).ToArray());
    }

    [TestMethod]
    public async Task ARedactedBlock_IsReplayedAsRedactedThinking_AfterTheJournal()
    {
        var first = AnthropicStopReasonTests.Stream(
            """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"encrypted-payload"}}""",
            """{"type":"content_block_stop","index":0}""",
            AnthropicStopReasonTests.TextBlock(1, "Done."),
            End);
        var (response, replayed) = await RoundTripAsync(first, throughJournal: true).ConfigureAwait(false);

        var redacted = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual(true, redacted.Redacted);
        Assert.AreEqual("redacted_thinking", replayed[0].GetProperty("type").GetString());
        Assert.AreEqual("encrypted-payload", replayed[0].GetProperty("data").GetString());
    }

    [TestMethod]
    public void TheRawTextMark_IsWrittenOnlyWhenSet_AndReadBack()
    {
        var plain = JsonSerializer.Serialize<AgentMessagePart>(new AgentMessagePart.Reasoning("summary"), AgentJsonSerializerContext.Default.AgentMessagePart);
        Assert.IsFalse(plain.Contains("rawText", StringComparison.OrdinalIgnoreCase), plain);

        var text = JsonSerializer.Serialize<AgentMessagePart>(new AgentMessagePart.Reasoning("thinking") { RawText = true }, AgentJsonSerializerContext.Default.AgentMessagePart);
        var read = JsonSerializer.Deserialize(text, AgentJsonSerializerContext.Default.AgentMessagePart);
        Assert.AreEqual(true, Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(read).RawText);
    }

    [TestMethod]
    public void TheRedactedMark_IsWrittenOnlyWhenSet()
    {
        var plain = JsonSerializer.Serialize<AgentMessagePart>(new AgentMessagePart.Reasoning("text", "sig"), AgentJsonSerializerContext.Default.AgentMessagePart);
        Assert.IsFalse(plain.Contains("redacted", StringComparison.OrdinalIgnoreCase), plain);

        var redacted = JsonSerializer.Serialize<AgentMessagePart>(new AgentMessagePart.Reasoning(string.Empty, "data") { Redacted = true }, AgentJsonSerializerContext.Default.AgentMessagePart);
        var read = JsonSerializer.Deserialize(redacted, AgentJsonSerializerContext.Default.AgentMessagePart);
        Assert.AreEqual(true, Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(read).Redacted);
    }

    private static string Thinking(int index, string text, string signature)
        => $$$"""
             {"type":"content_block_start","index":{{{index}}},"content_block":{"type":"thinking","thinking":"","signature":""}}
             {"type":"content_block_delta","index":{{{index}}},"delta":{"type":"thinking_delta","thinking":"{{{text}}}"}}
             {"type":"content_block_delta","index":{{{index}}},"delta":{"type":"signature_delta","signature":"{{{signature}}}"}}
             {"type":"content_block_stop","index":{{{index}}}}
             """;

    // Runs a turn on the stream, then a second turn that replays its answer; returns the answer and the content of the
    // assistant message the second request sent.
    private static async Task<(AgentTurnResponse Response, JsonElement[] Replayed)> RoundTripAsync(string stream, bool throughJournal = false)
    {
        var bodies = new List<string>();
        var streams = new Queue<string>([stream, AnthropicStopReasonTests.Stream(AnthropicStopReasonTests.TextBlock(0, "ok"), End)]);
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streams.Dequeue(), Encoding.UTF8, "text/event-stream") };
            })),
        });
        var request = AnthropicStopReasonTests.CreateRequest() with { ModelId = "claude-opus-4-5", ModelInfo = new AgentModelInfo("claude-opus-4-5") };
        var response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var answer = response.AssistantMessage;
        if (throughJournal)
        {
            // What a session reads back from its journal.
            answer = JsonSerializer.Deserialize(JsonSerializer.Serialize(answer, AgentJsonSerializerContext.Default.AgentConversationMessage), AgentJsonSerializerContext.Default.AgentConversationMessage)!;
        }

        var next = request with
        {
            Conversation = [.. request.Conversation, answer, new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Next.")])],
        };
        _ = await executor.ExecuteTurnAsync(next, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var body = JsonDocument.Parse(bodies[1]);
        var replayed = body.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray().Select(static block => block.Clone()).ToArray();
        return (response, replayed);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
