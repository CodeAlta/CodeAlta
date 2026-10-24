using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// What a session read again (the first prompt after a start of CodeAlta, an archive, a move) reads of its journal:
/// what the provider needs, never the whole history when it does not need it.
/// </summary>
[TestClass]
public sealed class AgentRuntimeResumeHistoryTests
{
    private const string KeptProviderKey = "kept";
    private const string ApiProviderKey = "api";

    [TestMethod]
    public async Task SessionOfAProviderThatKeepsItsConversation_IsReadAgainWithoutItsHistory()
    {
        using var temp = TestTempDirectory.Create();
        string sessionId;
        await using (var runtime = CreateRuntime(temp.Path, new KeptConversationExecutor(), new ApiExecutor()))
        {
            await using var session = await runtime.CreateSessionAsync(CreateOptions(KeptProviderKey));
            sessionId = session.SessionId;
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("one") });
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("two") });
        }

        var kept = new KeptConversationExecutor();
        await using var resumedRuntime = CreateRuntime(temp.Path, kept, new ApiExecutor());
        await using var resumed = await resumedRuntime.ResumeSessionAsync(sessionId, ResumeOptions());
        Assert.AreEqual(0, resumedRuntime.SessionStore.HistoryEventsRead, "The provider holds the conversation: no event of the journal is read.");

        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("three") });

        Assert.AreEqual(0, resumedRuntime.SessionStore.HistoryEventsRead, "The provider continued its conversation without the history.");
        var request = kept.Requests.Single();
        CollectionAssert.AreEqual(new[] { "three" }, Texts(request.Conversation));
        Assert.IsNotNull(request.EarlierConversation);
        Assert.IsNull(request.EarlierConversation.Messages, "Nothing loaded the earlier conversation.");
        Assert.AreEqual("2", request.EarlierConversation.State.ProviderState?.GetProperty("turns").ToString());

        // The provider that cannot continue its own conversation loads the one CodeAlta recorded.
        var earlier = await request.EarlierConversation.LoadAsync();
        CollectionAssert.AreEqual(new[] { "one", "kept #1", "two", "kept #2" }, Texts(earlier));
        Assert.IsGreaterThan(0L, resumedRuntime.SessionStore.HistoryEventsRead);

        // And the history is whole for whoever asks the session for it.
        var history = await resumed.GetHistoryAsync();
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, Prompts(history));
    }

    [TestMethod]
    public async Task SessionOfAProviderThatKeepsItsConversation_IsGivenItWhenTheProviderLoadsIt()
    {
        using var temp = TestTempDirectory.Create();
        string sessionId;
        await using (var runtime = CreateRuntime(temp.Path, new KeptConversationExecutor(), new ApiExecutor()))
        {
            await using var session = await runtime.CreateSessionAsync(CreateOptions(KeptProviderKey));
            sessionId = session.SessionId;
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("one") });
        }

        var kept = new KeptConversationExecutor { LoadsEarlierConversation = true };
        await using var resumedRuntime = CreateRuntime(temp.Path, kept, new ApiExecutor());
        await using var resumed = await resumedRuntime.ResumeSessionAsync(sessionId, ResumeOptions());
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("two") });
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("three") });

        CollectionAssert.AreEqual(new[] { "one", "kept #1", "two" }, Texts(kept.WholeConversations[0]));
        CollectionAssert.AreEqual(new[] { "one", "kept #1", "two", "kept #1", "three" }, Texts(kept.WholeConversations[1]));
    }

    [TestMethod]
    public async Task SessionMovedToAProviderThatDoesNotKeepIt_IsGivenItsWholeConversation()
    {
        using var temp = TestTempDirectory.Create();
        string sessionId;
        await using (var runtime = CreateRuntime(temp.Path, new KeptConversationExecutor(), new ApiExecutor()))
        {
            await using var session = await runtime.CreateSessionAsync(CreateOptions(KeptProviderKey));
            sessionId = session.SessionId;
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("one") });
        }

        var api = new ApiExecutor();
        await using var resumedRuntime = CreateRuntime(temp.Path, new KeptConversationExecutor(), api);
        await using var resumed = await resumedRuntime.ResumeSessionAsync(sessionId, new AgentSessionResumeOptions
        {
            ProviderKey = ApiProviderKey,
            WorkingDirectory = "C:\\repo\\sample",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("two") });

        var request = api.Requests.Single();
        Assert.IsNull(request.EarlierConversation);
        CollectionAssert.AreEqual(new[] { "one", "kept #1", "two" }, Texts(request.Conversation));
    }

    [TestMethod]
    public async Task CompactedSessionOfCodeAlta_IsReadAgainFromItsLastCheckpoint_AndSendsTheSameRequest()
    {
        using var temp = TestTempDirectory.Create();
        var live = new ApiExecutor();
        await using var runtime = CreateRuntime(temp.Path, new KeptConversationExecutor(), live);
        await using var session = await runtime.CreateSessionAsync(CreateOptions(ApiProviderKey));
        foreach (var prompt in new[] { "one", "two", "three" })
        {
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text(prompt + " " + new string('x', 400)) });
        }

        await session.CompactAsync();
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("four") });

        // The same journal, read again by another start of CodeAlta while this session goes on.
        var copy = Path.Combine(temp.Path, "copy");
        CopyDirectory(Path.Combine(temp.Path, "machine"), Path.Combine(copy, "machine"));
        var resumedApi = new ApiExecutor();
        await using var resumedRuntime = CreateRuntime(copy, new KeptConversationExecutor(), resumedApi);
        await using var resumed = await resumedRuntime.ResumeSessionAsync(session.SessionId, ResumeOptions());

        var read = resumedRuntime.SessionStore.HistoryEventsRead;
        var whole = (await new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(copy, "machine", "agents")))
            .ReadEventsAsync(session.SessionId)).Count;
        Assert.IsGreaterThan(0L, read);
        Assert.IsLessThan(whole, read, "Only the events from the last compaction checkpoint are read.");

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("five") });
        await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("five") });

        var expected = live.Requests[^1];
        var actual = resumedApi.Requests[^1];
        Assert.IsNull(actual.EarlierConversation);
        CollectionAssert.AreEqual(Serialize(expected.Conversation), Serialize(actual.Conversation));
        Assert.AreEqual(expected.DeveloperInstructions, actual.DeveloperInstructions);
    }

    private static AgentSessionCreateOptions CreateOptions(string providerKey)
        => new()
        {
            ProviderKey = providerKey,
            Model = "model-1",
            WorkingDirectory = "C:\\repo\\sample",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static AgentSessionResumeOptions ResumeOptions()
        => new()
        {
            WorkingDirectory = "C:\\repo\\sample",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static string[] Texts(IEnumerable<AgentConversationMessage> conversation)
        => [.. conversation.Select(static message => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)))];

    private static string[] Prompts(IEnumerable<AgentEvent> history)
        => [.. history.OfType<AgentContentCompletedEvent>().Where(static item => item.Kind is AgentContentKind.User).Select(static item => item.Content)];

    private static string[] Serialize(IEnumerable<AgentConversationMessage> conversation)
        => [.. conversation.Select(static message => JsonSerializer.Serialize(message, AgentJsonSerializerContext.Default.AgentConversationMessage))];

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static AgentRuntime CreateRuntime(string root, KeptConversationExecutor kept, ApiExecutor api)
        => new(
            ModelProviderIds.OpenAIResponses,
            "Fixture",
            new AgentRuntimeOptions
            {
                StateRootPath = Path.Combine(root, "machine", "agents"),
                Providers =
                [
                    CreateRegistration(KeptProviderKey, "kept-cli", kept, isDefault: false),
                    CreateRegistration(ApiProviderKey, "openai-responses", api, isDefault: true),
                ],
            });

    private static AgentRuntimeProviderRegistration CreateRegistration(string key, string family, IModelProviderTurnExecutor executor, bool isDefault)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = family,
                ProviderKey = key,
                DisplayName = key,
                TransportKind = AgentTransportKind.OpenAIResponses,
                BaseUri = new Uri("https://api.example.com/v1"),
                IsDefault = isDefault,
                Profile = new AgentProviderProfile { SupportsDeveloperRole = true, StreamsUsage = true, MaxTokensFieldName = "max_output_tokens" },
            },
            TurnExecutor = executor,
        };

    // A provider whose CLI keeps the conversation: what it holds is counted in its state.
    private sealed class KeptConversationExecutor : IModelProviderTurnExecutor, IAgentProviderKeptConversation
    {
        public List<AgentTurnRequest> Requests { get; } = [];

        public List<IReadOnlyList<AgentConversationMessage>> WholeConversations { get; } = [];

        public bool LoadsEarlierConversation { get; init; }

        public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new AgentCompactionOutcome(true, "Compacted by the provider."));

        public async Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (LoadsEarlierConversation)
            {
                WholeConversations.Add(await AgentEarlierConversation.GetWholeConversationAsync(request, cancellationToken));
            }

            var turns = (request.State.ProviderState is { } state ? state.GetProperty("turns").GetInt32() : 0) + 1;
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text($"kept #{Requests.Count}")]),
                ProviderSessionId = "cli-conversation",
                ProviderState = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["turns"] = turns }),
            };
        }
    }

    // A provider CodeAlta sends the whole conversation to at each turn.
    private sealed class ApiExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        public List<AgentTurnRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo("model-1", "Model 1", Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal) { ["inputTokenLimit"] = 1_000L }),
            ]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            if (request.IsStandalone)
            {
                return Task.FromResult(new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("## Objective\nThe summary.")]),
                });
            }

            Requests.Add(request);
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text($"api #{Requests.Count}")]),
            });
        }
    }
}
