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

    [TestMethod]
    public async Task AToolCallCutByMaxTokens_DoesNotRun_AndTheModelIsToldWhy()
    {
        // The output limit cut the arguments of a large note: the JSON never closes.
        var cut = Stream(
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_cut","name":"save_note","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"big.txt\",\"content\":\"line 1\\nline 2"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"output_tokens":4096}}""");
        var done = Stream(TextBlock(0, "I will write it in smaller parts."),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":8}}""");
        var bodies = new List<string>();
        var streams = new Queue<string>([cut, done]);
        var calls = 0;
        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
        {
            StateRootPath = Path.GetTempPath(),
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://api.anthropic.test"),
                    HttpClient = new HttpClient(new StubHandler(request =>
                    {
                        if (request.RequestUri!.AbsolutePath == "/v1/models")
                        {
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson, Encoding.UTF8, "application/json") };
                        }

                        bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streams.Dequeue(), Encoding.UTF8, "text/event-stream") };
                    })),
                },
            },
        });
        using var temp = TestTempDirectory.Create();
        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "claude-sonnet-5-5",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("save_note", "Save a note.", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                    (_, _) =>
                    {
                        calls++;
                        return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("written")]));
                    }),
            ],
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Write big.txt.") }).ConfigureAwait(false);

        Assert.AreEqual(0, calls, "A call whose arguments were cut does not run.");
        Assert.AreEqual(2, bodies.Count);
        using var second = System.Text.Json.JsonDocument.Parse(bodies[1]);
        var result = second.RootElement.GetProperty("messages").EnumerateArray()
            .SelectMany(static message => message.GetProperty("content").ValueKind == System.Text.Json.JsonValueKind.Array
                ? message.GetProperty("content").EnumerateArray().ToArray()
                : [])
            .Single(static block => block.GetProperty("type").GetString() == "tool_result");
        Assert.AreEqual("toolu_cut", result.GetProperty("tool_use_id").GetString());
        StringAssert.Contains(result.GetRawText(), "max_tokens");
    }

    private const string ModelsJson =
        """
        {
          "data": [
            {
              "type": "model", "id": "claude-sonnet-5-5", "display_name": "Claude Sonnet 5.5", "created_at": "2026-09-01T00:00:00Z",
              "max_input_tokens": 1000000, "max_tokens": 4096
            }
          ],
          "has_more": false, "first_id": "claude-sonnet-5-5", "last_id": "claude-sonnet-5-5"
        }
        """;

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
