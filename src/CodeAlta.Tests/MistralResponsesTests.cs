using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class MistralResponsesTests
{
    // A tool call with its reasoning, as Mistral streams it: its times have a fraction, and its reasoning is plain
    // reasoning_text without encrypted content.
    private const string ToolCallStream = """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","created_at":1793306289.0,"status":"in_progress","model":"mistral-small-latest","output":[],"store":false}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_1","summary":[],"type":"reasoning","content":[],"encrypted_content":null,"status":"in_progress"}}

        event: response.reasoning_text.delta
        data: {"type":"response.reasoning_text.delta","sequence_number":2,"content_index":0,"delta":"Call the tool.","item_id":"rs_1","output_index":0}

        event: response.output_item.done
        data: {"type":"response.output_item.done","sequence_number":3,"output_index":0,"item":{"id":"rs_1","summary":[],"type":"reasoning","content":[{"text":"Call the tool.","type":"reasoning_text"}],"encrypted_content":null,"status":"completed"}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":4,"output_index":1,"item":{"arguments":"","call_id":"R8cTQroQZ","name":"lookup_weather","type":"function_call","id":"fc_1","status":"in_progress"}}

        event: response.function_call_arguments.delta
        data: {"type":"response.function_call_arguments.delta","sequence_number":5,"delta":"{\"city\": \"Paris\"}","item_id":"fc_1","output_index":1}

        event: response.output_item.done
        data: {"type":"response.output_item.done","sequence_number":6,"output_index":1,"item":{"arguments":"{\"city\": \"Paris\"}","call_id":"R8cTQroQZ","name":"lookup_weather","type":"function_call","id":"fc_1","status":"completed"}}

        event: response.completed
        data: {"type":"response.completed","sequence_number":7,"response":{"id":"resp_1","object":"response","created_at":1793306289.0,"completed_at":1793306290.0,"status":"completed","model":"mistral-small-latest","output":[{"id":"rs_1","summary":[],"type":"reasoning","content":[{"text":"Call the tool.","type":"reasoning_text"}],"encrypted_content":null,"status":"completed"},{"arguments":"{\"city\": \"Paris\"}","call_id":"R8cTQroQZ","name":"lookup_weather","type":"function_call","id":"fc_1","status":"completed"}],"usage":{"input_tokens":85,"input_tokens_details":{"cached_tokens":64},"output_tokens":20,"output_tokens_details":{"reasoning_tokens":12},"total_tokens":105},"store":false}}


        """;

    [TestMethod]
    public void Stream_TruncatesTheTimesOfTheEventsOnly()
    {
        Assert.AreEqual("""{"created_at":1793306289,"completed_at":1793306290}""", Truncate("""{"created_at":1793306289.0,"completed_at":1793306290.25}"""));
        Assert.AreEqual("""{"expires_at": 17}""", Truncate("""{"expires_at": 17.5}"""));
        // A name in a string, a number with an exponent, another property and an integer are left as they are.
        const string Unchanged = """{"text":"\"created_at\":1.5","created_at":1.7e9,"price":1.5,"started_at":12}""";
        Assert.AreEqual(Unchanged, Truncate(Unchanged));
    }

    [TestMethod]
    public async Task Stream_PassesEachLineOnAsItEnds_WhateverTheReads()
    {
        var source = Encoding.UTF8.GetBytes("data: {\"created_at\":1.0}\n\ndata: {\"completed_at\":2.5}\n\ntail");
        using var stream = new MistralResponsesStream(new TrickleStream(source, chunk: 3));
        using var reader = new StreamReader(stream);

        Assert.AreEqual("data: {\"created_at\":1}\n\ndata: {\"completed_at\":2}\n\ntail", await reader.ReadToEndAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ResponsesExecutor_ReadsTheStreamOfMistral_AndSendsAStatelessRequest()
    {
        using var handler = new RecordingHandler(ToolCallStream);
        using var httpClient = new HttpClient(handler);
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            BaseUri = new Uri("https://mistral.test"),
            Api = MistralApi.Responses,
            HttpClient = httpClient,
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest("mistral-small-latest", AgentReasoningEffort.High, [AgentReasoningEffort.None, AgentReasoningEffort.High]),
            static (_, _) => ValueTask.CompletedTask,
            TestContext.CancellationToken);

        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual("Call the tool.", reasoning.Value);
        var call = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("R8cTQroQZ", call.CallId);
        Assert.AreEqual("lookup_weather", call.Name);
        Assert.AreEqual("Paris", call.Arguments.GetProperty("city").GetString());
        Assert.AreEqual(64L, response.Usage?.LastOperation?.CachedInputTokens);
        Assert.AreEqual(12L, response.Usage?.LastOperation?.ReasoningTokens);

        Assert.AreEqual("https://mistral.test/v1/responses", handler.LastUri?.AbsoluteUri);
        Assert.AreEqual("Bearer test-key", handler.LastAuthorization);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        // Stateless: no stored response and no earlier one named; Mistral refuses both.
        Assert.IsFalse(root.TryGetProperty("store", out var store) && store.ValueKind == JsonValueKind.True);
        Assert.IsFalse(root.TryGetProperty("previous_response_id", out _));
        Assert.AreEqual("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.AreEqual("lookup_weather", root.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    [DataRow(AgentReasoningEffort.None, "none")]
    [DataRow(AgentReasoningEffort.Max, "max")]
    public async Task ResponsesExecutor_SendsTheEffortAsMistralNamesIt(AgentReasoningEffort effort, string expected)
    {
        using var handler = new RecordingHandler(ToolCallStream);
        using var httpClient = new HttpClient(handler);
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            Api = MistralApi.Responses,
            HttpClient = httpClient,
        });

        await executor.ExecuteTurnAsync(
            CreateTurnRequest("glm-5-2", effort, MistralReasoningEfforts.Find("glm-5-2")!),
            static (_, _) => ValueTask.CompletedTask,
            TestContext.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.AreEqual(expected, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [TestMethod]
    [DataRow(AgentReasoningEffort.None, "none")]
    [DataRow(AgentReasoningEffort.Max, "max")]
    [DataRow(AgentReasoningEffort.XHigh, "xhigh")]
    public async Task ChatExecutor_SendsTheEffortAsMistralNamesIt(AgentReasoningEffort effort, string expected)
    {
        using var handler = new RecordingHandler("""
            data: {"id":"c","created":1,"model":"glm-5-2","choices":[{"index":0,"delta":{"content":"OK"},"finish_reason":"stop"}]}

            data: [DONE]

            """);
        using var httpClient = new HttpClient(handler);
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });

        await executor.ExecuteTurnAsync(
            CreateTurnRequest("glm-5-2", effort, MistralReasoningEfforts.Find("glm-5-2")!) with
            {
                Provider = new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "mistral-chat",
                    ProviderKey = "mistral",
                    DisplayName = "Mistral",
                    TransportKind = AgentTransportKind.MistralChat,
                },
            },
            static (_, _) => ValueTask.CompletedTask,
            TestContext.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.AreEqual(expected, body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [TestMethod]
    public async Task ModelList_GivesEachReasoningModelTheEffortsItTakes()
    {
        using var handler = new RecordingHandler("""
            {"data":[
              {"id":"mistral-small-latest","capabilities":{"completion_chat":true,"reasoning":true}},
              {"id":"mistral-large-4-0","aliases":["mistral-large-4"],"capabilities":{"completion_chat":true,"reasoning":true}},
              {"id":"zai-glm-5-3","capabilities":{"completion_chat":true,"reasoning":true}},
              {"id":"codestral-latest","capabilities":{"completion_chat":true,"reasoning":false}}
            ]}
            """);
        using var httpClient = new HttpClient(handler);
        await using var runtime = new MistralModelProviderRuntime(new MistralModelProviderRuntimeOptions
        {
            Providers = { new MistralProviderOptions { ProviderKey = "mistral", ApiKey = "test-key", HttpClient = httpClient } },
        });

        var models = (await runtime.ModelCatalog!.ListModelsAsync(runtime.RuntimeDescriptor, TestContext.CancellationToken))
            .ToDictionary(static model => model.Id);

        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.None, AgentReasoningEffort.High }, models["mistral-small-latest"].SupportedReasoningEfforts!.ToArray());
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, models["mistral-large-4-0"].SupportedReasoningEfforts!.ToArray());
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.Max }, models["zai-glm-5-3"].SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual(0, models["codestral-latest"].SupportedReasoningEfforts!.Count);
        Assert.AreEqual(AgentReasoningEffort.High, models["mistral-large-4-0"].DefaultReasoningEffort);
        Assert.AreEqual(AgentReasoningEffort.High, models["zai-glm-5-3"].DefaultReasoningEffort);
        Assert.AreEqual("https://api.mistral.ai/v1/models", handler.LastUri?.AbsoluteUri);
    }

    [TestMethod]
    public async Task VibeModelList_IsTheCatalogOfVibe_WithItsEfforts()
    {
        using var handler = new RecordingHandler("""
            {"models":[
              {"id":"mistral-medium-latest","label":"Mistral Medium 3.5","aliases":["mistral-medium-3.5"],"max_context_length":262144,
               "parameters":{"temperature":1,"reasoning":["none","high"]},"capabilities":{"completion_chat":true,"function_calling":true,"vision":true}},
              {"id":"zai-glm-5-3","label":"GLM 5.3","publisher":"zai","parameters":{"reasoning":["low","high","max"]},"capabilities":{"completion_chat":true,"function_calling":true}},
              {"id":"devstral-small","label":"Devstral Small","capabilities":{"completion_chat":true,"function_calling":true}},
              {"id":"no-tools","label":"No tools","capabilities":{"completion_chat":true,"function_calling":false}}
            ]}
            """);
        using var httpClient = new HttpClient(handler);
        var keys = 0;
        await using var runtime = new MistralModelProviderRuntime(new MistralModelProviderRuntimeOptions
        {
            Providers =
            {
                new MistralProviderOptions
                {
                    ProviderKey = "mistral-vibe",
                    ApiKeyProvider = _ => { keys++; return ValueTask.FromResult("vibe-key"); },
                    UsesVibeModelCatalog = true,
                    HttpClient = httpClient,
                },
            },
        });

        var models = await runtime.ModelCatalog!.ListModelsAsync(runtime.RuntimeDescriptor, TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "mistral-medium-latest", "zai-glm-5-3", "devstral-small" }, models.Select(static model => model.Id).ToArray());
        Assert.AreEqual("Mistral Medium 3.5", models[0].DisplayName);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.None, AgentReasoningEffort.High }, models[0].SupportedReasoningEfforts!.ToArray());
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.Max }, models[1].SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual(0, models[2].SupportedReasoningEfforts!.Count);
        Assert.AreEqual("https://api.mistral.ai/v1/model-catalog?surface=vibe-code", handler.LastUri?.AbsoluteUri);
        Assert.AreEqual("Bearer vibe-key", handler.LastAuthorization);
        Assert.AreEqual(1, keys);
    }

    // Streams Mistral sent (live captures of 2026-10-29, in TestData/Mistral), on both APIs. GLM and DeepSeek call the
    // tool without reasoning first, and send an empty name in the chunks after the first one.
    [TestMethod]
    [DataRow("chat", "mistral-small-latest", true)]
    [DataRow("chat", "zai-glm-5-3", false)]
    [DataRow("chat", "deepseek-v4-1-flash-1017", false)]
    [DataRow("responses", "mistral-small-latest", true)]
    [DataRow("responses", "zai-glm-5-3", false)]
    [DataRow("responses", "deepseek-v4-1-flash-1017", false)]
    public async Task CapturedToolCall_IsReadWithItsNameArgumentsAndReasoning(string api, string model, bool reasons)
    {
        var response = await ReplayAsync(api, $"{api}-tool-call.{model}.sse", model);

        var call = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("get_weather", call.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(call.CallId));
        Assert.AreEqual("Paris", call.Arguments.GetProperty("city").GetString());
        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().ToArray();
        Assert.AreEqual(reasons, reasoning.Length > 0 && reasoning.All(static part => !string.IsNullOrWhiteSpace(part.Value)));
        Assert.IsTrue(response.Usage?.LastOperation?.InputTokens > 0);
    }

    [TestMethod]
    [DataRow("chat")]
    [DataRow("responses")]
    public async Task CapturedReasoning_OfGlm_IsReasoningThenTheAnswer(string api)
    {
        var response = await ReplayAsync(api, $"{api}-reasoning.zai-glm-5-3.sse", "zai-glm-5-3");

        Assert.IsFalse(string.IsNullOrWhiteSpace(response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single().Value));
        var text = string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
        StringAssert.Contains(text, "391");
    }

    public TestContext TestContext { get; set; } = null!;

    private async Task<AgentTurnResponse> ReplayAsync(string api, string file, string model)
    {
        using var handler = new RecordingHandler(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Mistral", file), TestContext.CancellationToken));
        using var httpClient = new HttpClient(handler);
        var responses = api == "responses";
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            Api = responses ? MistralApi.Responses : MistralApi.ChatCompletions,
            HttpClient = httpClient,
        });
        var request = CreateTurnRequest(model, AgentReasoningEffort.High, MistralReasoningEfforts.Find(model) ?? MistralReasoningEfforts.Default);
        return await executor.ExecuteTurnAsync(
            responses
                ? request
                : request with
                {
                    Provider = new ModelProviderRuntimeDescriptor
                    {
                        ProtocolFamily = "mistral-chat",
                        ProviderKey = "mistral",
                        DisplayName = "Mistral",
                        TransportKind = AgentTransportKind.MistralChat,
                    },
                },
            static (_, _) => ValueTask.CompletedTask,
            TestContext.CancellationToken);
    }

    private static string Truncate(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        return Encoding.UTF8.GetString(bytes, 0, MistralResponsesStream.TruncateTimes(bytes));
    }

    private static AgentTurnRequest CreateTurnRequest(string model, AgentReasoningEffort effort, IReadOnlyList<AgentReasoningEffort> efforts)
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone();
        return new AgentTurnRequest
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = MistralResponsesTurnExecutor.ProtocolFamily,
                ProviderKey = "mistral",
                DisplayName = "Mistral",
                TransportKind = AgentTransportKind.OpenAIResponses,
            },
            ProviderId = new ModelProviderId("mistral"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = model,
            ModelInfo = new AgentModelInfo(model, SupportedReasoningEfforts: efforts),
            ReasoningEffort = effort,
            SystemMessage = "System instructions",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Weather in Paris?")])],
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("lookup_weather", "Look up weather.", schema),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            ],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var mediaType = responseBody.TrimStart().StartsWith('{') ? "application/json" : "text/event-stream";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, mediaType) };
        }
    }

    // Gives a few bytes at each read, so that a line comes in pieces.
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
