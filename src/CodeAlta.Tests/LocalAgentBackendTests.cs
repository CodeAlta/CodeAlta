using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentBackendTests
{
    [TestMethod]
    public async Task LocalAgentBackend_CreateListResumeAndDelete_Works()
    {
        using var temp = TestTempDirectory.Create();
        var backend = CreateBackend(temp.Path, out var executor);

        await backend.StartAsync().ConfigureAwait(false);

        await using var createdSession = await backend.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = "openai",
                    Title = "Initial thread title",
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\sample",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        _ = await createdSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("First prompt"),
                }).ConfigureAwait(false);

        var sessions = await backend.ListSessionsAsync().ConfigureAwait(false);
        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual(createdSession.SessionId, sessions[0].SessionId);
        Assert.AreEqual("openai-responses", sessions[0].ProtocolFamily);
        Assert.AreEqual("openai", sessions[0].ProviderKey);
        Assert.AreEqual("gpt-5.4", sessions[0].ModelId);
        Assert.AreEqual("C:\\repo\\sample", sessions[0].WorkspacePath);
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(sessions[0].Details);
        Assert.AreEqual("OpenAI", details.ProviderDisplayName);
        Assert.AreEqual("Initial thread title", details.Title);

        await using var resumedSession = await backend.ResumeSessionAsync(
                createdSession.SessionId,
                new AgentSessionResumeOptions
                {
                    WorkingDirectory = "C:\\repo\\sample",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        _ = await resumedSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Second prompt"),
                }).ConfigureAwait(false);

        Assert.AreEqual(2, executor.Requests.Count);
        Assert.AreEqual(1, executor.Requests[0].Conversation.Count);
        Assert.AreEqual(3, executor.Requests[1].Conversation.Count);

        Assert.IsTrue(await backend.DeleteSessionAsync(createdSession.SessionId).ConfigureAwait(false));
        Assert.AreEqual(0, (await backend.ListSessionsAsync().ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task LocalAgentBackend_CreateSession_UsesDefaultProviderWhenNotSpecified()
    {
        using var temp = TestTempDirectory.Create();
        var backend = CreateBackend(temp.Path, out _);

        await using var session = await backend.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\default",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        var sessions = await backend.ListSessionsAsync().ConfigureAwait(false);
        Assert.AreEqual("openai", sessions.Single().ProviderKey);
        Assert.AreEqual(session.SessionId, sessions.Single().SessionId);
    }

    [TestMethod]
    public async Task LocalAgentBackend_SendAsync_EmitsTurnDiffForBuiltInFileChanges()
    {
        using var temp = TestTempDirectory.Create();
        var workspacePath = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspacePath);
        var executor = new FileWritingTurnExecutor();
        var backend = CreateBackend(temp.Path, executor);

        await using var session = await backend.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = "openai",
                    Model = "gpt-5.4",
                    WorkingDirectory = workspacePath,
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Create a file"),
                })
            .ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        var diffUpdated = history
            .OfType<AgentSessionUpdateEvent>()
            .Single(@event => @event.Kind == AgentSessionUpdateKind.DiffUpdated);
        var diff = diffUpdated.Details?.GetProperty("diff").GetString();
        Assert.IsNotNull(diff);
        StringAssert.Contains(diff, "diff --git a/created.txt b/created.txt");
        StringAssert.Contains(diff, "--- /dev/null");
        StringAssert.Contains(diff, "+++ b/created.txt");
        StringAssert.Contains(diff, "+hello");

        var completedTool = history
            .OfType<AgentActivityEvent>()
            .Single(@event => @event.ActivityId == "call-1" && @event.Phase == AgentActivityPhase.Completed);
        var toolDiff = completedTool.Details?.GetProperty("diff").GetString();
        Assert.IsNotNull(toolDiff);
        StringAssert.Contains(toolDiff, "diff --git a/created.txt b/created.txt");
        StringAssert.Contains(toolDiff, "+hello");

        var toolOutput = history
            .OfType<AgentContentCompletedEvent>()
            .Single(@event => @event.ParentActivityId == "call-1" && @event.Kind == AgentContentKind.ToolOutput);
        Assert.AreEqual(toolDiff, toolOutput.Details?.GetProperty("diff").GetString());
    }

    [TestMethod]
    public async Task LocalAgentTurnFileChangeTracker_CreateUnifiedDiff_UsesPreciseDiffForLargeFiles()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "large.txt");
        var beforeLines = Enumerable.Range(1, 4_000)
            .Select(static index => index == 2_000 ? "line 2000 before" : $"line {index}")
            .ToArray();
        var afterLines = Enumerable.Range(1, 4_000)
            .Select(static index => index == 2_000 ? "line 2000 after" : $"line {index}")
            .ToArray();
        await File.WriteAllTextAsync(filePath, string.Join('\n', beforeLines) + "\n").ConfigureAwait(false);

        var tracker = new LocalAgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([filePath], CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(filePath, string.Join('\n', afterLines) + "\n").ConfigureAwait(false);
        await tracker.CaptureAfterAsync([filePath], CancellationToken.None).ConfigureAwait(false);

        var diff = tracker.CreateUnifiedDiff();

        Assert.IsNotNull(diff);
        StringAssert.Contains(diff, "diff --git a/large.txt b/large.txt");
        StringAssert.Contains(diff, "-line 2000 before");
        StringAssert.Contains(diff, "+line 2000 after");
        Assert.IsTrue(CountUnifiedDiffLines(diff!, '+') < 10);
        Assert.IsTrue(CountUnifiedDiffLines(diff!, '-') < 10);
    }

    [TestMethod]
    public async Task LocalAgentTurnFileChangeTracker_CreateUnifiedDiff_DoesNotMatchDifferentLineEndings()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "eol.txt");
        await File.WriteAllTextAsync(filePath, "alpha\r\nbeta\r\n").ConfigureAwait(false);

        var tracker = new LocalAgentTurnFileChangeTracker(temp.Path);
        await tracker.CaptureBeforeAsync([filePath], CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\n").ConfigureAwait(false);
        await tracker.CaptureAfterAsync([filePath], CancellationToken.None).ConfigureAwait(false);

        var diff = tracker.CreateUnifiedDiff();

        Assert.IsNotNull(diff);
        StringAssert.Contains(diff, "-alpha");
        StringAssert.Contains(diff, "+alpha");
        Assert.AreEqual(2, CountUnifiedDiffLines(diff!, '+'));
        Assert.AreEqual(2, CountUnifiedDiffLines(diff!, '-'));
    }

    [TestMethod]
    public async Task LocalAgentBackend_ResumeSession_RepairsRecoveredUsageUsingEquivalentModelIds()
    {
        using var temp = TestTempDirectory.Create();
        var backend = CreateBackend(temp.Path, out _);
        var store = new FileSystemLocalAgentSessionStore(
            new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sessionId = "019d6a00-80ca-7f83-8407-92db7e0fae60";
        var createdAt = DateTimeOffset.Parse("2026-04-07T22:11:51.114932+00:00");
        var summary = new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = AgentBackendIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\code\\CodeAlta",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Usage = new AgentSessionUsage(
                LastOperation: new AgentOperationUsageSnapshot(
                    Model: "gpt-5.4-2026-03-05",
                    InputTokens: 41923,
                    OutputTokens: 367),
                Scope: AgentUsageScope.LastOperation,
                Source: AgentUsageSource.RecoveredHistory,
                UpdatedAt: createdAt),
        };
        var state = new LocalAgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            UpdatedAt = createdAt,
            Usage = summary.Usage,
        };

        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var resumedSession = await backend.ResumeSessionAsync(
            sessionId,
            new AgentSessionResumeOptions
            {
                WorkingDirectory = "C:\\code\\CodeAlta",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var repairedSummary = await store.GetSessionAsync("openai-responses", "openai", sessionId).ConfigureAwait(false);
        Assert.IsNotNull(repairedSummary?.Usage);
        Assert.AreEqual(1050000L, repairedSummary.Usage.TokenLimit);
        Assert.IsNull(repairedSummary.Usage.CurrentTokens);
        Assert.AreEqual(AgentUsageScope.LastOperation, repairedSummary.Usage.Scope);
    }

    [TestMethod]
    public async Task LocalAgentBackend_ResumeSession_RecoversUsageFromPersistedHistoryEvents()
    {
        using var temp = TestTempDirectory.Create();
        var backend = CreateBackend(temp.Path, out var executor);
        var store = new FileSystemLocalAgentSessionStore(
            new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sessionId = "019e17b0-1b23-74a8-b1f1-a532f06c4ef1";
        var createdAt = DateTimeOffset.Parse("2026-05-11T15:00:00+00:00");
        var usageUpdatedAt = createdAt.AddMinutes(1);
        var summary = new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = AgentBackendIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\code\\CodeAlta",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var state = new LocalAgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            UpdatedAt = createdAt,
        };
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(203_533, null, 42, "Active context window"),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.4-2026-03-05",
                InputTokens: 676,
                OutputTokens: 105,
                CachedInputTokens: 202_752),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.LocalProviderUsage,
            UpdatedAt: usageUpdatedAt);

        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);
        await store.AppendEventsAsync(
                "openai-responses",
                "openai",
                sessionId,
                [new AgentSessionUpdateEvent(
                    AgentBackendIds.OpenAIResponses,
                    sessionId,
                    usageUpdatedAt,
                    null,
                    AgentSessionUpdateKind.UsageUpdated,
                    "Usage updated.",
                    Usage: usage)])
            .ConfigureAwait(false);

        await using var resumedSession = await backend.ResumeSessionAsync(
            sessionId,
            new AgentSessionResumeOptions
            {
                WorkingDirectory = "C:\\code\\CodeAlta",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var repairedSummary = await store.GetSessionAsync("openai-responses", "openai", sessionId).ConfigureAwait(false);
        var repairedState = await store.GetStateAsync("openai-responses", "openai", sessionId).ConfigureAwait(false);

        Assert.IsNotNull(repairedSummary?.Usage);
        Assert.IsNotNull(repairedState?.Usage);
        Assert.AreEqual(203_533L, repairedSummary.Usage.CurrentTokens);
        Assert.AreEqual(1050000L, repairedSummary.Usage.TokenLimit);
        Assert.AreEqual(202_752L, repairedSummary.Usage.LastOperation?.CachedInputTokens);
        Assert.AreEqual(203_533L, repairedState.Usage.CurrentTokens);
        Assert.AreEqual(1050000L, repairedState.Usage.TokenLimit);
        Assert.AreEqual(0, executor.Requests.Count);
    }

    private static LocalAgentBackend CreateBackend(string tempRoot, out RecordingTurnExecutor executor)
    {
        executor = new RecordingTurnExecutor();
        return CreateBackend(tempRoot, executor);
    }

    private static LocalAgentBackend CreateBackend(string tempRoot, ILocalAgentTurnExecutor executor)
    {
        return new LocalAgentBackend(
            AgentBackendIds.OpenAIResponses,
            "OpenAI Responses",
            new LocalAgentBackendOptions
            {
                StateRootPath = Path.Combine(tempRoot, "machine", "agents"),
                Providers =
                [
                    new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = "openai-responses",
                            ProviderKey = "openai",
                            DisplayName = "OpenAI",
                            BackendId = AgentBackendIds.OpenAIResponses,
                            TransportKind = LocalAgentTransportKind.OpenAIResponses,
                            BaseUri = new Uri("https://api.openai.com/v1"),
                            IsDefault = true,
                            Profile = new LocalAgentProviderProfile
                            {
                                SupportsDeveloperRole = true,
                                SupportsStore = true,
                                SupportsReasoningEffort = true,
                                StreamsUsage = true,
                                MaxTokensFieldName = "max_output_tokens",
                                ReasoningFieldNames = ["reasoning"],
                            },
                        },
                        TurnExecutor = executor,
                    },
                ],
            });
    }

    private static int CountUnifiedDiffLines(string diff, char prefix)
        => diff.Split('\n')
            .Count(line => line.StartsWith(prefix) &&
                           !line.StartsWith(new string(prefix, 3), StringComparison.Ordinal));

    private sealed class RecordingTurnExecutor : ILocalAgentTurnExecutor
    {
        public List<LocalAgentTurnRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            LocalAgentProviderDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo(
                    "gpt-5.4-2026-03-05",
                    "GPT-5.4",
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
            Requests.Add(request);
            return Task.FromResult(
                new LocalAgentTurnResponse
                {
                    AssistantMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Assistant,
                        [new LocalAgentMessagePart.Text($"Echo #{Requests.Count}")]),
                });
        }
    }

    private sealed class FileWritingTurnExecutor : ILocalAgentTurnExecutor
    {
        private int _requestCount;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            LocalAgentProviderDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo("gpt-5.4", "GPT-5.4"),
            ]);

        public Task<LocalAgentTurnResponse> ExecuteTurnAsync(
            LocalAgentTurnRequest request,
            Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            _requestCount++;
            if (_requestCount == 1)
            {
                var arguments = JsonSerializer.SerializeToElement(new
                {
                    input = """
                            *** Begin Patch
                            *** Add File: created.txt
                            +hello
                            *** End Patch
                            """,
                });
                return Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.ToolCall("call-1", "apply_patch", arguments)]),
                    });
            }

            return Task.FromResult(
                new LocalAgentTurnResponse
                {
                    AssistantMessage = new LocalAgentConversationMessage(
                        LocalAgentConversationRole.Assistant,
                        [new LocalAgentMessagePart.Text("Done.")]),
                });
        }
    }
}
