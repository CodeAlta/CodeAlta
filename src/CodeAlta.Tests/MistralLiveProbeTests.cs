using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Opt-in probes of the Mistral API with <c>CODEALTA_TEST_MISTRAL_API_KEY</c> (and <c>CODEALTA_TEST_MISTRAL_MODEL</c>,
/// <c>mistral-small-latest</c> by default): a tool round with reasoning through the chat completions of the Mistral
/// provider and through the Responses executor. Each costs a fraction of a cent.
/// </summary>
[TestClass]
public sealed class MistralLiveProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(null)]
    [DataRow("mistral-large-4")]
    [DataRow("zai-glm-5-3")]
    [DataRow("glm-5-2")]
    [DataRow("deepseek-v4-1-flash-1017")]
    public async Task Live_ChatCompletions_ToolRoundWithReasoning(string? named)
    {
        if (!TryGetKey(out var key, out var model))
        {
            return;
        }

        model = named ?? model;

        using var http = new HttpClient(new LoggingHandler(TestContext));
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = key,
            HttpClient = http,
        });
        var descriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "mistral-chat",
            ProviderKey = "mistral",
            DisplayName = "Mistral",
            TransportKind = AgentTransportKind.MistralChat,
            Profile = new AgentProviderProfile { SupportsDeveloperRole = true, SupportsStore = false, StreamsUsage = true, MaxTokensFieldName = "max_tokens" },
        };

        await RunToolRoundAsync(executor, descriptor, model);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("mistral-large-4")]
    [DataRow("zai-glm-5-3")]
    [DataRow("glm-5-2")]
    [DataRow("deepseek-v4-1-flash-1017")]
    public async Task Live_ResponsesExecutor_ToolRoundWithReasoning(string? named)
    {
        if (!TryGetKey(out var key, out var model))
        {
            return;
        }

        model = named ?? model;

        using var http = new HttpClient(new LoggingHandler(TestContext));
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = key,
            Api = MistralApi.Responses,
            HttpClient = http,
        });
        var descriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "mistral-responses",
            ProviderKey = "mistral",
            DisplayName = "Mistral",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        await RunToolRoundAsync(executor, descriptor, model);
    }

    [TestMethod]
    public async Task Live_TheModelsHaveTheEffortsTheyTake()
    {
        if (!TryGetKey(out var key, out _))
        {
            return;
        }

        using var http = new HttpClient();
        await using var runtime = new MistralModelProviderRuntime(new MistralModelProviderRuntimeOptions
        {
            Providers = { new MistralProviderOptions { ProviderKey = "mistral", ApiKey = key, HttpClient = http } },
        });
        var models = await runtime.ModelCatalog!.ListModelsAsync(runtime.RuntimeDescriptor, TestContext.CancellationToken);
        foreach (var model in models.Where(static model => model.SupportedReasoningEfforts is { Count: > 0 }))
        {
            TestContext.WriteLine($"{model.Id}: {string.Join(",", model.SupportedReasoningEfforts!)} default={model.DefaultReasoningEffort}");
        }

        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.None, AgentReasoningEffort.High },
            models.Single(static model => model.Id == "mistral-small-latest").SupportedReasoningEfforts!.ToArray());
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High },
            models.Single(static model => model.Id == "mistral-large-4").SupportedReasoningEfforts!.ToArray());
    }

    private async Task RunToolRoundAsync(IModelProviderTurnExecutor executor, ModelProviderRuntimeDescriptor descriptor, string model)
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone();
        AgentToolDefinition[] tools =
        [
            new(new AgentToolSpec("get_weather", "Gets the weather of a city.", schema),
                static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("Sunny, 21 C")]))),
        ];
        var user = new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("What is the weather in Paris? Use the tool, then answer in one sentence.")]);
        AgentTurnRequest Request(IReadOnlyList<AgentConversationMessage> conversation, string run) => new()
        {
            Provider = descriptor,
            ProviderId = new ModelProviderId("mistral"),
            SessionId = "session-live",
            RunId = new AgentRunId(run),
            ModelId = model,
            ModelInfo = new AgentModelInfo(model, SupportedReasoningEfforts: MistralReasoningEfforts.Find(model) ?? MistralReasoningEfforts.Default),
            ReasoningEffort = AgentReasoningEffort.High,
            SystemMessage = "You are a test agent.",
            DeveloperInstructions = "Be brief.",
            Conversation = conversation,
            Tools = tools,
            State = new AgentSessionState { SessionId = "session-live", UpdatedAt = DateTimeOffset.UtcNow },
        };

        var deltas = new List<string>();
        ValueTask OnUpdate(AgentTurnDelta delta, CancellationToken _)
        {
            deltas.Add(delta.GetType().Name);
            return ValueTask.CompletedTask;
        }

        var first = await executor.ExecuteTurnAsync(Request([user], "run-1"), OnUpdate, TestContext.CancellationToken);
        TestContext.WriteLine($"first parts: {string.Join(", ", first.AssistantMessage.Parts.Select(Describe))}");
        TestContext.WriteLine($"first usage: in={first.Usage?.LastOperation?.InputTokens} out={first.Usage?.LastOperation?.OutputTokens} reasoning={first.Usage?.LastOperation?.ReasoningTokens}");
        TestContext.WriteLine($"deltas: {string.Join(",", deltas.Distinct())}");
        var call = first.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().FirstOrDefault();
        Assert.IsNotNull(call, "the model called the tool");
        Assert.AreEqual("get_weather", call.Name);
        // GLM and DeepSeek may call the tool without reasoning first: only Mistral Small is asked to have reasoned.
        if (model == "mistral-small-latest")
        {
            Assert.IsTrue(first.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Any(), "the model reasoned");
        }

        var tool = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [new AgentMessagePart.ToolResult(call.CallId, new AgentToolResult(true, [new AgentToolResultItem.Text("Sunny, 21 C")]))]);
        var second = await executor.ExecuteTurnAsync(Request([user, first.AssistantMessage, tool], "run-2"), OnUpdate, TestContext.CancellationToken);
        TestContext.WriteLine($"second parts: {string.Join(", ", second.AssistantMessage.Parts.Select(Describe))}");
        var text = string.Concat(second.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
        StringAssert.Contains(text, "21");
    }

    private static string Describe(AgentMessagePart part)
        => part switch
        {
            AgentMessagePart.Text text => $"text({Excerpt(text.Value)})",
            AgentMessagePart.Reasoning reasoning => $"reasoning({Excerpt(reasoning.Value)}, protected={reasoning.ProtectedData is not null})",
            AgentMessagePart.ToolCall call => $"call({call.CallId} {call.Name} {call.Arguments.GetRawText()})",
            _ => part.GetType().Name,
        };

    private static string Excerpt(string? value)
        => value is null ? "null" : value.Length <= 60 ? value.ReplaceLineEndings(" ") : value[..60].ReplaceLineEndings(" ") + "…";

    private static bool TryGetKey(out string key, out string model)
    {
        key = Environment.GetEnvironmentVariable("CODEALTA_TEST_MISTRAL_API_KEY") ?? string.Empty;
        model = Environment.GetEnvironmentVariable("CODEALTA_TEST_MISTRAL_MODEL") is { Length: > 0 } named ? named : "mistral-small-latest";
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Inconclusive("CODEALTA_TEST_MISTRAL_API_KEY is not set.");
            return false;
        }

        return true;
    }

    // Writes each request body and the status of its response, for reading what went on the wire.
    private sealed class LoggingHandler(TestContext context) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            context.WriteLine($"> {request.Method} {request.RequestUri} {body}");
            var response = await base.SendAsync(request, cancellationToken);
            context.WriteLine($"< {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                context.WriteLine($"< {content}");
                response.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
