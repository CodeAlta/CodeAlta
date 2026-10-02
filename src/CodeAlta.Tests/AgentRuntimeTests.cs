using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentRuntimeTests
{
    [TestMethod]
    public async Task AgentRuntime_CreateListResumeAndDelete_Works()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out var executor);

        await agentRuntime.StartAsync().ConfigureAwait(false);

        await using var createdSession = await agentRuntime.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = "openai",
                    Title = "Initial session title",
                    ParentSessionId = "parent-session",
                    CreatedByRunId = new AgentRunId("run-parent"),
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\sample",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        _ = await createdSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("First prompt"),
                }).ConfigureAwait(false);

        var sessions = await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual(createdSession.SessionId, sessions[0].SessionId);
        Assert.AreEqual("openai-responses", sessions[0].ProtocolFamily);
        Assert.AreEqual("openai", sessions[0].ProviderKey);
        Assert.AreEqual("gpt-5.4", sessions[0].ModelId);
        Assert.AreEqual("C:\\repo\\sample", sessions[0].WorkspacePath);
        Assert.AreEqual("parent-session", sessions[0].ParentSessionId);
        Assert.AreEqual("parent-session", sessions[0].CreatedBySessionId);
        Assert.AreEqual(new AgentRunId("run-parent"), sessions[0].CreatedByRunId);
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(sessions[0].Details);
        Assert.IsNull(details.ProviderDisplayName);
        Assert.AreEqual("Initial session title", details.Title);

        await using var resumedSession = await agentRuntime.ResumeSessionAsync(
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

        Assert.IsTrue(await agentRuntime.DeleteSessionAsync(createdSession.SessionId).ConfigureAwait(false));
        Assert.AreEqual(0, (await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
    }

    [TestMethod]
    public async Task AgentRuntime_CreateSession_UsesDefaultProviderWhenNotSpecified()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out _);

        await using var session = await agentRuntime.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\default",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        var sessions = await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual("openai", sessions.Single().ProviderKey);
        Assert.AreEqual(session.SessionId, sessions.Single().SessionId);
    }

    [TestMethod]
    public async Task AgentRuntime_CreateSession_UsesRequestedSessionId()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out _);

        await using var session = await agentRuntime.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    SessionId = "codealta-owned-session",
                    ProviderKey = "openai",
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\requested",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                }).ConfigureAwait(false);

        var sessions = await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual("codealta-owned-session", session.SessionId);
        Assert.AreEqual("codealta-owned-session", sessions.Single().SessionId);
    }

    [TestMethod]
    public async Task AgentRuntime_ResumeSession_LoadsLegacyProviderIdOnlyJournalAndSwitchesProvider()
    {
        using var temp = TestTempDirectory.Create();
        var sessionId = "019e836a-ef6d-7a2a-97a7-893f0f0c1001";
        var createdAt = DateTimeOffset.Parse("2026-05-20T12:00:00+00:00");
        await WriteLegacyProviderIdOnlyJournalAsync(
                temp.Path,
                sessionId,
                createdAt,
                ProviderId: "openai",
                providerSessionId: "resp_legacy")
            .ConfigureAwait(false);

        var targetExecutor = new RecordingTurnExecutor();
        var targetRuntime = CreateAgentRuntime(
            temp.Path,
            targetExecutor,
            providerKey: "anthropic",
            displayName: "Anthropic",
            protocolFamily: "anthropic-messages",
            transportKind: AgentTransportKind.AnthropicMessages,
            ProviderId: new ModelProviderId("anthropic"));

        var legacySessions = await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, legacySessions.Length);
        Assert.AreEqual(sessionId, legacySessions[0].SessionId);
        Assert.AreEqual("openai", legacySessions[0].ProviderKey);

        await using var resumedSession = await targetRuntime.ResumeSessionAsync(
                sessionId,
                new AgentSessionResumeOptions
                {
                    ProviderKey = "anthropic",
                    Model = "claude-sonnet-4.5",
                    WorkingDirectory = "C:\\repo\\legacy",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        _ = await resumedSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Continue") }).ConfigureAwait(false);

        var request = targetExecutor.Requests.Single();
        Assert.AreEqual("anthropic", request.Provider.ProviderKey);
        Assert.AreEqual("anthropic", request.State.ProviderKey);
        Assert.AreEqual("anthropic-messages", request.State.ProtocolFamily);
        Assert.IsNull(request.State.ProviderSessionId);
        Assert.IsNull(request.State.ProviderState);
    }

    [TestMethod]
    public async Task AgentRuntime_ResumeSession_DifferentProvider_ReplaysStoredHistory()
    {
        using var temp = TestTempDirectory.Create();
        var sourceExecutor = new RecordingTurnExecutor();
        var sourceRuntime = CreateAgentRuntime(
            temp.Path,
            sourceExecutor,
            providerKey: "openai",
            displayName: "OpenAI",
            protocolFamily: "openai-responses",
            transportKind: AgentTransportKind.OpenAIResponses,
            ProviderId: new ModelProviderId("openai"));

        await using var sourceSession = await sourceRuntime.CreateSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = "openai",
                    Title = "Switchable session",
                    Model = "gpt-5.4",
                    WorkingDirectory = "C:\\repo\\sample",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        _ = await sourceSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt") }).ConfigureAwait(false);

        var store = new FileSystemAgentSessionStore(
            new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sourceState = await store.GetStateAsync("openai-responses", "openai", sourceSession.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(sourceState);
        using var providerState = JsonDocument.Parse("""{"responseId":"resp_old"}""");
        await store.UpsertStateAsync(sourceState with
            {
                ProviderSessionId = "resp_old",
                ProviderState = providerState.RootElement.Clone(),
            })
            .ConfigureAwait(false);

        var targetExecutor = new RecordingTurnExecutor();
        var targetRuntime = CreateAgentRuntime(
            temp.Path,
            targetExecutor,
            providerKey: "anthropic",
            displayName: "Anthropic",
            protocolFamily: "anthropic-messages",
            transportKind: AgentTransportKind.AnthropicMessages,
            ProviderId: new ModelProviderId("anthropic"));

        await using var resumedSession = await targetRuntime.ResumeSessionAsync(
                sourceSession.SessionId,
                new AgentSessionResumeOptions
                {
                    ProviderKey = "anthropic",
                    Model = "claude-sonnet-4.5",
                    WorkingDirectory = "C:\\repo\\sample",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        _ = await resumedSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt") }).ConfigureAwait(false);

        Assert.AreEqual(1, targetExecutor.Requests.Count);
        var request = targetExecutor.Requests[0];
        Assert.AreEqual("anthropic", request.Provider.ProviderKey);
        Assert.AreEqual("anthropic", request.State.ProviderKey);
        Assert.AreEqual("anthropic-messages", request.State.ProtocolFamily);
        Assert.IsNull(request.State.ProviderSessionId);
        Assert.IsNull(request.State.ProviderState);
        Assert.AreEqual(3, request.Conversation.Count);
        Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
        Assert.AreEqual(AgentConversationRole.Assistant, request.Conversation[1].Role);
        Assert.AreEqual(AgentConversationRole.User, request.Conversation[2].Role);

        var targetSummary = await store.GetSessionAsync("anthropic-messages", "anthropic", sourceSession.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(targetSummary);
        Assert.AreEqual("claude-sonnet-4.5", targetSummary.ModelId);
        Assert.IsNull(await store.GetSessionAsync("openai-responses", "openai", sourceSession.SessionId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AgentRuntime_ResumeMissingSession_DoesNotCreateReplacementOrExecuteProvider()
    {
        using var temp = TestTempDirectory.Create();
        await using var runtime = CreateAgentRuntime(temp.Path, out var executor);
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => runtime.ResumeSessionAsync("missing-session",
            new AgentSessionResumeOptions
            {
                ProviderKey = "openai", WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => throw new AssertFailedException("Resume must not request permission."),
            }));
        Assert.AreEqual(0, (await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync()).Length);
        Assert.AreEqual(0, executor.Requests.Count);
    }

    [TestMethod]
    public async Task AgentRuntime_UnknownTransferProvider_PreservesOriginalJournal()
    {
        using var temp = TestTempDirectory.Create();
        await using var runtime = CreateAgentRuntime(temp.Path, out var executor);
        await using var source = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "openai", Model = "original-model", WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => throw new AssertFailedException("Preparation must not request permission."),
        });
        var store = CreateSessionStore(temp.Path);
        var summary = await store.GetSessionSummaryAsync(source.SessionId);
        Assert.IsNotNull(summary);
        var path = new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents"))
            .GetSessionFilePath(source.SessionId, summary.CreatedAt);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => runtime.ResumeSessionAsync(source.SessionId,
            new AgentSessionResumeOptions
            {
                ProviderKey = "unknown", WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => throw new AssertFailedException("Preparation must not request permission."),
            }));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, executor.Requests.Count);
    }

    [TestMethod]
    public async Task AgentRuntime_PrepareTransfer_PreservesJournalAndOriginalContinuation()
    {
        using var temp = TestTempDirectory.Create();
        await using var originalRuntime = CreateAgentRuntime(temp.Path, out var originalExecutor);
        await using var original = await originalRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "openai", Model = "original-model", WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => throw new AssertFailedException("No permission request expected."),
        });
        var store = CreateSessionStore(temp.Path);
        var state = await store.GetStateAsync(original.SessionId);
        Assert.IsNotNull(state);
        await store.UpsertStateAsync(state with { ProviderSessionId = "original-continuation" });
        var summary = await store.GetSessionSummaryAsync(original.SessionId);
        Assert.IsNotNull(summary);
        var path = new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents"))
            .GetSessionFilePath(original.SessionId, summary.CreatedAt);
        var before = await File.ReadAllBytesAsync(path);
        var executor = new RecordingTurnExecutor();
        var cache = new TransferFaultCache(_ => throw new AssertFailedException("Preparation must not write cache or journal."));
        await using var target = CreateAgentRuntime(temp.Path, executor, "anthropic", "Anthropic",
            "anthropic-messages", AgentTransportKind.AnthropicMessages, new ModelProviderId("anthropic"), cache);
        var prepared = await target.PrepareTransferAsync(original.SessionId, new AgentSessionResumeOptions
        {
            ProviderKey = "anthropic", Model = "target-model", WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => throw new AssertFailedException("Preparation must not request permission."),
        });
        await using (prepared)
        {
            Assert.AreEqual("openai", prepared.OriginalSummary.ProviderKey);
            Assert.AreEqual("original-continuation", prepared.OriginalState.ProviderSessionId);
            Assert.AreEqual("anthropic", prepared.TargetSummary.ProviderKey);
            Assert.AreEqual("target-model", prepared.TargetSummary.ModelId);
            Assert.IsNull(prepared.TargetState.ProviderSessionId);
            Assert.IsNull(prepared.TargetState.ProviderState);
            Assert.AreEqual(before.LongLength, prepared.Revision.Length);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        }
        await prepared.DisposeAsync(); // Repeated abandonment must be harmless.
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, cache.Writes);
        Assert.AreEqual(0, executor.Requests.Count);
        Assert.AreEqual(0, originalExecutor.Requests.Count);
    }

    // Characterization of the legacy resume path, NOT the safety contract for explicit transfer.
    // The future prepare/commit path must never use this method as speculative preparation.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AgentRuntime_LegacyTransfer_PostSummaryFailureLeavesMixedDurableProvider(bool cancel)
    {
        using var temp = TestTempDirectory.Create();
        await using var sourceRuntime = CreateAgentRuntime(temp.Path, out var sourceExecutor);
        await using var source = await sourceRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "openai", Model = "original-model", WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => throw new AssertFailedException("Preparation must not request permission."),
        });
        var store = CreateSessionStore(temp.Path);
        var original = await store.GetStateAsync(source.SessionId);
        Assert.IsNotNull(original);
        using var continuation = JsonDocument.Parse("""{"responseId":"original-continuation"}""");
        await store.UpsertStateAsync(original with
        {
            ProviderSessionId = "original-continuation", ProviderState = continuation.RootElement.Clone(),
        });
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("injected post-summary cache failure");
        var cache = new TransferFaultCache(projection =>
        {
            Assert.AreEqual("anthropic", projection.Summary.ProviderKey);
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw failure;
        });
        var targetExecutor = new RecordingTurnExecutor();
        await using var target = CreateAgentRuntime(temp.Path, targetExecutor, "anthropic", "Anthropic",
            "anthropic-messages", AgentTransportKind.AnthropicMessages, new ModelProviderId("anthropic"), cache);
        Exception? observed = null;
        try
        {
            await using var unexpected = await target.ResumeSessionAsync(source.SessionId,
                new AgentSessionResumeOptions
                {
                    ProviderKey = "anthropic", Model = "target-model", WorkingDirectory = temp.Path,
                    OnPermissionRequest = static (_, _) => throw new AssertFailedException("Preparation must not request permission."),
                },
                cancellation.Token);
        }
        catch (Exception error) { observed = error; }
        Assert.IsNotNull(observed);
        if (cancel) Assert.IsInstanceOfType<OperationCanceledException>(observed);
        else Assert.AreSame(failure, observed);
        Assert.AreEqual(1, cache.Writes);

        // Read through a new cache-free store to establish persisted, not in-memory, evidence.
        var reopened = CreateSessionStore(temp.Path);
        var summary = await reopened.GetSessionSummaryAsync(source.SessionId);
        var state = await reopened.GetStateAsync(source.SessionId);
        Assert.IsNotNull(summary);
        Assert.IsNotNull(state);
        Assert.AreEqual("anthropic", summary.ProviderKey);
        Assert.AreEqual("target-model", summary.ModelId);
        Assert.AreEqual("openai", state.ProviderKey);
        Assert.AreEqual("original-continuation", state.ProviderSessionId);
        Assert.AreEqual("original-continuation", state.ProviderState!.Value.GetProperty("responseId").GetString());
        Assert.AreEqual(0, sourceExecutor.Requests.Count);
        Assert.AreEqual(0, targetExecutor.Requests.Count);
    }

    [TestMethod]
    public async Task AgentRuntime_SendAsync_EmitsTurnDiffForBuiltInFileChanges()
    {
        using var temp = TestTempDirectory.Create();
        var workspacePath = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspacePath);
        var executor = new FileWritingTurnExecutor();
        var agentRuntime = CreateAgentRuntime(temp.Path, executor);

        await using var session = await agentRuntime.CreateSessionAsync(
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
    public async Task AgentTurnFileChangeTracker_CreateUnifiedDiff_UsesPreciseDiffForLargeFiles()
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

        var tracker = new AgentTurnFileChangeTracker(temp.Path);
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
    public async Task AgentTurnFileChangeTracker_CreateUnifiedDiff_DoesNotMatchDifferentLineEndings()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "eol.txt");
        await File.WriteAllTextAsync(filePath, "alpha\r\nbeta\r\n").ConfigureAwait(false);

        var tracker = new AgentTurnFileChangeTracker(temp.Path);
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
    public async Task AgentRuntime_ResumeSession_RepairsRecoveredUsageUsingEquivalentModelIds()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out _);
        var store = new FileSystemAgentSessionStore(
            new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sessionId = "019d6a00-80ca-7f83-8407-92db7e0fae60";
        var createdAt = DateTimeOffset.Parse("2026-04-07T22:11:51.114932+00:00");
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
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
        var state = new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            UpdatedAt = createdAt,
            Usage = summary.Usage,
        };

        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);
        _ = await agentRuntime.ListModelsAsync().ConfigureAwait(false);

        await using var resumedSession = await agentRuntime.ResumeSessionAsync(
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
    public async Task AgentRuntime_ResumeSession_SkipsModelEnrichmentWhenCacheUnavailable()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out var executor);
        var store = new FileSystemAgentSessionStore(
            new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sessionId = "019e17b0-1b23-74a8-b1f1-a532f06c4ef2";
        var createdAt = DateTimeOffset.Parse("2026-05-11T15:10:00+00:00");
        var usage = new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.4-2026-03-05",
                InputTokens: 100,
                OutputTokens: 10),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.RecoveredHistory,
            UpdatedAt: createdAt);
        await store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = sessionId,
                ProviderId = ModelProviderIds.OpenAIResponses,
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                ModelId = "gpt-5.4",
                WorkingDirectory = "C:\\code\\CodeAlta",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Usage = usage,
            })
            .ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState
            {
                SessionId = sessionId,
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                UpdatedAt = createdAt,
                Usage = usage,
            })
            .ConfigureAwait(false);

        await using var resumedSession = await agentRuntime.ResumeSessionAsync(
            sessionId,
            new AgentSessionResumeOptions
            {
                WorkingDirectory = "C:\\code\\CodeAlta",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var repairedSummary = await store.GetSessionAsync("openai-responses", "openai", sessionId).ConfigureAwait(false);
        Assert.IsNotNull(repairedSummary?.Usage);
        Assert.IsNull(repairedSummary.Usage.TokenLimit);
        Assert.AreEqual(0, executor.ModelListRequests);
    }

    [TestMethod]
    public async Task AgentRuntime_ResumeSession_RecoversUsageFromPersistedHistoryEvents()
    {
        using var temp = TestTempDirectory.Create();
        var agentRuntime = CreateAgentRuntime(temp.Path, out var executor);
        var store = new FileSystemAgentSessionStore(
            new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var sessionId = "019e17b0-1b23-74a8-b1f1-a532f06c4ef1";
        var createdAt = DateTimeOffset.Parse("2026-05-11T15:00:00+00:00");
        var usageUpdatedAt = createdAt.AddMinutes(1);
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\code\\CodeAlta",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var state = new AgentSessionState
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
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: usageUpdatedAt);

        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);
        await store.AppendEventsAsync(
                "openai-responses",
                "openai",
                sessionId,
                [new AgentSessionUpdateEvent(
                    ModelProviderIds.OpenAIResponses,
                    sessionId,
                    usageUpdatedAt,
                    null,
                    AgentSessionUpdateKind.UsageUpdated,
                    "Usage updated.",
                    Usage: usage)])
            .ConfigureAwait(false);
        _ = await agentRuntime.ListModelsAsync().ConfigureAwait(false);

        await using var resumedSession = await agentRuntime.ResumeSessionAsync(
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

    private static AgentRuntime CreateAgentRuntime(string tempRoot, out RecordingTurnExecutor executor)
    {
        executor = new RecordingTurnExecutor();
        return CreateAgentRuntime(tempRoot, executor);
    }

    private static FileSystemAgentSessionStore CreateSessionStore(string tempRoot)
        => new(new AgentRuntimePathLayout(Path.Combine(tempRoot, "machine", "agents")));

    private static AgentRuntime CreateAgentRuntime(string tempRoot, IModelProviderTurnExecutor executor)
        => CreateAgentRuntime(
            tempRoot,
            executor,
            providerKey: "openai",
            displayName: "OpenAI",
            protocolFamily: "openai-responses",
            transportKind: AgentTransportKind.OpenAIResponses,
            ProviderId: ModelProviderIds.OpenAIResponses);

    private static AgentRuntime CreateAgentRuntime(
        string tempRoot,
        IModelProviderTurnExecutor executor,
        string providerKey,
        string displayName,
        string protocolFamily,
        AgentTransportKind transportKind,
        ModelProviderId ProviderId,
        IAgentSessionProjectionCache? projectionCache = null)
    {
        return new AgentRuntime(
            new ModelProviderId(ProviderId.Value),
            displayName,
            new AgentRuntimeOptions
            {
                StateRootPath = Path.Combine(tempRoot, "machine", "agents"),
                SessionProjectionCache = projectionCache,
                Providers =
                [
                    new AgentRuntimeProviderRegistration
                    {
                        Provider = new ModelProviderRuntimeDescriptor
                        {
                            ProtocolFamily = protocolFamily,
                            ProviderKey = providerKey,
                            DisplayName = displayName,
                            TransportKind = transportKind,
                            BaseUri = new Uri("https://api.openai.com/v1"),
                            IsDefault = true,
                            Profile = new AgentProviderProfile
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

    private sealed class TransferFaultCache(Action<AgentSessionCacheProjection> onWrite) : IAgentSessionProjectionCache
    {
        public int Writes { get; private set; }
        public Task UpsertSessionAsync(AgentSessionCacheProjection projection, CancellationToken cancellationToken = default)
        {
            Writes++;
            onWrite(projection);
            return Task.CompletedTask;
        }
        public Task<AgentSessionCacheProjection?> GetSessionAsync(string sessionId, AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSessionCacheProjection?>(null);
        public IAsyncEnumerable<AgentSessionCacheProjection> ListSessionsAsync(AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Transfer must not enumerate unrelated sessions.");
        public Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Transfer must not delete the original session.");
        public Task<AgentSessionCacheReconciliationResult> ReconcileAsync(AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Transfer must not reconcile unrelated sessions.");
    }

    private static async Task WriteLegacyProviderIdOnlyJournalAsync(
        string tempRoot,
        string sessionId,
        DateTimeOffset createdAt,
        string ProviderId,
        string? providerSessionId = null)
    {
        var layout = new AgentRuntimePathLayout(Path.Combine(tempRoot, "machine", "agents"));
        var sessionFile = layout.GetSessionFilePath(sessionId, createdAt);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);

        using var summaryDocument = JsonDocument.Parse(
            $$"""
              {
                "sessionId": "{{sessionId}}",
                "backendId": "{{ProviderId}}",
                "modelId": "legacy-model",
                "workingDirectory": "C:\\repo\\legacy",
                "createdAt": "{{createdAt:O}}",
                "updatedAt": "{{createdAt:O}}"
              }
              """);
        using var stateDocument = JsonDocument.Parse(
            $$"""
              {
                "sessionId": "{{sessionId}}",
                "providerSessionId": "{{providerSessionId}}",
                "updatedAt": "{{createdAt:O}}"
              }
              """);
        var events = new AgentEvent[]
        {
            new AgentRawEvent(
                new ModelProviderId(ProviderId),
                sessionId,
                createdAt,
                "local.sessionSummary",
                summaryDocument.RootElement.Clone()),
            new AgentRawEvent(
                new ModelProviderId(ProviderId),
                sessionId,
                createdAt,
                "local.sessionState",
                stateDocument.RootElement.Clone()),
        };

        await File.WriteAllLinesAsync(sessionFile, events.Select(static @event => @event.ToJson())).ConfigureAwait(false);
    }

    private static int CountUnifiedDiffLines(string diff, char prefix)
        => diff.Split('\n')
            .Count(line => line.StartsWith(prefix) &&
                           !line.StartsWith(new string(prefix, 3), StringComparison.Ordinal));

    private sealed class RecordingTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        public List<AgentTurnRequest> Requests { get; } = [];

        public int ModelListRequests { get; private set; }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
            CancellationToken cancellationToken = default)
        {
            ModelListRequests++;
            return Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo(
                    "gpt-5.4-2026-03-05",
                    "GPT-5.4",
                    Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["contextWindow"] = 1050000L,
                    }),
            ]);
        }

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text($"Echo #{Requests.Count}")]),
                });
        }
    }

    private sealed class FileWritingTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private int _requestCount;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
            [
                new AgentModelInfo("gpt-5.4", "GPT-5.4"),
            ]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.ToolCall("call-1", "apply_patch", arguments)]),
                    });
            }

            return Task.FromResult(
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Done.")]),
                });
        }
    }
}
