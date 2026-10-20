using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// The placeholders of the request headers of an OpenAI-compatible provider (#192): OpenCode Go asks for an
/// <c>x-opencode-session</c> that stays the same for one conversation and differs between conversations.
/// </summary>
[TestClass]
public sealed class OpenAIRequestHeaderPlaceholdersTests
{
    [TestMethod]
    public void AValue_IsFilledWithTheIdsOfTheTurn_AndKeepsWhatIsNoPlaceholder()
    {
        Assert.AreEqual("codealta-s1", OpenAIRequestHeaderPlaceholders.Fill("codealta-${session.id}", "s1", "r1", "m1", "p1"));
        Assert.AreEqual("s1/r1/m1/p1", OpenAIRequestHeaderPlaceholders.Fill("${session.id}/${run.id}/${model.id}/${provider.key}", "s1", "r1", "m1", "p1"));
        // What only looks like a placeholder is sent as it was written.
        Assert.AreEqual("${unknown}-s1", OpenAIRequestHeaderPlaceholders.Fill("${unknown}-${session.id}", "s1", "r1", "m1", "p1"));
        Assert.AreEqual("s1 ${session.id", OpenAIRequestHeaderPlaceholders.Fill("${session.id} ${session.id", "s1", "r1", "m1", "p1"));
        Assert.AreEqual("static", OpenAIRequestHeaderPlaceholders.Fill("static", "s1", "r1", "m1", "p1"));
        // A known placeholder without a value is empty, and a value never breaks the line of its header.
        Assert.AreEqual("model-", OpenAIRequestHeaderPlaceholders.Fill("model-${model.id}", "s1", "r1", null, "p1"));
        Assert.AreEqual("s1x", OpenAIRequestHeaderPlaceholders.Fill("${session.id}", "s1\r\nx", "r1", "m1", "p1"));
    }

    [TestMethod]
    public void TheHeadersOfATurn_AreTheProvidersAndTheModels_Filled()
    {
        var provider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-opencode-session"] = "${session.id}",
            ["X-Title"] = "CodeAlta",
        };
        var filled = OpenAIRequestHeaderPlaceholders.ForTurn(provider, modelRequest: null, Turn("s-1", "glm-5.3"))!;
        Assert.AreEqual("s-1", filled["x-opencode-session"]);
        Assert.AreEqual("CodeAlta", filled["X-Title"]);

        // The headers of a model join and win; a placeholder in them is filled too.
        var model = new AgentModelRequestOverride
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Model"] = "${model.id}" },
            RemoveHeaders = ["X-Title"],
        };
        var merged = OpenAIRequestHeaderPlaceholders.ForTurn(provider, model, Turn("s-2", "glm-5.3"))!;
        Assert.AreEqual(("s-2", "glm-5.3"), (merged["x-opencode-session"], merged["X-Model"]));
        Assert.IsFalse(merged.ContainsKey("X-Title"));

        // Without a placeholder or an override of the model, the provider's headers apply as they are.
        Assert.IsNull(OpenAIRequestHeaderPlaceholders.ForTurn(new Dictionary<string, string> { ["X-Title"] = "CodeAlta" }, null, Turn("s-3", "m")));
        Assert.IsNull(OpenAIRequestHeaderPlaceholders.ForTurn(null, null, Turn("s-3", "m")));
    }

    [TestMethod]
    public void OutsideATurn_AHeaderWithAPlaceholderIsLeftOut()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-opencode-session"] = "${session.id}",
            ["X-Title"] = "CodeAlta",
        };
        var outside = OpenAIRequestHeaderPlaceholders.OutsideTurn(headers)!;
        CollectionAssert.AreEqual(new[] { "X-Title" }, outside.Keys.ToArray());
        var plain = new Dictionary<string, string> { ["X-Title"] = "CodeAlta" };
        Assert.AreSame(plain, OpenAIRequestHeaderPlaceholders.OutsideTurn(plain), "Headers without a placeholder are used as they are.");
    }

    [TestMethod]
    public async Task TwoSessions_SendTheirOwnSessionHeader_AndASessionKeepsIt()
    {
        var handler = new RecordingHandler();
        var temp = Path.Combine(AppContext.BaseDirectory, "openai-header-placeholders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await using var runtime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
            {
                StateRootPath = temp,
                Providers =
                {
                    new OpenAIProviderOptions
                    {
                        ProviderKey = "opencode-go",
                        IsDefault = true,
                        ApiKey = "test-key",
                        BaseUri = new Uri("https://opencode.ai/inference/go/v1"),
                        HttpClient = new HttpClient(handler),
                        ExtraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["x-opencode-session"] = "${session.id}",
                            ["X-Title"] = "CodeAlta",
                        },
                        ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("glm-5.3", DisplayName: "GLM")]),
                    },
                },
            });

            async Task SendAsync(IAgentSession session)
                => _ = await session.SendAsync(new AgentSendOptions { Input = new AgentInput([new AgentInputItem.Text("Hello")]) });

            await using var first = await runtime.CreateSessionAsync(new AgentSessionCreateOptions { Model = "glm-5.3", WorkingDirectory = temp, OnPermissionRequest = AllowOnce });
            await using var second = await runtime.CreateSessionAsync(new AgentSessionCreateOptions { Model = "glm-5.3", WorkingDirectory = temp, OnPermissionRequest = AllowOnce });
            await SendAsync(first);
            await SendAsync(second);
            await SendAsync(first);

            var sent = handler.Sessions;
            Assert.HasCount(3, sent);
            Assert.IsTrue(sent.All(static value => !string.IsNullOrWhiteSpace(value) && !value.Contains("${", StringComparison.Ordinal)), string.Join(", ", sent));
            Assert.AreNotEqual(sent[0], sent[1], "Two sessions send different values.");
            Assert.AreEqual(sent[0], sent[2], "A session keeps its value from one turn to the next.");
            Assert.IsTrue(handler.Titles.All(static title => title == "CodeAlta"), "A header without a placeholder is sent as it is.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static AgentTurnRequest Turn(string sessionId, string modelId)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "openai-chat",
                ProviderKey = "opencode-go",
                DisplayName = "OpenCode Go",
                TransportKind = AgentTransportKind.OpenAIChatCompletions,
            },
            ProviderId = new ModelProviderId("openai-chat"),
            SessionId = sessionId,
            RunId = new AgentRunId("run-1"),
            ModelId = modelId,
            Conversation = [],
            Tools = [],
            State = new AgentSessionState { SessionId = sessionId, ProtocolFamily = "openai", ProviderKey = "opencode-go", UpdatedAt = DateTimeOffset.UtcNow },
        };

    private static Task<AgentPermissionDecision> AllowOnce(AgentPermissionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

    // Answers every request with a short streamed reply, and keeps the headers that the test looks at.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<string> Sessions { get; } = [];

        public List<string> Titles { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Sessions.Add(request.Headers.TryGetValues("x-opencode-session", out var session) ? string.Join(",", session) : "");
                Titles.Add(request.Headers.TryGetValues("X-Title", out var title) ? string.Join(",", title) : "");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1744060800,"model":"glm-5.3","choices":[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":null}]}

                    data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1744060800,"model":"glm-5.3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        }
    }
}
