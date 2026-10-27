using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

// A session keeps each tool result in a message of its own; Anthropic wants the results of one turn in one user message.
[TestClass]
public sealed class AnthropicParallelToolResultsTests
{
    [TestMethod]
    public async Task TheResultsOfParallelCalls_GoOutInOneUserMessage_WithTheCacheMarkerOnTheLast()
    {
        var bodies = await SendAsync(CreateFirstTurn()).ConfigureAwait(false);

        var messages = bodies.Single()["messages"]!.AsArray();
        Assert.AreEqual(3, messages.Count, messages.ToJsonString());
        Assert.AreEqual("user", (string?)messages[2]!["role"]);
        var blocks = messages[2]!["content"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "toolu_a", "toolu_b" },
            blocks.Select(static block => (string?)block!["tool_use_id"]).ToArray());
        Assert.IsTrue(blocks.All(static block => (string?)block!["type"] == "tool_result"));
        Assert.IsNull(blocks[0]!["cache_control"], "The marker goes on the last block of the merged message.");
        Assert.IsNotNull(blocks[1]!["cache_control"]);
    }

    [TestMethod]
    public async Task TheNextRequest_StartsWithWhatTheLastOneSent()
    {
        var first = CreateFirstTurn();
        var second = first.Concat(
        [
            new AgentConversationMessage(AgentConversationRole.Assistant,
                [new AgentMessagePart.ToolCall("toolu_c", "read_file", JsonDocument.Parse("""{"path":"c.txt"}""").RootElement.Clone())]),
            Result("toolu_c", "c"),
        ]).ToArray();

        var bodies = await SendAsync(first, second).ConfigureAwait(false);

        var sent = Strip(bodies[0]["messages"]!.AsArray());
        var next = Strip(bodies[1]["messages"]!.AsArray());
        Assert.AreEqual(5, next.Count);
        for (var index = 0; index < sent.Count; index++)
        {
            Assert.AreEqual(sent[index]!.ToJsonString(), next[index]!.ToJsonString(), $"Message {index} changed.");
        }
    }

    [TestMethod]
    public void MergeConsecutive_LeavesTheCallersMessages_AndReturnsTheListWhenNothingMerges()
    {
        var first = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "1")]);
        var second = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("b", "2")]);
        var user = new ChatMessage(ChatRole.User, "next");
        var third = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c", "3")]);

        var merged = AnthropicToolResultMessages.MergeConsecutive([first, second, user, third]);

        Assert.AreEqual(3, merged.Count);
        CollectionAssert.AreEqual(
            new[] { "a", "b" },
            merged[0].Contents.OfType<FunctionResultContent>().Select(static result => result.CallId).ToArray());
        Assert.AreSame(user, merged[1]);
        Assert.AreSame(third, merged[2]);
        Assert.AreEqual(1, first.Contents.Count, "The first message of the run is not changed.");

        IReadOnlyList<ChatMessage> alone = [user, third];
        Assert.AreSame(alone, AnthropicToolResultMessages.MergeConsecutive(alone));
    }

    private static AgentConversationMessage[] CreateFirstTurn()
        =>
        [
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Read two files.")]),
            new AgentConversationMessage(AgentConversationRole.Assistant,
            [
                new AgentMessagePart.ToolCall("toolu_a", "read_file", JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone()),
                new AgentMessagePart.ToolCall("toolu_b", "read_file", JsonDocument.Parse("""{"path":"b.txt"}""").RootElement.Clone()),
            ]),
            Result("toolu_a", "a"),
            Result("toolu_b", "b"),
        ];

    private static AgentConversationMessage Result(string callId, string text)
        => new(AgentConversationRole.Tool,
            [new AgentMessagePart.ToolResult(callId, new AgentToolResult(true, [new AgentToolResultItem.Text(text)]))]);

    // The messages without their cache markers, which move from one request to the next.
    private static JsonArray Strip(JsonArray messages)
    {
        var copy = messages.DeepClone().AsArray();
        foreach (var message in copy)
        {
            if (message!["content"] is JsonArray blocks)
            {
                foreach (var block in blocks)
                {
                    block!.AsObject().Remove("cache_control");
                }
            }
        }

        return copy;
    }

    private static async Task<List<JsonNode>> SendAsync(params AgentConversationMessage[][] conversations)
    {
        var bodies = new List<JsonNode>();
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "Done."),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new CapturingHandler(bodies, stream)),
        });
        foreach (var conversation in conversations)
        {
            var request = AnthropicStopReasonTests.CreateRequest() with { Conversation = conversation };
            _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);
        }

        return bodies;
    }

    private sealed class CapturingHandler(List<JsonNode> bodies, string stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
