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

        await store.UpsertProviderAsync(CreateProviderDescriptor()).ConfigureAwait(false);
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

    private static LocalAgentBackend CreateBackend(string tempRoot, out RecordingTurnExecutor executor)
    {
        executor = new RecordingTurnExecutor();
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

    private static LocalAgentProviderDescriptor CreateProviderDescriptor()
    {
        return new LocalAgentProviderDescriptor
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
        };
    }

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
}
