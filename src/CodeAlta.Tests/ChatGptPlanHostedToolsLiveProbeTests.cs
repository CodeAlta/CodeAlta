using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Opt-in probes of the hosted tools the ChatGPT-subscription endpoint takes, with <c>CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT</c>
/// (the state folder whose <c>auth/openai-codex-subscription/codex.credential</c> is signed in, such as <c>~/.alta</c>)
/// and <c>CODEALTA_TEST_CODEX_MODEL</c> (a model of the subscription, speaking Responses Lite). Each request costs a
/// little of the subscription.
/// </summary>
[TestClass]
public sealed class ChatGptPlanHostedToolsLiveProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Live_TheSubscriptionTakesWebSearch_AsTheNormalizerWritesIt()
    {
        var result = await PostAsync(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString(Request("""{"type":"web_search"}""")), isHttp: true));
        if (result is null) return;

        Assert.AreEqual(200, result.Value.Status, result.Value.Text);
        Assert.IsFalse(result.Value.Failed, result.Value.Text);
    }

    [TestMethod]
    public async Task Live_TheExecutorSearchesTheWeb_ShowsTheSearch_AndReplaysTheAnswerThatCitesIt()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT is not set.");
            return;
        }

        // Every model of the subscription speaks Responses Lite (October 2026): the local tools go in the input,
        // and the search must stay a top-level tool for the model to use it.
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            StateRootPath = root,
            CodexSubscription = new OpenAICodexSubscriptionOptions { Experimental = true, ResponseTransport = "http", WebSearch = OpenAICodexWebSearchMode.Live },
        });
        var question = new AgentConversationMessage(AgentConversationRole.User,
            [new AgentMessagePart.Text("Search the web: what is the most recent version of the NuGet package Tomlyn? Answer in one sentence and cite the source.")]);
        var first = await executor.ExecuteTurnAsync(TurnRequest([question]), static (_, _) => ValueTask.CompletedTask, TestContext.CancellationToken);
        var answer = string.Concat(first.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
        TestContext.WriteLine($"answer: {answer}");
        foreach (var activity in first.ProviderActivities ?? [])
        {
            TestContext.WriteLine($"activity: {activity.Kind} {activity.Phase} {activity.ActivityId} {activity.Message}");
        }

        var search = first.ProviderActivities?.FirstOrDefault(static activity => activity.Kind == AgentActivityKind.WebSearch);
        Assert.IsNotNull(search, "the model searched the web");
        Assert.AreEqual(AgentActivityPhase.Completed, search.Phase);
        Assert.IsFalse(string.IsNullOrWhiteSpace(search.Message), "the search says what it looked for");
        StringAssert.Contains(answer, "](http", "the answer cites its source as a link");

        // The next turn replays the answer, without the search, and the endpoint takes it.
        var second = await executor.ExecuteTurnAsync(
            TurnRequest([question, first.AssistantMessage, new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Which site did you cite? Answer with its domain only, without searching again.")])]),
            static (_, _) => ValueTask.CompletedTask,
            TestContext.CancellationToken);
        var domain = string.Concat(second.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
        TestContext.WriteLine($"second: {domain}");
        StringAssert.Contains(domain.ToLowerInvariant(), "nuget");
    }

    private static AgentTurnRequest TurnRequest(IReadOnlyList<AgentConversationMessage> conversation)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
                TransportKind = AgentTransportKind.OpenAIResponses,
            },
            ProviderId = new ModelProviderId("openai-responses"),
            SessionId = "live-web-search",
            RunId = new AgentRunId("run-1"),
            ModelId = Model,
            ModelInfo = new AgentModelInfo(Model, Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["useResponsesLite"] = true,
                ["supportsReasoningSummaries"] = true,
            }),
            SystemMessage = "You are a test agent.",
            Conversation = conversation,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("echo", "Echoes a text.", JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("echo")]))),
            ],
            State = new AgentSessionState { SessionId = "live-web-search", ProtocolFamily = "codex", ProviderKey = "codex", UpdatedAt = DateTimeOffset.UtcNow },
        };

    [TestMethod]
    public async Task Live_TheSubscriptionRefusesProgrammaticToolCalling()
    {
        // Sent as the Responses API documents it: the hosted tool at the top level, the function allowed to the program.
        var tools = JsonNode.Parse(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString(Request(null)), isHttp: true).ToString())!.AsObject();
        tools["tools"]![0]!["tools"]![0]!["allowed_callers"] = new JsonArray { "direct", "programmatic" };
        tools["tools"]!.AsArray().Add(new JsonObject { ["type"] = "programmatic_tool_calling" });
        var result = await PostAsync(BinaryData.FromString(tools.ToJsonString()));
        if (result is null) return;

        Assert.AreEqual(400, result.Value.Status, "the subscription takes programmatic_tool_calling now: ChatGptPlanRequestNormalizer can let it through");
        StringAssert.Contains(result.Value.Text, "Unsupported tool type: programmatic_tool_calling");
    }

    [TestMethod]
    public async Task Live_TheSubscriptionTakesTheHostedImageGenerationTool()
    {
        // ChatGptPlanRequestNormalizer refuses image_generation without a probe of its own: this is that probe. The tool
        // is sent as Codex sent it to its backend until it moved to the images endpoint (see the next probe): at the
        // top level beside the namespace of the local tools. The events are written with their long strings (the base64
        // of the image) cut, to show the shape of the item.
        var body = JsonNode.Parse(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString(Request("""{"type":"web_search"}""")), isHttp: true).ToString())!.AsObject();
        body["tools"]![1] = new JsonObject { ["type"] = "image_generation", ["output_format"] = "png" };
        body["input"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Generate a small, simple image of a red circle on a white background, then answer with the single word: done" } };
        var result = await PostAsync(BinaryData.FromString(body.ToJsonString()));
        if (result is null) return;

        foreach (var line in result.Value.Text.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            try
            {
                var data = JsonNode.Parse(line[6..])!;
                Strip(data);
                TestContext.WriteLine(data.ToJsonString());
            }
            catch (JsonException)
            {
                TestContext.WriteLine(line[..Math.Min(300, line.Length)]);
            }
        }

        Assert.AreEqual(200, result.Value.Status, result.Value.Text);
        Assert.IsFalse(result.Value.Failed, result.Value.Text[..Math.Min(2000, result.Value.Text.Length)]);
        StringAssert.Contains(result.Value.Text, "\"image_generation_call\"", "the model generated the image it was asked for");

        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(static pair => pair.Key).ToArray())
                    {
                        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 200)
                        {
                            obj[key] = $"<{text.Length} chars>";
                        }
                        else
                        {
                            Strip(obj[key]);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array) Strip(item);
                    break;
            }
        }
    }

    [TestMethod]
    public async Task Live_TheSubscriptionTakesTheImagesApi_AsCodexCallsIt()
    {
        // Codex no longer sends the hosted tool: its own image tool posts this to the images endpoint of its provider
        // (codex-rs/ext/image-generation/src/tool.rs). Here, the endpoint of a ChatGPT token-sharing sign-in.
        var body = """{"prompt":"A small red circle on a white background.","background":"opaque","model":"gpt-image-2","quality":"auto","size":"auto"}""";
        var result = await PostAsync(BinaryData.FromString(body), "images/generations");
        if (result is null) return;

        Assert.AreEqual(200, result.Value.Status, result.Value.Text[..Math.Min(2000, result.Value.Text.Length)]);
        using var document = JsonDocument.Parse(result.Value.Text);
        Assert.IsTrue(document.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()!.Length > 0, "the endpoint gave the image");
    }

    private static string Request(string? hostedTool)
        => $$$"""
            {"model":"{{{Model}}}", "instructions":"You are a test agent.",
             "input":[{"role":"user", "content":"Answer with the single word: ok"}],
             "tools":[{"type":"function", "name":"echo", "description":"Echoes a text.", "strict":true,
               "parameters":{"type":"object", "properties":{"text":{"type":"string"}}, "required":["text"], "additionalProperties":false}}{{{(hostedTool is null ? "" : ", " + hostedTool)}}}]}
            """;

    private static string Model => Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_MODEL") is { Length: > 0 } named ? named : "gpt-6-luna";

    private async Task<(int Status, bool Failed, string Text)?> PostAsync(BinaryData body, string endpoint = "responses")
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT is not set.");
            return null;
        }

        using var http = new HttpClient();
        var auth = new OpenAICodexSubscriptionAuthManager(new FileOpenAICodexSubscriptionCredentialStore(root), new OpenAICodexSubscriptionOAuthClient(http), "codex");
        var identity = await CodexSubscriptionHttpRequestFactory.CreateIdentityAsync(auth, new OpenAICodexSubscriptionOptions(), TestContext.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexSubscriptionHttpRequestFactory.ResolveEndpoint(null, endpoint))
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
        };
        CodexSubscriptionHttpRequestFactory.ApplyIdentity(request, identity);
        using var response = await http.SendAsync(request, TestContext.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        // A refusal after the start of the stream comes as an event of the stream.
        var failed = text.Contains("\"response.failed\"", StringComparison.Ordinal) || text.Contains("\"type\":\"error\"", StringComparison.Ordinal);
        TestContext.WriteLine($"{(int)response.StatusCode} failed={failed} {text.ReplaceLineEndings(" ")[..Math.Min(400, text.Length)]}");
        return ((int)response.StatusCode, failed, text);
    }
}
