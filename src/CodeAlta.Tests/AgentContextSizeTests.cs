using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentContextSizeTests
{
    [TestMethod]
    public void Choices_AreImmutableExactAndDefaultIsAlwaysSupported()
    {
        long[] input = [50_000, 10_000, 50_000];
        var sizes = new AgentModelContextSizes(10_000, input);
        input[0] = 99_999;
        CollectionAssert.AreEqual(new long[] { 10_000, 50_000 }, sizes.TokenCounts.ToArray());
        Assert.IsTrue(sizes.Supports(null));
        Assert.IsTrue(sizes.Supports(50_000));
        Assert.IsFalse(sizes.Supports(30_000));
        Assert.IsFalse(sizes.Supports(0));
        Assert.IsFalse(sizes.Supports(-1));
        Assert.IsEmpty(new AgentModelContextSizes(null, [50_000]).TokenCounts);
        Assert.IsEmpty(new AgentModelContextSizes(10_000, [10_000, -1]).TokenCounts);
        Assert.IsEmpty(new AgentModelContextSizes(10_000, [50_000]).TokenCounts);
        Assert.IsEmpty(new AgentModelContextSizes(10_000, []).TokenCounts);
        Assert.IsNull(new AgentModelContextSizes(long.MaxValue, [long.MaxValue]).DefaultTokens);
        Assert.IsEmpty(new AgentModelContextSizes(10_000, [10_000, 9_007_199_254_740_992]).TokenCounts);
        Assert.IsTrue(new AgentModelContextSizes(10_000, [10_000, AgentModelContextSizes.MaximumTokens]).Supports(AgentModelContextSizes.MaximumTokens));
    }

    [TestMethod]
    public void Selection_ResolvesSessionThenPreferenceThenDefaultWithoutClamping()
    {
        var model = Model();
        Assert.AreEqual(10_000L, model.ContextSizes!.Resolve(10_000, 50_000));
        Assert.AreEqual(50_000L, model.ContextSizes.Resolve(null, 50_000));
        Assert.AreEqual(50_000L, model.ContextSizes.Resolve(40_000, 50_000));
        Assert.AreEqual(10_000L, model.ContextSizes.Resolve(40_000, 40_000));
        Assert.AreEqual(50_000L, AgentModelContextSizes.DefaultCapacity(model, 50_000));
        Assert.AreEqual(Budget(model, null, 50_000), Budget(model, 50_000, null));
        Assert.AreEqual(Budget(model, null, null), Budget(model, 10_000, null));
        Assert.AreEqual(10_000L, AgentModelContextSizes.DefaultCapacity(model with { ContextSizes = null }, 50_000));
        Assert.IsNull(AgentModelContextSizes.DefaultCapacity(null, 50_000));
        Assert.IsNull(new AgentContextSelection(null).Tokens);
    }

    [TestMethod]
    public void Selection_RequiresExactModelAndProvider_NotDisplayNamesOrDatedAliases()
    {
        var model = Model();
        foreach (var (id, provider) in new[] { ("context-test-2026-10-29", "codex"), ("CONTEXT-TEST", "codex"), ("context-test", "other") })
        {
            var resolved = AgentModelContextResolver.Resolve(model, id, provider, 50_000, 50_000);
            Assert.IsNull(resolved!.ContextSizes);
            Assert.AreEqual(10_000L, AgentTokenBudgetResolver.Resolve(resolved, AgentCompactionSettings.Default).TotalContextEnvelope);
        }
    }

    [TestMethod]
    public void Budget_ReservesOutputAndRetainsIndependentHardInputCeilings()
    {
        var model = Model(new Dictionary<string, object?>
        {
            ["contextWindow"] = 10_000L, ["inputTokenLimit"] = 30_000L, ["outputTokenLimit"] = 2_000L,
        });
        Assert.AreEqual(8_000L, Budget(model, 10_000, null).InputContextLimit);
        Assert.AreEqual(30_000L, Budget(model, 50_000, null).InputContextLimit);
        Assert.AreEqual(2_000L, Budget(model, 50_000, null).MaxOutputTokens);
        Assert.AreEqual(10_000L, model.Capabilities!["contextWindow"]);
    }

    [TestMethod]
    public void Budget_CodexHeadroomAndCompactionThresholdHaveDistinctBases()
    {
        var model = new AgentModelInfo("context-test", Provider: "codex", Capabilities: new Dictionary<string, object?>
        {
            ["contextWindow"] = 272_000L,
            ["outputTokenLimit"] = 128_000L,
            [AgentTokenBudgetResolver.EffectiveContextPercentKey] = 95L,
            [AgentTokenBudgetResolver.ContextCompactionRatioKey] = 0.90,
            [AgentTokenBudgetResolver.AutoCompactTokenLimitKey] = 250_000L,
        }) { ContextSizes = new(272_000, [272_000, 872_000]) };
        var standard = AgentModelContextResolver.Resolve(model, model.Id, "codex", null, null)!;
        var expanded = AgentModelContextResolver.Resolve(model, model.Id, "codex", 872_000, null)!;
        Assert.AreEqual(258_400L, AgentTokenBudgetResolver.Resolve(standard, AgentCompactionSettings.Default).InputContextLimit);
        Assert.AreEqual(828_400L, AgentTokenBudgetResolver.Resolve(expanded, AgentCompactionSettings.Default).InputContextLimit);
        Assert.AreEqual(244_800L, AgentTokenBudgetResolver.ResolveCompactionThreshold(258_400, standard, AgentCompactionSettings.Default));
        Assert.AreEqual(250_000L, AgentTokenBudgetResolver.ResolveCompactionThreshold(828_400, expanded, AgentCompactionSettings.Default));
        Assert.AreEqual(129_200L, AgentTokenBudgetResolver.ResolveCompactionThreshold(258_400, standard, AgentCompactionSettings.Default with { Ratio = 0.5 }));
    }

    [TestMethod]
    public void Enrichment_PreservesVerifiedChoicesWithoutTurningFallbackDefaultsIntoHardLimits()
    {
        var model = Model();
        var enriched = AgentModelMetadataEnricher.EnrichModel(model, null, null, null,
            [new AgentModelKnownLimit("context-test", 8_000, 10_000, 0.90)]);
        Assert.AreSame(model.ContextSizes, enriched.ContextSizes);
        Assert.AreEqual(49_000L, Budget(enriched, 50_000, null).InputContextLimit);
        var overridden = AgentModelMetadataEnricher.EnrichModel(model, null, null,
            new Dictionary<string, AgentModelOverride> { [model.Id] = new() { InputTokenLimit = 20_000 } });
        Assert.IsEmpty(overridden.ContextSizes!.TokenCounts);
        Assert.AreEqual(9_000L, Budget(overridden, 50_000, null).InputContextLimit);
        var lowerInput = AgentModelMetadataEnricher.EnrichModel(model, null, null,
            new Dictionary<string, AgentModelOverride> { [model.Id] = new() { InputTokenLimit = 5_000 } });
        Assert.AreEqual(5_000L, Budget(lowerInput, 50_000, null).InputContextLimit);
        var overriddenContext = AgentModelMetadataEnricher.EnrichModel(model, null, null,
            new Dictionary<string, AgentModelOverride> { [model.Id] = new() { ContextWindowTokens = 20_000 } });
        Assert.IsEmpty(overriddenContext.ContextSizes!.TokenCounts);
        Assert.AreEqual(20_000L, AgentModelContextSizes.DefaultCapacity(overriddenContext, 50_000));
        var unsupported = AgentModelMetadataEnricher.EnrichModel(model with { ContextSizes = null }, null, null,
            new Dictionary<string, AgentModelOverride> { [model.Id] = new() { ContextWindowTokens = 999_999 } });
        Assert.IsNull(unsupported.ContextSizes);
    }

    [TestMethod]
    public void ModelJson_RoundTripsChoicesWithoutPersistingSessionBudget()
    {
        var model = AgentModelContextResolver.Resolve(Model(), "context-test", "codex", 50_000, null)!;
        var json = JsonSerializer.Serialize(model, AgentJsonSerializerContext.Default.AgentModelInfo);
        var restored = JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentModelInfo)!;
        CollectionAssert.AreEqual(new long[] { 10_000, 50_000 }, restored.ContextSizes!.TokenCounts.ToArray());
        Assert.AreEqual(10_000L, restored.ContextSizes.DefaultTokens);
        Assert.IsNull(restored.EffectiveContextWindowTokens);
        Assert.IsNull(JsonSerializer.Deserialize("""{"id":"old-model"}""", AgentJsonSerializerContext.Default.AgentModelInfo)!.ContextSizes);
    }

    [TestMethod]
    public void Usage_ReplacesOldCapacityWithoutLosingCountsOrOperation()
    {
        var usage = new AgentSessionUsage(
            Window: new(8_000, 49_000, 12, TotalContextEnvelope: 50_000, MaxOutputTokens: 999),
            LastOperation: new(InputTokens: 7_800, OutputTokens: 200, CachedInputTokens: 5_000),
            Source: AgentUsageSource.ProviderUsage, UpdatedAt: DateTimeOffset.UnixEpoch);
        var selected = AgentModelContextResolver.Resolve(Model(), "context-test", "codex", 10_000, null)!;
        var attached = AgentUsageFactory.AttachModelInfo(usage, selected)!;
        Assert.AreEqual(9_000L, attached.TokenLimit);
        Assert.AreEqual(10_000L, attached.Window!.TotalContextEnvelope);
        Assert.AreEqual(1_000L, attached.Window.MaxOutputTokens);
        Assert.AreEqual(usage.CurrentTokens, attached.CurrentTokens);
        Assert.AreEqual(usage.MessageCount, attached.MessageCount);
        Assert.AreEqual(usage.LastOperation, attached.LastOperation);
        Assert.AreEqual(usage.UpdatedAt, attached.UpdatedAt);
        var estimate = AgentUsageFactory.AttachWindowEstimate(usage, selected, 8_100, 13, DateTimeOffset.UtcNow)!;
        Assert.AreEqual(9_000L, estimate.TokenLimit);
        Assert.AreEqual(usage.LastOperation, estimate.LastOperation);
    }

    [TestMethod]
    public async Task Sessions_IsolateSelectionsAndReattachRefreshesCapacityBeforeSending()
    {
        using var temp = TestTempDirectory.Create();
        var model = Model();
        var executor = new ContextExecutor(model);
        await using var runtime = Runtime(temp.Path, executor);
        await runtime.ListModelsAsync();
        string sessionId;
        await using (var large = await runtime.CreateSessionAsync(Options(temp.Path, 50_000)))
        {
            sessionId = large.SessionId;
            await large.SendAsync(new() { Input = AgentInput.Text("one") });
            await using var small = await runtime.CreateSessionAsync(Options(temp.Path, 10_000));
            await small.SendAsync(new() { Input = AgentInput.Text("two") });
            await large.SendAsync(new() { Input = AgentInput.Text("three") });
        }

        CollectionAssert.AreEqual(new long?[] { 50_000, 10_000, 50_000 },
            executor.Requests.Select(request => AgentTokenBudgetResolver.Resolve(request.ModelInfo, AgentCompactionSettings.Default).TotalContextEnvelope).ToArray());
        var before = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!.Usage!;
        await using var resumed = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(temp.Path, null));
        var after = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!.Usage!;
        Assert.AreEqual(9_000L, after.TokenLimit);
        Assert.AreEqual(10_000L, after.Window!.TotalContextEnvelope);
        Assert.AreEqual(before.CurrentTokens, after.CurrentTokens);
        Assert.AreEqual(before.LastOperation, after.LastOperation);
        Assert.AreEqual(10_000L, model.Capabilities!["contextWindow"]);
        Assert.IsNull(model.EffectiveContextWindowTokens);
    }

    [TestMethod]
    public async Task SmallerSelection_CompactsRetainedHistoryBeforeNextOrdinaryRequest()
    {
        using var temp = TestTempDirectory.Create();
        var executor = new ContextExecutor(Model());
        await using var runtime = Runtime(temp.Path, executor);
        await runtime.ListModelsAsync();
        string sessionId;
        await using (var session = await runtime.CreateSessionAsync(Options(temp.Path, 50_000)))
        {
            sessionId = session.SessionId;
            await session.SendAsync(new() { Input = AgentInput.Text(string.Join(' ', Enumerable.Repeat("history", 12_000))) });
        }

        Assert.AreEqual(1, executor.Requests.Count, "The large budget must not compact the initial request.");
        await using var resumed = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(temp.Path, 10_000));
        await resumed.SendAsync(new() { Input = AgentInput.Text("Continue the task.") });
        var subsequent = executor.Requests.Skip(1).ToArray();
        Assert.IsGreaterThan(1, subsequent.Length);
        Assert.IsTrue(ContextExecutor.IsSummary(subsequent[0]), "Compaction must precede the ordinary request.");
        var ordinary = subsequent.First(request => !ContextExecutor.IsSummary(request));
        Assert.AreEqual(9_000L, AgentTokenBudgetResolver.Resolve(ordinary.ModelInfo, AgentCompactionSettings.Default).InputContextLimit);
        Assert.IsTrue(ordinary.Conversation.Any(message => message.Parts.OfType<AgentMessagePart.Text>().Any(text => text.Value.Contains("Continue the task.", StringComparison.Ordinal))));
        var history = await resumed.GetHistoryAsync();
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(item => item.BackendEventType == "local.compactionCheckpoint"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(50_000L)]
    public async Task Reattach_WithoutDiscoveryClearsStaleCapacityIncludingDefaultButKeepsCounts(long? tokens)
    {
        using var temp = TestTempDirectory.Create();
        var executor = new ContextExecutor(Model());
        await using var runtime = Runtime(temp.Path, executor);
        string sessionId;
        await using (var session = await runtime.CreateSessionAsync(Options(temp.Path, 50_000)))
        {
            sessionId = session.SessionId;
            await session.SendAsync(new() { Input = AgentInput.Text("Retain usage.") });
        }

        // Only the session has discovered the catalog; the runtime has no cache and must not discover on resume.
        var before = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!.Usage!;
        await using var resumed = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(temp.Path, tokens));
        var after = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!.Usage!;
        Assert.IsNull(after.TokenLimit);
        Assert.IsNull(after.Window!.TotalContextEnvelope);
        Assert.AreEqual(before.CurrentTokens, after.CurrentTokens);
        Assert.AreEqual(before.LastOperation, after.LastOperation);
        Assert.AreEqual(1, executor.Requests.Count);
    }

    private static AgentModelInfo Model(IReadOnlyDictionary<string, object?>? capabilities = null)
        => new("context-test", Provider: "codex", Capabilities: capabilities ?? new Dictionary<string, object?>
        {
            ["contextWindow"] = 10_000L, ["outputTokenLimit"] = 1_000L,
        }) { ContextSizes = new(10_000, [10_000, 50_000]) };

    private static AgentTokenBudget Budget(AgentModelInfo model, long? session, long? preference)
        => AgentTokenBudgetResolver.Resolve(AgentModelContextResolver.Resolve(model, model.Id, "codex", session, preference), AgentCompactionSettings.Default);

    private static AgentRuntime Runtime(string root, ContextExecutor executor)
        => new(ModelProviderIds.OpenAIResponses, "Synthetic context runtime", new AgentRuntimeOptions
        {
            StateRootPath = root,
            Providers = [new()
            {
                Provider = new()
                {
                    ProviderKey = "codex", ProtocolFamily = "openai-responses", DisplayName = "Synthetic",
                    TransportKind = AgentTransportKind.OpenAIResponses, Profile = new() { SupportsDeveloperRole = true },
                    Compaction = AgentCompactionSettings.Default,
                },
                TurnExecutor = executor,
            }],
        });

    private static AgentSessionCreateOptions Options(string root, long? tokens) => new()
    {
        ProviderKey = "codex", Model = "context-test", WorkingDirectory = root,
        ContextWindowTokens = tokens, InstructionsAlreadyComposed = true,
        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
    };

    private static AgentSessionResumeOptions ResumeOptions(string root, long? tokens) => new()
    {
        ProviderKey = "codex", Model = "context-test", WorkingDirectory = root,
        ContextWindowTokens = tokens, InstructionsAlreadyComposed = true,
        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
    };

    private sealed class ContextExecutor(AgentModelInfo model) : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        public List<AgentTurnRequest> Requests { get; } = [];
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([model]);

        public static bool IsSummary(AgentTurnRequest request)
            => request.SystemMessage?.Contains("CodeAlta compaction", StringComparison.Ordinal) == true ||
               request.SystemMessage?.Contains("CodeAlta oversized-anchor", StringComparison.Ordinal) == true;

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var text = IsSummary(request)
                ? "## Objective\nContinue the task.\n## Progress\nHistory retained in this summary.\n## Next Steps\nContinue the task."
                : "Done.";
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
            });
        }
    }
}
