using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// How the result of a tool call that failed goes out to the Messages API.
[TestClass]
public sealed class AnthropicToolResultErrorTests
{
    [TestMethod]
    public async Task AFailedToolResult_GoesOutWithIsErrorTrue_AndKeepsItsText()
    {
        var bodies = new List<string>();
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new CapturingHandler(bodies, AnthropicStopReasonTests.Stream(
                AnthropicStopReasonTests.TextBlock(0, "Done."),
                """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}"""))),
        });
        var failure = "Tool 'read_file' failed: File not found: missing.txt";
        var request = AnthropicStopReasonTests.CreateRequest() with
        {
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Read two files.")]),
                new AgentConversationMessage(AgentConversationRole.Assistant,
                [
                    new AgentMessagePart.ToolCall("toolu_failed", "read_file", JsonDocument.Parse("""{"path":"missing.txt"}""").RootElement.Clone()),
                    new AgentMessagePart.ToolCall("toolu_ok", "read_file", JsonDocument.Parse("""{"path":"present.txt"}""").RootElement.Clone()),
                ]),
                new AgentConversationMessage(AgentConversationRole.Tool,
                [
                    new AgentMessagePart.ToolResult("toolu_failed", new AgentToolResult(false, [new AgentToolResultItem.Text(failure)], failure)),
                    new AgentMessagePart.ToolResult("toolu_ok", new AgentToolResult(true, [new AgentToolResultItem.Text("hello")])),
                ]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(1, bodies.Count);
        using var body = JsonDocument.Parse(bodies[0]);
        var results = body.RootElement.GetProperty("messages").EnumerateArray()
            .SelectMany(static message => message.GetProperty("content").ValueKind == JsonValueKind.Array
                ? message.GetProperty("content").EnumerateArray().ToArray()
                : [])
            .Where(static block => block.GetProperty("type").GetString() == "tool_result")
            .ToDictionary(static block => block.GetProperty("tool_use_id").GetString()!);
        Assert.IsTrue(results["toolu_failed"].GetProperty("is_error").GetBoolean(), results["toolu_failed"].GetRawText());
        StringAssert.Contains(results["toolu_failed"].GetRawText(), "File not found: missing.txt");
        Assert.IsFalse(results["toolu_ok"].TryGetProperty("is_error", out var isError) && isError.GetBoolean(), results["toolu_ok"].GetRawText());
    }

    private sealed class CapturingHandler(List<string> bodies, string stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
