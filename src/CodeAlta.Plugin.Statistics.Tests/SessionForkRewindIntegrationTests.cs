using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The journals the runtime writes when a session is forked and rewound, read by the engine of the statistics: every turn a
/// provider was paid for is counted once, in the session that made it.
/// </summary>
[TestClass]
public sealed class SessionForkRewindIntegrationTests
{
    [TestMethod]
    public async Task TheTurnsOfForkedAndRewoundSessions_AreEachCountedOnce()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var provider = new SpendingProvider();
        var projectRoot = Path.Combine(harness.Root, "project");
        Directory.CreateDirectory(projectRoot);
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(harness.Root, "global"),
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            StartPlugins = false,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        });
        var options = new SessionExecutionOptions
        {
            ProviderId = provider.Descriptor.ProviderId,
            ProviderKey = provider.Descriptor.ProviderId.Value,
            WorkingDirectory = projectRoot,
            ProjectRoots = [projectRoot],
            Model = "fake-model",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        await using var engine = new StatisticsEngine(harness.Store, host.SessionJournals, new StatisticsEngineOptions { StartDelay = TimeSpan.Zero, FlowDebounce = TimeSpan.Zero });
        await engine.InitializeAsync();
        await engine.ChooseHistoryAsync(HistoryChoice.All);

        // Three turns in a session.
        var source = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Review", CancellationToken.None);
        foreach (var prompt in new[] { "one", "two", "three" }) await SendAsync(host, source, options, prompt);
        await ReadAsync(engine, source.SessionId);
        var turns = (await host.RuntimeService.ReadSessionTurnsAsync(source.SessionId))!.Turns.Turns;
        var requestsOfATurn = await harness.SumAsync("usage_q", "requests") / 3;
        Assert.IsTrue(requestsOfATurn > 0, "A turn of the provider is a request.");
        await AssertSpentAsync(harness, provider, turns: 3, requestsOfATurn);
        Assert.AreEqual(3, (await RunsOfAsync(harness, source.SessionId)).Length);

        // A fork before the third turn starts with the first two: nothing more was spent.
        var forked = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId, BeforeRunId = turns[2].RunId });
        Assert.AreEqual("ok", forked.Status);
        var fork = forked.Session!;
        await ReadAsync(engine, fork.SessionId);
        await AssertSpentAsync(harness, provider, turns: 3, requestsOfATurn);
        Assert.AreEqual(0, (await RunsOfAsync(harness, fork.SessionId)).Length, "The turns a fork starts with are those of its source.");
        Assert.AreEqual(2L, await harness.CountAsync("session"));
        var forkRow = (await harness.Store.GetJournalAsync(fork.SessionId))!;
        Assert.AreEqual(fork.ForkedFrom!.CountedFrom, forkRow.ToCursor().State.Inherited!.CountedFrom, "The header of the fork says from when its records are its own.");

        // The fork makes a turn of its own.
        await SendAsync(host, await ListedAsync(host, fork.SessionId), options, "four");
        await ReadAsync(engine, fork.SessionId);
        await AssertSpentAsync(harness, provider, turns: 4, requestsOfATurn);
        Assert.AreEqual(1, (await RunsOfAsync(harness, fork.SessionId)).Length);

        // The source is rewound to before its second turn, and a new session keeps the two turns that are removed.
        var rewound = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = source.SessionId, BeforeRunId = turns[1].RunId });
        Assert.AreEqual("ok", rewound.Status);
        var kept = rewound.RemovedTurns!;
        await ReadAsync(engine, source.SessionId, kept.SessionId);
        await AssertSpentAsync(harness, provider, turns: 4, requestsOfATurn);
        CollectionAssert.AreEqual(new[] { turns[0].RunId }, await RunsOfAsync(harness, source.SessionId), "The rewound session lost the turns that were removed.");
        CollectionAssert.AreEqual(new[] { turns[1].RunId, turns[2].RunId }, await RunsOfAsync(harness, kept.SessionId), "They are counted in the session that keeps them.");

        // The rewound session and the one that keeps its turns both go on.
        await SendAsync(host, await ListedAsync(host, source.SessionId), options, "five");
        await SendAsync(host, await ListedAsync(host, kept.SessionId), options, "six");
        await ReadAsync(engine, source.SessionId, kept.SessionId);
        await AssertSpentAsync(harness, provider, turns: 6, requestsOfATurn);
        Assert.AreEqual(2, (await RunsOfAsync(harness, source.SessionId)).Length);
        Assert.AreEqual(3, (await RunsOfAsync(harness, kept.SessionId)).Length);

        // A rewind that discards what it removes: the turn is counted nowhere any more, though its provider was paid.
        var last = (await host.RuntimeService.ReadSessionTurnsAsync(source.SessionId))!.Turns.Turns[^1];
        var discarded = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = source.SessionId, BeforeRunId = last.RunId, KeepRemovedTurns = false });
        Assert.AreEqual("ok", discarded.Status);
        await ReadAsync(engine, source.SessionId);
        Assert.AreEqual(5 * requestsOfATurn, await harness.SumAsync("usage_q", "requests"));
        CollectionAssert.AreEqual(new[] { turns[0].RunId }, await RunsOfAsync(harness, source.SessionId));

        // The numbers are those of reading every journal at once, and the roll-ups follow.
        Assert.AreEqual(await ReferenceDumpAsync(host.SessionJournals, source.SessionId, fork.SessionId, kept.SessionId), await harness.DumpAsync());
        await harness.VerifyRollupsAsync();
    }

    // What the provider was sent and answered is what the statistics count, whatever the sessions its turns ended in.
    private static async Task AssertSpentAsync(StoreHarness harness, SpendingProvider provider, int turns, long requestsOfATurn)
    {
        Assert.AreEqual(turns, provider.Turns);
        Assert.AreEqual(turns * requestsOfATurn, await harness.SumAsync("usage_q", "requests"));
        Assert.AreEqual(provider.InputTokens * requestsOfATurn, await harness.SumAsync("usage_q", "input_tokens"));
        Assert.AreEqual(provider.OutputTokens * requestsOfATurn, await harness.SumAsync("usage_q", "output_tokens"));
        Assert.AreEqual((long)turns, await harness.SumAsync("activity_year", "runs_started"));
        Assert.AreEqual((long)turns, await harness.SumAsync("activity_year", "runs_completed"));
        Assert.AreEqual((long)turns, await harness.CountAsync("run"));
    }

    private static async Task ReadAsync(StatisticsEngine engine, params string[] sessions)
    {
        foreach (var session in sessions)
        {
            engine.Signal(session);
        }

        await engine.DrainAsync();
    }

    private static async Task<string[]> RunsOfAsync(StoreHarness harness, string sessionId)
        => [.. await harness.Store.ReadAsync(sql => sql.Query(
            $"SELECT run_id FROM {harness.Store.Prefix}run WHERE session_id = @p0 ORDER BY start_ms",
            static reader => reader.GetString(0),
            sessionId))];

    private static async Task<string> ReferenceDumpAsync(ISessionJournalCatalog journals, params string[] sessions)
    {
        await using var reference = await StoreHarness.CreateAsync();
        using var catchUp = new SessionCatchUp();
        foreach (var id in sessions)
        {
            await using var stream = (await journals.OpenAsync(id))!;
            var result = catchUp.CatchUp(id, stream, null, long.MaxValue, CancellationToken.None);
            var file = (await journals.GetAsync(id))!;
            await reference.Store.ApplyAsync(new ApplyRequest { SessionId = id, Batch = result.Batch, Cursor = result.Cursor, FileLength = file.Length, FileStampTicks = file.LastWriteUtc.UtcTicks });
        }

        return await reference.DumpAsync();
    }

    private static async Task SendAsync(CodeAltaHost host, SessionViewDescriptor session, SessionExecutionOptions options, string prompt)
    {
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text(prompt) }, CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await host.RuntimeService.HasActiveRunAsync(session, CancellationToken.None))
        {
            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "The run did not end.");
            await Task.Delay(10);
        }
    }

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session {sessionId} is not listed.");
        return null!;
    }

    // Answers every prompt, says what the turn cost in tokens, and keeps the sum of what it said.
    private sealed class SpendingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        private int _turns;
        private long _inputTokens;
        private long _outputTokens;

        public int Turns => Volatile.Read(ref _turns);

        public long InputTokens => Interlocked.Read(ref _inputTokens);

        public long OutputTokens => Interlocked.Read(ref _outputTokens);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-spending"), "Fake Spending") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = "fake-spending",
            DisplayName = "Fake Spending",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            // Every turn costs another number of tokens, so that a turn counted twice or left out changes the sums.
            var turn = Interlocked.Increment(ref _turns);
            var input = 1000L << turn;
            var output = 10L * turn;
            Interlocked.Add(ref _inputTokens, input);
            Interlocked.Add(ref _outputTokens, output);
            var contentId = "content-" + Guid.NewGuid().ToString("N");
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = contentId, Text = "answer" }, cancellationToken).ConfigureAwait(false);
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answer")]),
                AssistantPartContentIds = [contentId],
                ProviderSessionId = "response-" + turn,
                Usage = new AgentSessionUsage(
                    LastOperation: new AgentOperationUsageSnapshot("fake-model", InputTokens: input, OutputTokens: output),
                    Scope: AgentUsageScope.LastOperation,
                    UpdatedAt: DateTimeOffset.UtcNow),
            };
        }
    }
}
