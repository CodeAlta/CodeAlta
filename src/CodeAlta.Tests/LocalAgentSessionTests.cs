using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;

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
    public async Task LocalAgentSession_CompactAsync_PersistsSnapshotAndReplaysFromSummary()
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
                                         [new LocalAgentMessagePart.Text("First answer.")]),
                                 }),
                             (_, _, _) => Task.FromResult(
                                 new LocalAgentTurnResponse
                                 {
                                     AssistantMessage = new LocalAgentConversationMessage(
                                         LocalAgentConversationRole.Assistant,
                                         [new LocalAgentMessagePart.Text("Second answer.")]),
                                 })),
                         CreateOptions(provider, temp.Path)))
        {
            _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt") }).ConfigureAwait(false);
            _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt") }).ConfigureAwait(false);

            var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.Success);
            Assert.AreEqual(3, outcome.MessagesRemoved);
        }

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsTrue(persistedHistory.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.compactionSnapshot"));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionEventOffset);

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
                    Assert.AreEqual(2, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.System, request.Conversation[0].Role);
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                        "First prompt");
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[1].Role);
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

    private static LocalAgentProviderDescriptor CreateProvider()
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
        private readonly Queue<Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>> _steps;

        public ScriptedTurnExecutor(params Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>[] steps)
        {
            _steps = new Queue<Func<LocalAgentTurnRequest, Func<LocalAgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<LocalAgentTurnResponse>>>(steps);
        }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            LocalAgentProviderDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("gpt-5.4", "GPT-5.4")]);

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
}
