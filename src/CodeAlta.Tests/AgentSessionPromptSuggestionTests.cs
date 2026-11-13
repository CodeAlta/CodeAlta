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
    private static readonly TimeSpan Timeout = TestTime.Seconds(20);

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

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);

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

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);
        // Too long to be sent as it is: it is hidden all the same, and suggests nothing.
        executor.Chunks = ["Done.\n<next_prompt>" + new string('x', AgentPromptSuggestionEvent.MaximumLength + 1) + "</next_prompt>"];
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WithinAsync(Timeout);

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

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);

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

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);
        Assert.IsFalse(executor.DeveloperInstructions[^1]!.Contains("Next prompt suggestion", StringComparison.Ordinal));
        Assert.AreEqual(0, events.All().OfType<AgentPromptSuggestionEvent>().Count(), "A tag the model writes while the setting is off is not told.");

        Volatile.Write(ref enabled, true);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WithinAsync(Timeout);
        StringAssert.Contains(executor.DeveloperInstructions[^1], Guidance);
        Assert.AreEqual("go ahead", events.All().OfType<AgentPromptSuggestionEvent>().Single().Suggestion);
    }

    [TestMethod]
    public async Task Suggestion_IsKeptInTheSummaryWithItsRun_UntilTheNextRunStarts()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>run the tests</next_prompt>"] };
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true);
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);

        var told = events.All().OfType<AgentPromptSuggestionEvent>().Single();
        var run = events.All().OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.Idle).RunId;
        Assert.AreEqual(run, told.AnsweredRunId, "A suggestion names the run it answers.");
        Assert.IsNull(told.RunId, "It still comes outside the runs.");
        // Read again from the journal, as a host that starts again reads it.
        var stored = await ReadSummaryAsync(directory, session.SessionId);
        Assert.AreEqual(("run the tests", run), (stored.PromptSuggestion, stored.PromptSuggestionRunId));

        // The next run starts without it, before it asks the model anything, and an answer without a tag keeps none.
        executor.Chunks = ["Nothing obvious follows."];
        AgentSessionSummary? duringRun = null;
        executor.OnTurn = async () => duringRun = await ReadSummaryAsync(directory, session.SessionId);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again") }).WithinAsync(Timeout);
        Assert.IsNull(duringRun!.PromptSuggestion);
        Assert.IsNull(duringRun.PromptSuggestionRunId);
        var after = await ReadSummaryAsync(directory, session.SessionId);
        Assert.IsNull(after.PromptSuggestion);
        Assert.IsNull(after.PromptSuggestionRunId);
    }

    [TestMethod]
    public async Task Suggestion_IsNotKept_ByARunThatFailsOrIsAbortedBeforeItStarts()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>run the tests</next_prompt>"] };
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);
        Assert.AreEqual("run the tests", (await ReadSummaryAsync(directory, session.SessionId)).PromptSuggestion);

        // The start of the run fails: the run was admitted, so the suggestion it answers is gone all the same.
        var failing = new Lifecycle(static _ => throw new InvalidOperationException("Binding failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("again"), RunLifecycle = failing }).WithinAsync(Timeout));
        Assert.IsNull((await ReadSummaryAsync(directory, session.SessionId)).PromptSuggestion);

        // A suggestion again, then a run aborted while its start waits.
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("once more") }).WithinAsync(Timeout);
        Assert.AreEqual("run the tests", (await ReadSummaryAsync(directory, session.SessionId)).PromptSuggestion);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new Lifecycle(async token => { waiting.TrySetResult(); await Task.Delay(System.Threading.Timeout.Infinite, token); });
        using var abort = new CancellationTokenSource();
        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("aborted"), RunLifecycle = blocked }, abort.Token);
        await waiting.Task.WithinAsync(Timeout);
        await abort.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => send.WithinAsync(Timeout));
        Assert.IsNull((await ReadSummaryAsync(directory, session.SessionId)).PromptSuggestion);
        Assert.HasCount(2, executor.DeveloperInstructions, "Neither the failed nor the aborted run reached the provider.");
    }

    [TestMethod]
    public async Task Suggestion_ThatTheCacheIsTooBusyToKeep_DoesNotFailTheRunThatEnded()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>run the tests</next_prompt>"] };
        var inner = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(directory.Path, "store")));
        await using var session = await CreateSessionAsync(directory, executor, promptSuggestions: true, store: new LockedOnSuggestionStore(inner));
        var events = Collect(session);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);

        Assert.AreEqual("run the tests", events.All().OfType<AgentPromptSuggestionEvent>().Single().Suggestion, "It is still offered.");
        Assert.IsFalse(events.All().OfType<AgentErrorEvent>().Any(), "The run that ended is not made a failure.");
        Assert.IsNull((await ReadSummaryAsync(directory, session.SessionId)).PromptSuggestion);
    }

    [TestMethod]
    public async Task Suggestion_IsNotKept_WhenTheSessionTakesNone_OrTheSettingIsOff()
    {
        using var directory = TestTempDirectory.Create();
        var executor = new AnsweringExecutor { Chunks = ["Done.\n<next_prompt>go ahead</next_prompt>"] };
        await using (var none = await CreateSessionAsync(directory, executor, promptSuggestions: false))
        {
            await none.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);
            Assert.IsNull((await ReadSummaryAsync(directory, none.SessionId)).PromptSuggestion);
        }

        await using var off = await CreateSessionAsync(directory, executor, promptSuggestions: true, static () => false);
        await off.SendAsync(new AgentSendOptions { Input = AgentInput.Text("hello") }).WithinAsync(Timeout);
        Assert.IsNull((await ReadSummaryAsync(directory, off.SessionId)).PromptSuggestion);
    }

    [TestMethod]
    public void Summary_WritesNoSuggestionItHasNot_AndReadsBackTheOneItHas()
    {
        var at = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var summary = new AgentSessionSummary { SessionId = "s", ProviderId = ModelProviderIds.OpenAIResponses, CreatedAt = at, UpdatedAt = at };
        var plain = System.Text.Json.JsonSerializer.Serialize(summary, AgentJsonSerializerContext.Default.AgentSessionSummary);
        Assert.DoesNotContain("promptSuggestion", plain, "A journal without a suggestion is written as before.");

        var suggested = summary with { PromptSuggestion = "commit it", PromptSuggestionRunId = new AgentRunId("run-1") };
        var json = System.Text.Json.JsonSerializer.Serialize(suggested, AgentJsonSerializerContext.Default.AgentSessionSummary);
        var read = System.Text.Json.JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentSessionSummary)!;
        Assert.AreEqual(("commit it", new AgentRunId("run-1")), (read.PromptSuggestion, read.PromptSuggestionRunId));
    }

    private static async Task<AgentSessionSummary> ReadSummaryAsync(TestTempDirectory directory, string sessionId)
        => (await new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(directory.Path, "store"))).GetSessionSummaryAsync(sessionId))!;

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

    private static async Task<AgentSession> CreateSessionAsync(TestTempDirectory directory, AnsweringExecutor executor, bool promptSuggestions, Func<bool>? enabled = null,
        IAgentSessionJournalStore? store = null)
    {
        var sessionId = Guid.CreateVersion7().ToString();
        store ??= new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(directory.Path, "store")));
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

    /// <summary>What a send observes of the start of its run: here, what the test makes of it.</summary>
    private sealed class Lifecycle(Func<CancellationToken, Task> started) : AgentRunLifecycle
    {
        public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken) => started(executionToken);

        public override Task ClosingAsync(AgentRunId runId) => Task.CompletedTask;
    }

    /// <summary>A store whose cache is locked whenever a summary with a suggestion is written.</summary>
    private sealed class LockedOnSuggestionStore(FileSystemAgentSessionStore inner) : IAgentSessionJournalStore
    {
        public Task UpsertSessionAsync(AgentSessionSummary session, CancellationToken cancellationToken = default)
            => session.PromptSuggestion is null ? inner.UpsertSessionAsync(session, cancellationToken) : throw new AgentSessionCacheLockedException();

        public Task<AgentSessionSummary?> GetSessionAsync(string protocolFamily, string providerKey, string sessionId, CancellationToken cancellationToken = default)
            => inner.GetSessionAsync(protocolFamily, providerKey, sessionId, cancellationToken);

        public Task<AgentSessionSummary?> GetSessionSummaryAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.GetSessionSummaryAsync(sessionId, cancellationToken);

        public IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(string protocolFamily, string providerKey, CancellationToken cancellationToken = default)
            => inner.ListSessionSummariesAsync(protocolFamily, providerKey, cancellationToken);

        public IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(CancellationToken cancellationToken = default)
            => inner.ListSessionSummariesAsync(cancellationToken);

        public Task AppendEventsAsync(string protocolFamily, string providerKey, string sessionId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken = default)
            => inner.AppendEventsAsync(protocolFamily, providerKey, sessionId, events, cancellationToken);

        public Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(string protocolFamily, string providerKey, string sessionId, CancellationToken cancellationToken = default)
            => inner.ReadEventsAsync(protocolFamily, providerKey, sessionId, cancellationToken);

        public Task UpsertStateAsync(AgentSessionState state, CancellationToken cancellationToken = default) => inner.UpsertStateAsync(state, cancellationToken);

        public Task<AgentSessionState?> GetStateAsync(string protocolFamily, string providerKey, string sessionId, CancellationToken cancellationToken = default)
            => inner.GetStateAsync(protocolFamily, providerKey, sessionId, cancellationToken);

        public Task<AgentSessionState?> GetStateAsync(string sessionId, CancellationToken cancellationToken = default) => inner.GetStateAsync(sessionId, cancellationToken);

        public Task<bool> DeleteSessionAsync(string protocolFamily, string providerKey, string sessionId, CancellationToken cancellationToken = default)
            => inner.DeleteSessionAsync(protocolFamily, providerKey, sessionId, cancellationToken);

        public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default)
            => inner.ListSessionsAsync(filter, cancellationToken);

        public Task<AgentSessionMetadata?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) => inner.GetSessionAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken = default) => inner.ReadEventsAsync(sessionId, cancellationToken);

        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => inner.DeleteSessionAsync(sessionId, cancellationToken);
    }

    /// <summary>A provider that streams its answer in the chunks the test gives.</summary>
    private sealed class AnsweringExecutor : IModelProviderTurnExecutor
    {
        public string[] Chunks { get; set; } = ["An answer."];

        /// <summary>The developer instructions of each request, in order.</summary>
        public List<string?> DeveloperInstructions { get; } = [];

        /// <summary>What runs when a turn is asked for, before it answers.</summary>
        public Func<Task>? OnTurn { get; set; }

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            if (OnTurn is { } onTurn) await onTurn();
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
