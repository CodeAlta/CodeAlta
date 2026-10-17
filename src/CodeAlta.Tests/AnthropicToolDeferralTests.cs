using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AnthropicToolDeferralTests
{
    [TestMethod]
    public async Task ClaudeApi_SendsDeferredToolsWithDeferLoading_AndTheCacheMarkerOnTheLastLoadedTool()
    {
        using var body = await SendAsync(new Uri("https://api.anthropic.com"), "claude-sonnet-4-5");
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().ToDictionary(static tool => tool.GetProperty("name").GetString()!);

        Assert.IsTrue(tools["mcp__slack__post"].GetProperty("defer_loading").GetBoolean());
        Assert.IsFalse(tools["mcp__slack__post"].TryGetProperty("cache_control", out _), "A deferred tool takes no marker: the API refuses it.");
        Assert.IsFalse(tools["read_file"].TryGetProperty("defer_loading", out _));
        Assert.IsFalse(tools[AgentToolSearch.ToolName].TryGetProperty("defer_loading", out _), "The search tool is never deferred.");
        Assert.IsTrue(tools[AgentToolSearch.ToolName].TryGetProperty("cache_control", out _), "The marker is on the last tool that is loaded.");
        Assert.IsFalse(tools["read_file"].TryGetProperty("cache_control", out _));
    }

    [TestMethod]
    public async Task ClaudeApi_SendsTheLoadsOfAResultAsToolReferenceBlocks_AndAToolThatIsGoneAsText()
    {
        using var body = await SendAsync(new Uri("https://api.anthropic.com"), "claude-opus-5-5");
        var results = body.RootElement.GetProperty("messages").EnumerateArray()
            .SelectMany(static message => message.GetProperty("content").ValueKind == JsonValueKind.Array ? message.GetProperty("content").EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(static block => block.GetProperty("type").GetString() == "tool_result")
            .ToArray();

        var search = results.Single(static result => result.GetProperty("tool_use_id").GetString() == "call-search");
        var content = search.GetProperty("content").EnumerateArray().ToArray();
        Assert.HasCount(1, content, "A result that loads tools carries only their references.");
        Assert.AreEqual("tool_reference", content[0].GetProperty("type").GetString());
        Assert.AreEqual("mcp__slack__post", content[0].GetProperty("tool_name").GetString());

        var gone = results.Single(static result => result.GetProperty("tool_use_id").GetString() == "call-gone");
        var text = gone.GetProperty("content").EnumerateArray().Select(static block => block.GetProperty("text").GetString()).ToArray();
        CollectionAssert.Contains(text, "Loaded tool: mcp__gone__x (no longer available)");
        Assert.DoesNotContain("", body.RootElement.GetRawText());
    }

    [TestMethod]
    public async Task AnotherEndpoint_OrAnOlderModel_DefersNothing()
    {
        foreach (var (uri, model) in new[] { (new Uri("https://gateway.example/anthropic"), "claude-sonnet-4-5"), (new Uri("https://api.anthropic.com"), "claude-3-7-sonnet-latest") })
        {
            using var body = await SendAsync(uri, model);
            Assert.IsFalse(body.RootElement.GetProperty("tools").EnumerateArray().Any(static tool => tool.TryGetProperty("defer_loading", out _)), uri + " " + model);
        }

        // A profile can turn it on for an endpoint that takes it.
        var executor = (IModelProviderToolDeferral)AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions { ProviderKey = "gateway", BaseUri = new Uri("https://gateway.example") });
        var request = Request("claude-sonnet-4-5") with
        {
            Provider = Request("claude-sonnet-4-5").Provider with { Profile = new AgentProviderProfile { SupportsToolDeferral = true } },
        };
        Assert.IsTrue(executor.DefersTools(request));
    }

    [TestMethod]
    public void RewriteToolReferences_LeavesABodyWithoutReferencesAsItIs()
        => Assert.IsNull(AnthropicToolDeferral.RewriteToolReferences("""{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}],"tools":[]}"""));

    // Opt in with an API key in CODEALTA_TEST_ANTHROPIC_API_KEY (and a model in CODEALTA_TEST_ANTHROPIC_MODEL): one request of
    // a few hundred tokens, which the account pays.
    [TestMethod]
    public async Task Live_TheClaudeApiTakesDeferredToolsAndToolReferences()
    {
        var apiKey = Environment.GetEnvironmentVariable("CODEALTA_TEST_ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Inconclusive("Set CODEALTA_TEST_ANTHROPIC_API_KEY to send one request to the Claude API.");
        }

        var model = Environment.GetEnvironmentVariable("CODEALTA_TEST_ANTHROPIC_MODEL") is { Length: > 0 } named ? named : "claude-haiku-4-5";
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions { ProviderKey = "anthropic", ApiKey = apiKey });
        var request = Request(model) with
        {
            MaxOutputTokens = 1024,
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Post 'build is green' on the #dev channel of Slack.")]),
                new AgentConversationMessage(AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall("toolu_search", AgentToolSearch.ToolName, JsonSerializer.SerializeToElement(new { query = "post a message on slack" }))]),
                new AgentConversationMessage(AgentConversationRole.Tool,
                    [new AgentMessagePart.ToolResult("toolu_search", new AgentToolResult(true, [new AgentToolResultItem.Text("Loaded: mcp__slack__post"), new AgentToolResultItem.ToolReference("mcp__slack__post")]))]),
            ],
        };

        var response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask);

        var call = response.AssistantMessage!.Parts.OfType<AgentMessagePart.ToolCall>().FirstOrDefault();
        Assert.IsNotNull(call, "The model calls the tool the search loaded.");
        Assert.AreEqual("mcp__slack__post", call.Name);
    }

    private static async Task<JsonDocument> SendAsync(Uri baseUri, string model)
    {
        string? body = null;
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
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(stream, System.Text.Encoding.UTF8, "text/event-stream") };
            })),
        });
        var request = Request(model) with { Provider = Request(model).Provider with { BaseUri = baseUri } };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask);

        return JsonDocument.Parse(body!);
    }

    private static AgentTurnRequest Request(string model)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                DisplayName = "Anthropic",
                TransportKind = AgentTransportKind.AnthropicMessages,
            },
            ProviderId = new ModelProviderId("anthropic"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = model,
            ModelInfo = new AgentModelInfo(model),
            MaxOutputTokens = 4096,
            SystemMessage = "You are a test agent.",
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Post on slack.")]),
                new AgentConversationMessage(AgentConversationRole.Assistant,
                [
                    new AgentMessagePart.ToolCall("call-search", AgentToolSearch.ToolName, JsonSerializer.SerializeToElement(new { query = "slack" })),
                    new AgentMessagePart.ToolCall("call-gone", AgentToolSearch.ToolName, JsonSerializer.SerializeToElement(new { query = "gone" })),
                ]),
                new AgentConversationMessage(AgentConversationRole.Tool,
                [
                    new AgentMessagePart.ToolResult("call-search", new AgentToolResult(true, [new AgentToolResultItem.Text("Loaded: mcp__slack__post"), new AgentToolResultItem.ToolReference("mcp__slack__post")])),
                    new AgentMessagePart.ToolResult("call-gone", new AgentToolResult(true, [new AgentToolResultItem.Text("Loaded: mcp__gone__x"), new AgentToolResultItem.ToolReference("mcp__gone__x")])),
                ]),
                new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Loaded.")]),
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Go on.")]),
            ],
            Tools =
            [
                AgentToolDeferralTests.Tool("read_file"),
                AgentToolSearch.Create(static (_, _) => ValueTask.FromResult<IReadOnlyList<AgentToolSearchHit>>([])),
                AgentToolDeferralTests.Tool("mcp__slack__post", deferred: true, "Posts a message to a Slack channel."),
            ],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
