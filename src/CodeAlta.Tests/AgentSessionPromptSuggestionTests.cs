using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>What a session does with the prompts its provider suggests.</summary>
[TestClass]
public sealed class AgentSessionPromptSuggestionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Suggestion_IsToldBetweenTheRuns_NeverDuringOne_AndIsNotRecorded()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new SuggestingExecutor();
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true);
        var events = Collect(session);

        // A late suggestion arrives while the next run goes on: it was for the turn before.
        executor.DuringTurn = "for the turn before";
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);
        Assert.AreEqual(true, executor.RequestTakesSuggestions, "The provider is told that the session takes suggestions.");
        Assert.AreEqual(0, events.Told().Length, "What is suggested while a run goes on answers another turn.");

        // Once the send returned its run is released: what is suggested now is told at once.
        executor.Suggest("  go ahead  ");
        var told = events.Told().Single();
        Assert.AreEqual("go ahead", told.Suggestion);
        Assert.IsNull(told.RunId);

        // What is not one line somebody would send as it is, is none.
        foreach (var unfit in new[] { " ", "one\ntwo", "tab\there", new string('x', AgentPromptSuggestionEvent.MaximumLength + 1) })
        {
            executor.Suggest(unfit);
        }

        executor.Suggest(new string('x', AgentPromptSuggestionEvent.MaximumLength));
        Assert.AreEqual(2, events.Told().Length);
        Assert.IsFalse((await session.GetHistoryAsync()).OfType<AgentPromptSuggestionEvent>().Any(), "A suggestion is not history.");
    }

    [TestMethod]
    public async Task Suggestion_IsNotToldToASessionThatTakesNone()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new SuggestingExecutor();
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: false);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);
        executor.Suggest("go ahead");

        Assert.AreEqual(false, executor.RequestTakesSuggestions, "The provider is told not to make any.");
        Assert.AreEqual(0, events.Told().Length);
    }

    private static EventLog Collect(AgentSession session)
    {
        var log = new EventLog();
        session.Subscribe(log.Add);
        return log;
    }

    private sealed class EventLog
    {
        private readonly List<AgentEvent> _events = [];

        public void Add(AgentEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public AgentPromptSuggestionEvent[] Told()
        {
            lock (_events) return [.. _events.OfType<AgentPromptSuggestionEvent>()];
        }
    }

    private static async Task<AgentSession> CreateSessionAsync(TestTempDirectory directory, SuggestingExecutor executor, bool promptSuggestions)
    {
        var sessionId = Guid.CreateVersion7().ToString();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(directory.Path, "store")));
        var timestamp = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "suggestion-fixture",
            DisplayName = "Scripted suggestion fixture",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://suggestion-fixture.invalid/"),
            Profile = new AgentProviderProfile(),
            Compaction = AgentCompactionSettings.Default with { Enabled = false },
        };
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = provider.ProtocolFamily,
            ProviderKey = provider.ProviderKey,
            ModelId = "fixture-model",
            WorkingDirectory = directory.Path,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        var state = new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = provider.ProtocolFamily,
            ProviderKey = provider.ProviderKey,
            UpdatedAt = timestamp,
        };
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        var options = new AgentSessionCreateOptions
        {
            ProviderKey = provider.ProviderKey,
            Model = summary.ModelId,
            WorkingDirectory = directory.Path,
            ProjectRoots = [directory.Path],
            InstructionsAlreadyComposed = true,
            SystemMessage = "Isolated fixture instructions.",
            DeveloperInstructions = "Do not discover project instructions.",
            PromptSuggestions = promptSuggestions,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        return new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            executor, options, cachedModels: [new AgentModelInfo("fixture-model", "Fixture model")]);
    }

    /// <summary>A provider that answers a turn with a text, and suggests prompts when the test says so.</summary>
    private sealed class SuggestingExecutor : IModelProviderTurnExecutor, IAgentProviderPromptSuggestions
    {
        private Action<string>? _handler;

        /// <summary>What the provider suggests while a turn goes on.</summary>
        public string? DuringTurn { get; set; }

        /// <summary>What the last request said: whether its session takes suggestions.</summary>
        public bool? RequestTakesSuggestions { get; private set; }

        public void Suggest(string suggestion) => Volatile.Read(ref _handler)?.Invoke(suggestion);

        public IDisposable OnPromptSuggestion(string sessionId, Action<string> handler)
        {
            Volatile.Write(ref _handler, handler);
            return new Registration(this);
        }

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestTakesSuggestions = request.PromptSuggestions;
            if (DuringTurn is { } suggestion) Suggest(suggestion);
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Should I go ahead?")]),
            });
        }

        private sealed class Registration(SuggestingExecutor owner) : IDisposable
        {
            public void Dispose() => Volatile.Write(ref owner._handler, null);
        }
    }
}
