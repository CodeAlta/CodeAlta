using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>
/// The model suggests the next prompt of the user with its answer: the session asks it to in its instructions, hides
/// the <c>&lt;next_prompt&gt;</c> tag it writes, and tells the suggestion once the turn ended.
/// </summary>
[TestClass]
public sealed class AgentSessionPromptSuggestionTests
{
    private const string Guidance = "Next prompt suggestion: end your final answer with a <next_prompt> tag.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Suggestion_IsTheTagOfTheAnswer_ToldAfterTheRun_AndNeverShownNorRecorded()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor
        {
            Chunks = ["I fixed it.\n<next", "_prompt>run the ", "tests</next_pr", "ompt>"],
        };
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        StringAssert.Contains(executor.DeveloperInstructions[^1], Guidance);
        var all = events.All();
        var told = all.OfType<AgentPromptSuggestionEvent>().Single();
        Assert.AreEqual("run the tests", told.Suggestion);
        Assert.IsNull(told.RunId, "A suggestion comes outside the runs.");
        Assert.IsTrue(Array.FindIndex(all, static e => e is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }) < Array.IndexOf(all, told),
            "Those who listen take a suggestion only once the run is idle.");
        // Not a character of the tag is shown while the answer streams, nor once it is complete.
        Assert.AreEqual("I fixed it.\n", string.Concat(all.OfType<AgentContentDeltaEvent>().Select(static delta => delta.Delta)));
        Assert.AreEqual("I fixed it.\n", all.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
        Assert.IsFalse((await session.GetHistoryAsync()).OfType<AgentPromptSuggestionEvent>().Any(), "A suggestion is not history.");
    }

    [TestMethod]
    public async Task Suggestion_IsNoneWhenTheLastAnswerHasNoFitTag()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Nothing obvious follows."] };
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);
        // Too long to be sent as it is: it is hidden all the same, and suggests nothing.
        executor.Chunks = ["Done.\n<next_prompt>" + new string('x', AgentPromptSuggestionEvent.MaximumLength + 1) + "</next_prompt>"];
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Timeout);

        Assert.AreEqual(0, events.All().OfType<AgentPromptSuggestionEvent>().Count());
        Assert.AreEqual("Done.\n", events.All().OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task SessionThatTakesNone_IsNotAskedAndToldNothing_ButTheTagIsStillHidden()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>go ahead</next_prompt>"] };
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: false);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);

        Assert.IsFalse(executor.DeveloperInstructions[^1]!.Contains("Next prompt suggestion", StringComparison.Ordinal));
        Assert.AreEqual(0, events.All().OfType<AgentPromptSuggestionEvent>().Count());
        Assert.AreEqual("Done.\n", events.All().OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task Setting_IsReadAtEachRun()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>go ahead</next_prompt>"] };
        var enabled = false;
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true, () => Volatile.Read(ref enabled));
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WaitAsync(Timeout);
        Assert.IsFalse(executor.DeveloperInstructions[^1]!.Contains("Next prompt suggestion", StringComparison.Ordinal));
        Assert.AreEqual(0, events.All().OfType<AgentPromptSuggestionEvent>().Count(), "A tag the model writes while the setting is off is not told.");

        Volatile.Write(ref enabled, true);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Timeout);
        StringAssert.Contains(executor.DeveloperInstructions[^1], Guidance);
        Assert.AreEqual("go ahead", events.All().OfType<AgentPromptSuggestionEvent>().Single().Suggestion);
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

        public AgentEvent[] All()
        {
            lock (_events) return [.. _events];
        }
    }

    private static async Task<AgentSession> CreateSessionAsync(TestTempDirectory directory, AnsweringExecutor executor, bool promptSuggestions, Func<bool>? enabled = null)
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
            Streaming = true,
            PromptSuggestions = promptSuggestions,
            PromptSuggestionInstructions = Guidance,
            PromptSuggestionsEnabled = enabled,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        return new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            executor, options, cachedModels: [new AgentModelInfo("fixture-model", "Fixture model")]);
    }

    /// <summary>A provider that streams its answer in the chunks the test gives.</summary>
    private sealed class AnsweringExecutor : IModelProviderTurnExecutor
    {
        public string[] Chunks { get; set; } = ["An answer."];

        /// <summary>The developer instructions of each request, in order.</summary>
        public List<string?> DeveloperInstructions { get; } = [];

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            // The paragraph is of this session alone: it comes with the instructions of the session, after the others.
            lock (DeveloperInstructions) DeveloperInstructions.Add(AgentInstructionText.DeveloperAndSession(request));
            foreach (var chunk in Chunks)
            {
                await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "answer-1", Text = chunk }, cancellationToken);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(string.Concat(Chunks))]),
                AssistantPartContentIds = ["answer-1"],
            };
        }
    }
}
