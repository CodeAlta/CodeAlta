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
                             [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                             (request, _, _) =>
                             {
                                 Assert.AreEqual(1, request.Conversation.Count);
                                 Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                                 var payload = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                                 StringAssert.Contains(payload, "<conversation>");
                                 return Task.FromResult(
                                     new LocalAgentTurnResponse
                                     {
                                         AssistantMessage = new LocalAgentConversationMessage(
                                             LocalAgentConversationRole.Assistant,
                                             [new LocalAgentMessagePart.Text(
                                                 """
                                                 ## Objective
                                                 Continue the coding task.
                                                 ## Active User Request
                                                 Second prompt
                                                 ## Constraints
                                                 - Preserve behavior.
                                                 ## Progress
                                                 ### Done
                                                 - First answer captured.
                                                 ### In Progress
                                                 - Working on the second prompt.
                                                 ### Blocked
                                                 - None recorded.
                                                 ## Decisions
                                                 - Use checkpoints.
                                                 ## Next Steps
                                                 - Continue from the retained suffix.
                                                 ## Critical Context
                                                 - Keep recent context verbatim.
                                                 ## Relevant Files
                                                 - None tracked.
                                                 """)]),
                                     });
                             },
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
                AllowSplitTurn: true)
            {
                RecentSuffixTargetTokens = (int)(fixedTokens + 64 + anchorTokens + 20),
            },
            anchorContentId: "user:2");

        Assert.IsNotNull(preparation);
        Assert.AreEqual("user:2", preparation!.AnchorContentId);
        Assert.IsTrue(preparation.MessagesToSummarize.Count >= 1);
        Assert.IsTrue(preparation.MessagesToKeep.Count >= 1);
        Assert.AreEqual(secondUserMessage, preparation.MessagesToKeep[0]);
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_Preparation_KeepsContiguousNewestSuffix()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("u1 " + new string('a', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("a1")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("u2 " + new string('b', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("a2")]),
        };

        var lastMessageTokens = LocalAgentTokenEstimator.EstimateMessage(conversation[^1]);
        var preparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: 2000,
                InputTokenLimit: 500,
                OutputTokenLimit: 128,
                UsablePromptBudget: 500,
                ReservedOutputTokens: 32,
                ReservedOverheadTokens: 32),
            new LocalAgentCompactionSettings(
                Enabled: true,
                TriggerThreshold: 0.80,
                TargetThreshold: 0.50,
                ReservedOutputTokens: 32,
                ReservedOverheadTokens: 32,
                KeepLastUserMessage: false,
                AllowSplitTurn: true),
            checkpointTokenEstimate: 64,
            promptBudgetOverride: lastMessageTokens + 96);

        Assert.IsNotNull(preparation);
        CollectionAssert.AreEqual(new[] { conversation[^1] }, preparation!.MessagesToKeep.ToArray());
        CollectionAssert.AreEqual(
            new[] { conversation[0], conversation[1], conversation[2] },
            preparation.MessagesToSummarize.ToArray());
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_Preparation_ThrowsWhenSplitTurnDisabled()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("First prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("First answer")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Latest prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text(new string('x', 480))]),
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: 2000,
                InputTokenLimit: 300,
                OutputTokenLimit: 128,
                UsablePromptBudget: 300,
                ReservedOutputTokens: 32,
                ReservedOverheadTokens: 32),
            new LocalAgentCompactionSettings(
                Enabled: true,
                TriggerThreshold: 0.80,
                TargetThreshold: 0.50,
                ReservedOutputTokens: 32,
                ReservedOverheadTokens: 32,
                KeepLastUserMessage: true,
                AllowSplitTurn: false),
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 120));
    }

    [TestMethod]
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_PrefersRecentHighSignalToolOutput()
    {
        using var oldArgs = JsonDocument.Parse("""{"path":"old.log"}""");
        using var recentArgs = JsonDocument.Parse("""{"command":"dotnet test"}""");
        var oldAssistant = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Assistant,
            [
                new LocalAgentMessagePart.Reasoning("Old exploratory reasoning that should be cheaper to omit."),
                new LocalAgentMessagePart.ToolCall("call-old", "read_file", oldArgs.RootElement.Clone()),
            ]);
        var oldTool = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Tool,
            [
                new LocalAgentMessagePart.ToolResult(
                    "call-old",
                    new AgentToolResult(true, [new AgentToolResultItem.Text("old output marker " + new string('a', 400))])),
            ]);
        var recentAssistant = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Assistant,
            [
                new LocalAgentMessagePart.Text("Recent verification."),
                new LocalAgentMessagePart.ToolCall("call-new", "shell_command", recentArgs.RootElement.Clone()),
            ]);
        var recentTool = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Tool,
            [
                new LocalAgentMessagePart.ToolResult(
                    "call-new",
                    new AgentToolResult(
                        false,
                        [new AgentToolResultItem.Text("build failed" + Environment.NewLine + "CS1591: Missing XML comment")],
                        Error: "build failed")),
            ]);

        var preparation = new LocalAgentCompactionPreparation(
            Trigger: LocalAgentCompactionTrigger.Threshold,
            MessagesToSummarize: [oldAssistant, oldTool],
            TurnPrefixMessages: [],
            MessagesToKeep: [recentAssistant, recentTool],
            AnchorContentId: "user:latest",
            IsSplitTurn: false,
            TokensBefore: new LocalAgentTokenEstimate(1000, "test", IsEstimated: true),
            PreviousSummary: null);
        var settings = LocalAgentCompactionSettings.Default with
        {
            ToolResultCharsPerItem = 80,
            ToolResultCharsTotal = 90,
            ReasoningCharsPerItem = 40,
            ReasoningCharsTotal = 40,
        };

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Finish the fix",
            readFiles: [],
            modifiedFiles: [],
            settings);

        StringAssert.Contains(result.UserMessage, "build failed");
        StringAssert.Contains(result.UserMessage, "callId=call-old");
        Assert.IsTrue(result.Statistics.OmittedToolResultCount >= 1);
        StringAssert.Contains(result.UserMessage, "[Assistant reasoning summary]");
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_Preparation_UsesRecentSuffixTargetTokens()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("u1 " + new string('a', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("a1 " + new string('b', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("u2 " + new string('c', 220))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("a2 " + new string('d', 220))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("u3")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("a3")]),
        };

        var retainedTarget = LocalAgentTokenEstimator.EstimateMessage(conversation[^1]) + LocalAgentTokenEstimator.EstimateMessage(conversation[^2]) + 32;
        var preparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: 4096,
                InputTokenLimit: 4096,
                OutputTokenLimit: 512,
                UsablePromptBudget: 4096,
                ReservedOutputTokens: 64,
                ReservedOverheadTokens: 64),
            (LocalAgentCompactionSettings.Default with
            {
                RecentSuffixTargetTokens = (int)(retainedTarget + 64),
                KeepLastUserMessage = false,
            }),
            checkpointTokenEstimate: 64);

        Assert.IsNotNull(preparation);
        Assert.AreEqual(2, preparation!.MessagesToKeep.Count);
        Assert.AreEqual(LocalAgentConversationRole.User, preparation.MessagesToKeep[0].Role);
        Assert.AreEqual(LocalAgentConversationRole.Assistant, preparation.MessagesToKeep[1].Role);
        StringAssert.Contains(Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(preparation.MessagesToKeep[0].Parts.Single()).Value, "u3");
        StringAssert.Contains(Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(preparation.MessagesToKeep[1].Parts.Single()).Value, "a3");
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_Preparation_ReducesOversizedLatestUserAnchorWhenConfigured()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Earlier prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Earlier answer")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Huge prompt " + new string('x', 2200))]),
        };

        var preparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: 800,
                InputTokenLimit: 400,
                OutputTokenLimit: 128,
                UsablePromptBudget: 280,
                ReservedOutputTokens: 64,
                ReservedOverheadTokens: 64),
            LocalAgentCompactionSettings.Default with
            {
                AllowOversizedAnchorReduction = true,
                RecentSuffixTargetTokens = 160,
            },
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 200);

        Assert.IsNotNull(preparation);
        Assert.AreEqual(conversation[^1], preparation!.OversizedAnchorMessage);
        CollectionAssert.DoesNotContain(preparation.MessagesToKeep.ToArray(), conversation[^1]);
        CollectionAssert.DoesNotContain(preparation.MessagesToSummarize.ToArray(), conversation[^1]);
        Assert.AreEqual("user:latest", preparation.AnchorContentId);
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_UsesSummarizerExecutorAndPreviousSummaryOnUpdate()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-update");
        var state = CreateState("session-summary-update");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    Assert.IsNull(request.ReasoningEffort);
                    var payload = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Continue the task.
                                    ## Active User Request
                                    Keep working.
                                    ## Constraints
                                    - Preserve behavior.
                                    ## Progress
                                    ### Done
                                    - Captured progress.
                                    ### In Progress
                                    - Continue.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Use compaction.
                                    ## Next Steps
                                    - Continue from the suffix.
                                    ## Critical Context
                                    - Important details retained.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Third answer " + new string('c', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 140)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 140)) }).ConfigureAwait(false);
        _ = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Third prompt " + new string('z', 140)) }).ConfigureAwait(false);
        _ = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);

        Assert.AreEqual(2, summaryPayloads.Count);
        StringAssert.Contains(summaryPayloads[0], "<conversation>");
        Assert.IsFalse(summaryPayloads[0].Contains("<previous-summary>", StringComparison.Ordinal));
        StringAssert.Contains(summaryPayloads[1], "<previous-summary>");
        StringAssert.Contains(summaryPayloads[1], "## Objective");
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_PassesConfiguredSummaryOutputTokenLimitToSummarizer()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            SummaryOutputTokens = 320,
        });
        var summary = CreateSummary("session-summary-output-limit");
        var state = CreateState("session-summary-output-limit");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var observedMaxOutputTokens = new List<int?>();
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    observedMaxOutputTokens.Add(request.MaxOutputTokens);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Keep working.
                                    ## Active User Request
                                    Follow up.
                                    ## Constraints
                                    - Preserve behavior.
                                    ## Progress
                                    ### Done
                                    - Captured state.
                                    ### In Progress
                                    - Compaction.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Limit summary output.
                                    ## Next Steps
                                    - Continue.
                                    ## Critical Context
                                    - Keep the summary tight.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        CollectionAssert.AreEqual(new int?[] { 320 }, observedMaxOutputTokens);
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_ChunksOversizedSummaryInput()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            SummaryInputTokens = 360,
            RecentSuffixTargetTokens = 180,
            MaxChunkPasses = 4,
        });
        var summary = CreateSummary("session-chunked-compaction");
        var state = CreateState("session-chunked-compaction");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    var payload = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Continue the task.
                                    ## Active User Request
                                    Keep working.
                                    ## Constraints
                                    - Preserve behavior.
                                    ## Progress
                                    ### Done
                                    - Captured progress.
                                    ### In Progress
                                    - Continue.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Use chunked compaction.
                                    ## Next Steps
                                    - Continue from the suffix.
                                    ## Critical Context
                                    - Important details retained.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 320))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 320))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Third answer " + new string('c', 320))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 240)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 240)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Third prompt " + new string('z', 240)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(summaryPayloads.Count >= 2);
        Assert.IsTrue(summaryPayloads.Skip(1).Any(static payload => payload.Contains("<previous-summary>", StringComparison.Ordinal)));

        var history = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = history
            .OfType<AgentRawEvent>()
            .Single(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.ChunkCount > 1);
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_ReducesOversizedLatestUserAnchorBeforeTurnExecution()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            TriggerThreshold = 0.60,
            ReservedOutputTokens = 128,
            ReservedOverheadTokens = 96,
            RecentSuffixTargetTokens = 160,
            SummaryInputTokens = 320,
            SummaryOutputTokens = 320,
            MaxChunkPasses = 4,
            AllowOversizedAnchorReduction = true,
        });
        var summary = CreateSummary("session-oversized-anchor");
        var state = CreateState("session-oversized-anchor");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        var actualTurnPayloads = new List<string>();
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [
                    new AgentModelInfo(
                        "gpt-5.4",
                        "GPT-5.4",
                        Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["contextWindow"] = 700L,
                            ["inputTokenLimit"] = 540L,
                            ["outputTokenLimit"] = 320L,
                        }),
                ],
                (request, _, _) =>
                {
                    var payload = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    var summaryText = request.SystemMessage?.Contains("oversized-anchor reducer", StringComparison.Ordinal) == true
                        ? """
                          ## Task
                          - Update the compaction system for large sessions.
                          ## Explicit Requirements
                          - Preserve recent knowledge.
                          - Keep configurable compaction targets.
                          - Cover very large prompts and oversized attachments.
                          ## Files and Identifiers
                          - doc/specs/agent_compaction_specs.md
                          - tmp/agent_compaction_plan_v2.md
                          ## Exact Literals and Errors
                          - "target_context_ratio_ideal = 0.03"
                          """
                        : """
                          ## Objective
                          Update compaction behavior for large sessions.
                          ## Active User Request
                          Implement the remaining compaction plan while preserving the latest task via an anchor synopsis.
                          ## Constraints
                          - Keep configurable limits.
                          - Prefer recent context.
                          ## Progress
                          ### Done
                          - Reduced the oversized latest user message into a compact anchor.
                          ### In Progress
                          - Continue implementing the compaction improvements.
                          ### Blocked
                          - None recorded.
                          ## Decisions
                          - Use a compact synopsis instead of replaying the full oversized prompt.
                          ## Next Steps
                          - Continue the requested implementation work.
                          ## Critical Context
                          - The large latest user input was intentionally reduced before replay.
                          ## Relevant Files
                          - doc/specs/agent_compaction_specs.md
                          - tmp/agent_compaction_plan_v2.md
                          """;
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text(summaryText)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Initial answer.")]),
                        Usage = CreateUsageSnapshot(120, 40),
                    }),
                (request, _, _) =>
                {
                    actualTurnPayloads.Add(Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Handled the oversized request.")]),
                            Usage = CreateUsageSnapshot(140, 50),
                        });
                }),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Normal prompt") }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = AgentInput.Text(
                "Please finish the compaction v2 implementation. " +
                "Requirements: " +
                string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 160).Select(index => $"{index}. Keep detail #{index} and mention doc/specs/agent_compaction_specs.md"))),
        }).ConfigureAwait(false);

        Assert.IsTrue(summaryPayloads.Count >= 2);
        Assert.IsTrue(summaryPayloads.Any(static payload => payload.Contains("<codealta-oversized-anchor-request", StringComparison.Ordinal)));
        Assert.IsTrue(summaryPayloads.Any(static payload => payload.Contains("<oversized-anchor-synopsis>", StringComparison.Ordinal)));
        Assert.AreEqual(1, actualTurnPayloads.Count);
        StringAssert.Contains(actualTurnPayloads[0], "codealta-compaction-checkpoint");
        Assert.IsFalse(actualTurnPayloads[0].Contains("Keep detail #160", StringComparison.Ordinal));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("threshold", persistedState.LastCompactionTrigger);

        var history = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = history
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.OversizedAnchorReduced);
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_SummarizerFailureLeavesStateUntouched()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-failure");
        var state = CreateState("session-summary-failure");
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
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (_, _, _) => throw new InvalidOperationException("summary failed"),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 140)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 140)) }).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync()).ConfigureAwait(false);

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNull(persistedState.CompactionCheckpointEventId);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsFalse(persistedHistory.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.compactionCheckpoint"));
        Assert.IsFalse(persistedHistory.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionStarted));
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_RejectsOversizedGeneratedSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(new LocalAgentCompactionSettings(
            Enabled: true,
            TriggerThreshold: 0.80,
            TargetThreshold: 0.50,
            ReservedOutputTokens: 32,
            ReservedOverheadTokens: 32,
            KeepLastUserMessage: true,
            AllowSplitTurn: true)
        {
            RecentSuffixTargetTokens = 160,
        });
        var summary = CreateSummary("session-summary-too-large");
        var state = CreateState("session-summary-too-large");
        await store.UpsertProviderAsync(provider).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryAttempts = 0;
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [
                    new AgentModelInfo(
                        "gpt-5.4",
                        "GPT-5.4",
                        Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["contextWindow"] = 400L,
                            ["inputTokenLimit"] = 300L,
                            ["outputTokenLimit"] = 64L,
                        }),
                ],
                (_, _, _) =>
                {
                    summaryAttempts++;
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("## Objective\n" + new string('x', 4000))]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 160)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 160)) }).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync()).ConfigureAwait(false);

        Assert.AreEqual(1, summaryAttempts);
        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNull(persistedState.CompactionCheckpointEventId);
    }

    [TestMethod]
    public void LocalAgentTokenEstimator_EstimatePromptTokens_UsesLastOperationPlusTrailingTail()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Answer")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Tool, [new LocalAgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("tool output")]))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Follow-up")]),
        };

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage: "System",
            developerInstructions: null,
            conversation,
            new AgentSessionUsage(
                LastOperation: new AgentOperationUsageSnapshot(InputTokens: 120, OutputTokens: 30),
                Scope: AgentUsageScope.LastOperation,
                Source: AgentUsageSource.LocalProviderUsage,
                UpdatedAt: DateTimeOffset.UtcNow));

        Assert.AreEqual("provider-last-operation+local-tail", estimate.Source);
        Assert.AreEqual(
            150 + LocalAgentTokenEstimator.EstimateMessage(conversation[2]) + LocalAgentTokenEstimator.EstimateMessage(conversation[3]),
            estimate.Tokens);
    }

    [TestMethod]
    public void LocalAgentTokenEstimator_EstimatePromptTokens_DoesNotReuseLastOperationAcrossCheckpoint()
    {
        var conversation = new[]
        {
            new LocalAgentCompactionCheckpoint
            {
                Version = 1,
                ContentId = "compaction:1",
                Trigger = "manual",
                Summary = "## Objective\nCheckpoint",
                TokensBefore = 200,
                SummarizedMessageCount = 2,
            }.CreateMessage(),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Follow-up")]),
        };

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage: "System",
            developerInstructions: null,
            conversation,
            new AgentSessionUsage(
                LastOperation: new AgentOperationUsageSnapshot(InputTokens: 120, OutputTokens: 30),
                Scope: AgentUsageScope.LastOperation,
                Source: AgentUsageSource.LocalProviderUsage,
                UpdatedAt: DateTimeOffset.UtcNow));

        Assert.AreEqual("local-heuristic", estimate.Source);
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
            [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
            (request, _, _) =>
            {
                var payload = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                StringAssert.Contains(payload, "<conversation>");
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text(
                                """
                                ## Objective
                                Recover from overflow.
                                ## Active User Request
                                Prompt two
                                ## Constraints
                                - Keep the latest user message verbatim.
                                ## Progress
                                ### Done
                                - Earlier work summarized.
                                ### In Progress
                                - Retry after compaction.
                                ### Blocked
                                - Context overflow.
                                ## Decisions
                                - Compact and retry once.
                                ## Next Steps
                                - Retry the request.
                                ## Critical Context
                                - Keep the checkpoint concise.
                                ## Relevant Files
                                - None tracked.
                                """)]),
                    });
            },
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
        private readonly Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>? _summaryHandler;
        private readonly Queue<Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>> _steps;

        public ScriptedTurnExecutor(params Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>[] steps)
            : this(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                summaryHandler: null,
                steps)
        {
        }

        public ScriptedTurnExecutor(
            IReadOnlyList<AgentModelInfo> models,
            Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>? summaryHandler = null,
            params Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>[] steps)
        {
            _models = models;
            _summaryHandler = summaryHandler;
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
            if (_summaryHandler is not null &&
                (request.SystemMessage?.Contains("CodeAlta compaction summarizer", StringComparison.Ordinal) == true ||
                 request.SystemMessage?.Contains("CodeAlta oversized-anchor reducer", StringComparison.Ordinal) == true))
            {
                return _summaryHandler(request, onUpdate, cancellationToken);
            }

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
            if (request.SystemMessage?.Contains("CodeAlta compaction summarizer", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("## Objective\nSummary")]),
                    });
            }

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
