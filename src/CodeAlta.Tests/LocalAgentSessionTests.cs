using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentSessionTests
{
    [TestMethod]
    public void LocalAgentInstructionComposer_ComposesDeveloperInstructionsAndAgentFiles()
    {
        using var temp = TestTempDirectory.Create();
        var repoRoot = Path.Combine(temp.Path, "repo");
        var projectRoot = Path.Combine(repoRoot, "src", "Project");
        var workingDirectory = Path.Combine(projectRoot, "Nested");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(repoRoot, "AGENTS.md"), "root instructions");
        File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "project instructions");

        var bundle = LocalAgentInstructionComposer.Compose(
            new AgentSessionCreateOptions
            {
                Model = "gpt-5.4",
                WorkingDirectory = workingDirectory,
                ProjectRoots = [projectRoot],
                SystemMessage = " system guidance ",
                DeveloperInstructions = " developer guidance ",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        Assert.AreEqual("system guidance", bundle.SystemMessage);
        Assert.IsNotNull(bundle.DeveloperInstructions);
        StringAssert.Contains(bundle.DeveloperInstructions, "developer guidance");
        StringAssert.Contains(bundle.DeveloperInstructions, Path.Combine(repoRoot, "AGENTS.md"));
        StringAssert.Contains(bundle.DeveloperInstructions, "root instructions");
        StringAssert.Contains(bundle.DeveloperInstructions, Path.Combine(projectRoot, "AGENTS.md"));
        StringAssert.Contains(bundle.DeveloperInstructions, "project instructions");
        Assert.AreEqual(64, bundle.InstructionHash.Length);
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_RunsToolLoopAndPersistsReplayableEvents()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-1");
        var state = CreateState("session-1");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var toolInvocations = new List<AgentToolInvocation>();
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                async (request, onUpdate, cancellationToken) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                    await onUpdate(
                            new LocalAgentTurnDelta
                            {
                                Kind = AgentContentKind.Assistant,
                                ContentId = "assistant-1",
                                Text = "thinking",
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    using var arguments = JsonDocument.Parse("""{"path":"sample.txt"}""");
                    return new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [
                                new LocalAgentMessagePart.Text("Need to inspect a file."),
                                new LocalAgentMessagePart.ToolCall("call-1", "inspect_file", arguments.RootElement.Clone()),
                            ]),
                        AssistantPartContentIds = ["assistant-1", null],
                        Usage = CreateUsageSnapshot(10, 5),
                        ProviderSessionId = "resp_123",
                    };
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.Tool, request.Conversation[^1].Role);
                    var toolResult = Assert.IsInstanceOfType<LocalAgentMessagePart.ToolResult>(request.Conversation[^1].Parts.Single());
                    StringAssert.Contains(Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value, "sample.txt");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Done inspecting the file.")]),
                            Usage = CreateUsageSnapshot(18, 9),
                            ProviderSessionId = "resp_124",
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                Tools =
                [
                    new AgentToolDefinition(
                        new AgentToolSpec("inspect_file", "Inspect a file", schema.RootElement.Clone()),
                        async (invocation, cancellationToken) =>
                        {
                            toolInvocations.Add(invocation);
                            if (invocation.Progress is not null)
                            {
                                await invocation.Progress(new AgentToolProgressUpdate("opening sample.txt" + Environment.NewLine), cancellationToken).ConfigureAwait(false);
                            }

                            return new AgentToolResult(true, [new AgentToolResultItem.Text($"inspected {invocation.Arguments.GetProperty("path").GetString()}")]);
                        }),
                ],
            });

        var runId = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Inspect sample.txt"),
                }).ConfigureAwait(false);

        Assert.IsFalse(string.IsNullOrWhiteSpace(runId.Value));
        Assert.AreEqual(1, toolInvocations.Count);
        Assert.AreEqual("inspect_file", toolInvocations[0].ToolName);
        Assert.AreEqual("sample.txt", toolInvocations[0].Arguments.GetProperty("path").GetString());

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.userMessage"));
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.assistantMessage"));
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.toolMessage"));
        Assert.IsTrue(history.OfType<AgentContentDeltaEvent>().Any(evt => evt.RunId == runId && evt.ContentId == "assistant-1"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(evt =>
            evt.RunId == runId &&
            evt.Kind == AgentContentKind.Assistant &&
            evt.ContentId == "assistant-1" &&
            evt.Content == "Need to inspect a file."));
        Assert.IsTrue(history.OfType<AgentContentDeltaEvent>().Any(evt =>
            evt.RunId == runId &&
            evt.Kind == AgentContentKind.ToolOutput &&
            evt.ContentId == "call-1:output" &&
            evt.ParentActivityId == "call-1" &&
            evt.Delta.Contains("opening sample.txt", StringComparison.Ordinal)));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static evt => evt.Kind == AgentContentKind.ToolOutput && evt.Content.Contains("inspected sample.txt", StringComparison.Ordinal)));
        Assert.AreEqual(2, history.OfType<AgentActivityEvent>().Count(static evt => evt.ActivityId == "call-1" && (evt.Phase == AgentActivityPhase.Requested || evt.Phase == AgentActivityPhase.Started)));
        Assert.IsTrue(history.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.Idle));

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsFalse(persistedHistory.OfType<AgentContentDeltaEvent>().Any());
        Assert.IsTrue(persistedHistory.OfType<AgentContentCompletedEvent>().Any(static evt => evt.Kind == AgentContentKind.Assistant));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_124", persistedState.ProviderSessionId);
    }

    [TestMethod]
    public async Task LocalAgentSession_ReplaysPersistedConversationOnResume()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-replay");
        var state = CreateState("session-replay");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var firstExecutor = new ScriptedTurnExecutor(
            (_, _, _) =>
            {
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Initial answer.")]),
                    });
            });

        await using (var firstSession = new LocalAgentSession(
                         AgentBackendIds.OpenAIResponses,
                         provider,
                         summary,
                         state,
                         [],
                         store,
                         firstExecutor,
                         CreateOptions(provider, temp.Path)))
        {
            _ = await firstSession.SendAsync(
                    new AgentSendOptions
                    {
                        Input = AgentInput.Text("First prompt"),
                    }).ConfigureAwait(false);
        }

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);

        var replayExecutor = new ScriptedTurnExecutor(
            (request, _, _) =>
            {
                Assert.AreEqual(3, request.Conversation.Count);
                Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                Assert.AreEqual(LocalAgentConversationRole.Assistant, request.Conversation[1].Role);
                Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[2].Role);
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Follow-up answer.")]),
                    });
            });

        await using var resumedSession = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            persistedState,
            persistedHistory,
            store,
            replayExecutor,
            CreateOptions(provider, temp.Path));

        _ = await resumedSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Second prompt"),
                }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_ResolvesEquivalentModelIdsForUsageLimits()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-model-alias");
        var state = CreateState("session-model-alias");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new AliasAwareTurnExecutor(),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = AgentInput.Text("Hello"),
        }).ConfigureAwait(false);

        var usageEvent = (await session.GetHistoryAsync().ConfigureAwait(false))
            .OfType<AgentSessionUpdateEvent>()
            .Single(static evt => evt.Kind == AgentSessionUpdateKind.UsageUpdated);

        Assert.AreEqual(1050000L, usageEvent.Usage?.TokenLimit);
        Assert.AreEqual(AgentUsageScope.CurrentWindow, usageEvent.Usage?.Scope);
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_PersistsCheckpointAndReplaysFromCheckpointPlusSuffix()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-compact");
        var state = CreateState("session-compact");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using (var session = new LocalAgentSession(
                         AgentBackendIds.OpenAIResponses,
                         provider,
                         summary,
                         state,
                         [],
                         store,
                         new ScriptedTurnExecutor(
                             (_, _, _) => Task.FromResult(
                                 new LocalAgentTurnResponse
                                 {
                                     AssistantMessage = new LocalAgentConversationMessage(
                                         LocalAgentConversationRole.Assistant,
                                         [new LocalAgentMessagePart.Text("First answer " + new string('a', 120))]),
                                 }),
                             (_, _, _) => Task.FromResult(
                                 new LocalAgentTurnResponse
                                 {
                                     AssistantMessage = new LocalAgentConversationMessage(
                                         LocalAgentConversationRole.Assistant,
                                         [new LocalAgentMessagePart.Text("Second answer " + new string('b', 120))]),
                                 })),
                         CreateOptions(provider, temp.Path)))
        {
            _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 140)) }).ConfigureAwait(false);
            _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 140)) }).ConfigureAwait(false);

            var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.Success);
        }

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Single(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.KeptMessages.Count >= 1);
        Assert.IsTrue(checkpoint.SummarizedMessageCount >= 1);

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionEventOffset);
        Assert.AreEqual("manual", persistedState.LastCompactionTrigger);
        Assert.AreEqual(checkpoint.ContentId, persistedState.CompactionCheckpointEventId);

        await using var resumedSession = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            persistedState,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsTrue(request.Conversation.Count >= 3);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                        "## Objective");
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[^1].Role);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Post-compaction answer.")]),
                        });
                }),
            CreateOptions(provider, temp.Path));

        _ = await resumedSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Third prompt"),
                }).ConfigureAwait(false);
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_ThresholdPreparation_ProtectsLatestUserMessage()
    {
        var instructionBundle = LocalAgentInstructionComposer.Compose(
            new AgentSessionCreateOptions
            {
                ProviderKey = "openai",
                Model = "gpt-5.4",
                WorkingDirectory = Environment.CurrentDirectory,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });
        var firstUserMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text("First prompt " + new string('x', 420))]);
        var firstAssistantMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Assistant,
            [new LocalAgentMessagePart.Text(new string('A', 420))]);
        var secondUserMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text("Second prompt " + new string('y', 40))]);
        var conversation = new[] { firstUserMessage, firstAssistantMessage, secondUserMessage };
        var estimatedPromptTokens = LocalAgentTokenEstimator.EstimatePromptTokens(
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            conversation,
            usage: null).Tokens;
        var fixedTokens = LocalAgentTokenEstimator.EstimatePromptTokens(
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            [],
            usage: null).Tokens;
        var anchorTokens = LocalAgentTokenEstimator.EstimateMessage(secondUserMessage);
        var usablePromptBudget = (fixedTokens + 64 + anchorTokens + 20) * 2L;
        var triggerThreshold = Math.Max(0.20d, Math.Min(0.95d, (estimatedPromptTokens - 1d) / usablePromptBudget));

        var preparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: usablePromptBudget + 20 + 10,
                InputTokenLimit: usablePromptBudget,
                OutputTokenLimit: 128,
                UsablePromptBudget: usablePromptBudget,
                ReservedOutputTokens: 20,
                ReservedOverheadTokens: 10),
            new LocalAgentCompactionSettings(
                Enabled: true,
                TriggerThreshold: triggerThreshold,
                TargetThreshold: 0.50,
                ReservedOutputTokens: 20,
                ReservedOverheadTokens: 10,
                KeepLastUserMessage: true,
                AllowSplitTurn: true),
            anchorContentId: "user:2");

        Assert.IsNotNull(preparation);
        Assert.AreEqual("user:2", preparation!.AnchorContentId);
        Assert.IsTrue(preparation.MessagesToSummarize.Count >= 1);
        Assert.IsTrue(preparation.MessagesToKeep.Count >= 1);
        Assert.AreEqual(secondUserMessage, preparation.MessagesToKeep[0]);
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_OverflowCompactsAndRetriesOnce()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(new LocalAgentCompactionSettings(
            Enabled: true,
            TriggerThreshold: 0.95,
            TargetThreshold: 0.50,
            ReservedOutputTokens: 10,
            ReservedOverheadTokens: 10,
            KeepLastUserMessage: true,
            AllowSplitTurn: true));
        var summary = CreateSummary("session-overflow");
        var state = CreateState("session-overflow");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var attempt = 0;
        var executor = new ScriptedTurnExecutor(
            (_, _, _) => Task.FromResult(
                new LocalAgentTurnResponse
                {
                    AssistantMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Assistant,
                        [new LocalAgentMessagePart.Text("Initial answer " + new string('a', 80))]),
                }),
            (request, _, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new LocalAgentTurnExecutionException(new LocalAgentTurnFailure("maximum context length exceeded", IsContextOverflow: true));
                }

                Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                StringAssert.Contains(
                    Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                    "codealta-compaction-checkpoint");
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Recovered after overflow.")]),
                        Usage = CreateUsageSnapshot(40, 20),
                    });
            },
            (request, _, _) =>
            {
                attempt++;
                Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                StringAssert.Contains(
                    Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                    "codealta-compaction-checkpoint");
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Recovered after overflow.")]),
                        Usage = CreateUsageSnapshot(40, 20),
                    });
            });

        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            executor,
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Prompt one " + new string('x', 100)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Prompt two " + new string('y', 90)) }).ConfigureAwait(false);

        Assert.AreEqual(2, attempt);
        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("overflow", persistedState.LastCompactionTrigger);
    }

    private static AgentSessionCreateOptions CreateOptions(LocalAgentProviderDescriptor provider, string workingDirectory)
    {
        return new AgentSessionCreateOptions
        {
            ProviderKey = provider.ProviderKey,
            Model = "gpt-5.4",
            WorkingDirectory = workingDirectory,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
    }

    private static LocalAgentProviderDescriptor CreateProvider(LocalAgentCompactionSettings? compaction = null)
    {
        return new LocalAgentProviderDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            BackendId = AgentBackendIds.OpenAIResponses,
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
            Profile = new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            Compaction = compaction ?? LocalAgentCompactionSettings.Default,
        };
    }

    private static LocalAgentSessionSummary CreateSummary(string sessionId)
    {
        return new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = AgentBackendIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\repo",
            CreatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
        };
    }

    private static LocalAgentSessionState CreateState(string sessionId)
    {
        return new LocalAgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
        };
    }

    private static AgentSessionUsage CreateUsageSnapshot(long inputTokens, long outputTokens)
    {
        return new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                InputTokens: inputTokens,
                OutputTokens: outputTokens),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.RecoveredHistory,
            UpdatedAt: DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"));
    }

    private sealed class ScriptedTurnExecutor : ILocalAgentTurnExecutor
    {
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly Queue<Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>> _steps;

        public ScriptedTurnExecutor(params Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>[] steps)
            : this(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                steps)
        {
        }

        public ScriptedTurnExecutor(
            IReadOnlyList<AgentModelInfo> models,
            params Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>[] steps)
        {
            _models = models;
            _steps = new Queue<Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>>(steps);
        }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            LocalAgentProviderDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_models);

        public Task<LocalAgentTurnResponse> ExecuteTurnAsync(
            LocalAgentTurnRequest request,
            Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            if (!_steps.TryDequeue(out var step))
            {
                throw new InvalidOperationException("No scripted turn step remained.");
            }

            return step(request, onUpdate, cancellationToken);
        }
    }

    private sealed class AliasAwareTurnExecutor : ILocalAgentTurnExecutor
    {
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            LocalAgentProviderDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo(
                    "gpt-5.4-2026-03-05",
                    DisplayName: "GPT-5.4",
                    Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["contextWindow"] = 1050000L,
                    }),
            ]);

        public Task<LocalAgentTurnResponse> ExecuteTurnAsync(
            LocalAgentTurnRequest request,
            Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            var usage = LocalAgentUsageFactory.CreateOperationUsage(
                modelId: "gpt-5.4-2026-03-05",
                modelInfo: request.ModelInfo,
                inputTokens: 1200,
                outputTokens: 50,
                totalTokens: 1250,
                cachedInputTokens: null,
                reasoningTokens: null,
                updatedAt: DateTimeOffset.UtcNow);

            return Task.FromResult(
                new LocalAgentTurnResponse
                {
                    AssistantMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Assistant,
                        [new LocalAgentMessagePart.Text("Done.")]),
                    Usage = usage,
                });
        }
    }
}
