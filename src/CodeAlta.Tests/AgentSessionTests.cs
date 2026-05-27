using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Presentation.Formatting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentSessionTests
{
    [TestMethod]
    public void AgentInstructionComposer_ComposesDeveloperInstructionsAndLargestContextFilesPerDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var repoRoot = Path.Combine(temp.Path, "repo");
        var projectRoot = Path.Combine(repoRoot, "src", "Project");
        var workingDirectory = Path.Combine(projectRoot, "Nested");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.Combine(projectRoot, ".github"));
        var repoAgents = Path.Combine(repoRoot, "AGENTS.md");
        var repoClaude = Path.Combine(repoRoot, "CLAUDE.md");
        var projectAgents = Path.Combine(projectRoot, "AGENTS.md");
        var projectCopilot = Path.Combine(projectRoot, ".github", "copilot-instructions.md");
        File.WriteAllText(repoAgents, "root");
        File.WriteAllText(repoClaude, "root claude instructions are longer");
        File.WriteAllText(projectAgents, "project agents");
        File.WriteAllText(projectCopilot, "project copilot instructions are much longer than agents");

        var bundle = AgentInstructionComposer.Compose(
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
        StringAssert.Contains(bundle.RuntimeContext, $"Current date: {DateTimeOffset.Now:yyyy-MM-dd}");
        StringAssert.Contains(bundle.RuntimeContext, $"Platform: {GetExpectedPlatformLabel()}");
        StringAssert.Contains(bundle.RuntimeContext, $"Default shell for `shell_command`: `{GetExpectedDefaultShellLabel()}`");
        StringAssert.Contains(bundle.RuntimeContext, $"Current working directory: `{Path.GetFullPath(workingDirectory)}`");
        StringAssert.Contains(bundle.RuntimeContext, $"Project root: `{Path.GetFullPath(projectRoot)}`");
        StringAssert.Contains(bundle.DeveloperInstructions, repoClaude);
        StringAssert.Contains(bundle.DeveloperInstructions, "root claude instructions are longer");
        Assert.IsFalse(bundle.DeveloperInstructions.Contains(repoAgents, StringComparison.Ordinal));
        StringAssert.Contains(bundle.DeveloperInstructions, projectCopilot);
        StringAssert.Contains(bundle.DeveloperInstructions, "project copilot instructions are much longer than agents");
        Assert.IsFalse(bundle.DeveloperInstructions.Contains(projectAgents, StringComparison.Ordinal));
        Assert.AreEqual(64, bundle.InstructionHash.Length);
    }

    [TestMethod]
    public void AgentInstructionComposer_ComposesActiveSkillsSection()
    {
        var skillFilePath = @"C:\skills\code-review\SKILL.md";
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        var bundle = AgentInstructionComposer.Compose(
            new AgentSessionCreateOptions
            {
                Model = "gpt-5.4",
                WorkingDirectory = @"C:\repo",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            },
            [
                new AgentLoadedSkillState
                {
                    Name = "code-review",
                    SkillFilePath = skillFilePath,
                    SkillRootPath = Path.GetDirectoryName(skillFilePath)!,
                    SourceKind = "ProjectAlta",
                    SourceId = "project-alta:C:\\repo",
                    ActivatedAt = DateTimeOffset.Parse("2026-04-06T10:10:00+00:00"),
                    ActivationMode = "model",
                    ActivationId = "call-skill",
                    Payload = payload,
                },
            ]);

        Assert.IsNotNull(bundle.DeveloperInstructions);
        StringAssert.Contains(bundle.DeveloperInstructions, "<active_skills>");
        StringAssert.Contains(bundle.DeveloperInstructions, "code-review");
        StringAssert.Contains(bundle.DeveloperInstructions, "Review code for regressions.");
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_RunsToolLoopAndPersistsReplayableEvents()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-1");
        var state = CreateState("session-1");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var toolInvocations = new List<AgentToolInvocation>();
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                async (request, onUpdate, cancellationToken) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, $"Current date: {DateTimeOffset.Now:yyyy-MM-dd}");
                    StringAssert.Contains(request.DeveloperInstructions, $"Platform: {GetExpectedPlatformLabel()}");
                    StringAssert.Contains(request.DeveloperInstructions, $"Default shell for `shell_command`: `{GetExpectedDefaultShellLabel()}`");
                    StringAssert.Contains(request.DeveloperInstructions, $"Current working directory: `{Path.GetFullPath(temp.Path)}`");
                    await onUpdate(
                            new AgentTurnDelta
                            {
                                Kind = AgentContentKind.Assistant,
                                ContentId = "assistant-1",
                                Text = "thinking",
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    using var arguments = JsonDocument.Parse("""{"path":"sample.txt"}""");
                    return new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [
                                new AgentMessagePart.Text("Need to inspect a file."),
                                new AgentMessagePart.ToolCall("call-1", "inspect_file", arguments.RootElement.Clone()),
                            ]),
                        AssistantPartContentIds = ["assistant-1", null],
                        Usage = CreateUsageSnapshot(10, 5),
                        ProviderSessionId = "resp_123",
                    };
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.Tool, request.Conversation[^1].Role);
                    var toolResult = Assert.IsInstanceOfType<AgentMessagePart.ToolResult>(request.Conversation[^1].Parts.Single());
                    StringAssert.Contains(Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value, "sample.txt");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Done inspecting the file.")]),
                            Usage = CreateUsageSnapshot(18, 9),
                            ProviderSessionId = "resp_124",
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                ReasoningEffort = AgentReasoningEffort.High,
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
        var modelChangedIndex = FindEventIndex(history, static evt => evt is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.ModelChanged });
        var systemPromptIndex = FindEventIndex(history, static evt => evt is AgentSystemPromptEvent);
        var firstUserIndex = FindEventIndex(history, static evt => evt is AgentRawEvent { BackendEventType: "local.userMessage" });
        Assert.IsTrue(modelChangedIndex >= 0 && systemPromptIndex >= 0 && firstUserIndex >= 0 && modelChangedIndex < systemPromptIndex && systemPromptIndex < firstUserIndex);
        var modelChangedEvent = (AgentSessionUpdateEvent)history[modelChangedIndex];
        Assert.IsNull(modelChangedEvent.Message);
        Assert.AreEqual("openai", modelChangedEvent.Details?.GetProperty("providerKey").GetString());
        Assert.AreEqual("gpt-5.4", modelChangedEvent.Details?.GetProperty("modelId").GetString());
        Assert.AreEqual("High", modelChangedEvent.Details?.GetProperty("reasoningEffort").GetString());
        Assert.AreEqual("Model used: provider `openai`, model `gpt-5.4`, reasoning: `High`.", ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(modelChangedEvent));
        var systemPromptEvent = (AgentSystemPromptEvent)history[systemPromptIndex];
        Assert.AreEqual("session_start", systemPromptEvent.Reason);
        Assert.AreEqual("native-system-and-developer", systemPromptEvent.ProviderPayloadSummary.ChannelMapping);
        Assert.IsFalse(string.IsNullOrWhiteSpace(systemPromptEvent.EffectivePromptHash));
        Assert.IsFalse(string.IsNullOrWhiteSpace(systemPromptEvent.DeveloperInstructions));
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
        Assert.IsTrue(persistedHistory.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.ModelChanged));
        Assert.IsTrue(persistedHistory.OfType<AgentSystemPromptEvent>().Any());
        Assert.IsTrue(persistedHistory.OfType<AgentContentCompletedEvent>().Any(static evt => evt.Kind == AgentContentKind.Assistant));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_124", persistedState.ProviderSessionId);
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_PersistsErrorEventWhenTurnFails()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-turn-failure");
        var state = CreateState("session-turn-failure");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor((_, _, _) => throw new InvalidOperationException("provider failed before completion")),
            CreateOptions(provider, temp.Path));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first delegated prompt") }))
            .ConfigureAwait(false);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsTrue(persistedHistory.OfType<AgentContentCompletedEvent>().Any(static evt => evt.Kind == AgentContentKind.User));
        var error = persistedHistory.OfType<AgentErrorEvent>().Single();
        Assert.IsNotNull(error.RunId);
        Assert.AreEqual("provider failed before completion", error.Message);
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_ProjectsTurnSessionUpdatesAsTransientEvents()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-reconnect-update");
        var state = CreateState("session-reconnect-update");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new SessionUpdateTurnExecutor(),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        var runId = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Retry a transient stream failure"),
                })
            .ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        var reconnect = history.OfType<AgentSessionUpdateEvent>().Single(evt => evt.Kind == AgentSessionUpdateKind.Reconnecting);
        Assert.AreEqual(runId, reconnect.RunId);
        Assert.AreEqual("Reconnecting to ChatGPT/Codex... 1/5", reconnect.Message);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsFalse(persistedHistory.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.Reconnecting));
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_PersistsSkillActivationWithoutPromptPromotionBeforeCompaction()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-skill-activation") with { WorkingDirectory = temp.Path };
        var state = CreateState("session-skill-activation");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "code-review");
        Directory.CreateDirectory(skillRoot);
        var skillFilePath = Path.Combine(skillRoot, "SKILL.md");
        await File.WriteAllTextAsync(skillFilePath, "# Skill").ConfigureAwait(false);
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (_, _, _) =>
                {
                    using var arguments = JsonDocument.Parse("""{"skillName":"code-review"}""");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [
                                    new AgentMessagePart.Text("Load the review skill."),
                                    new AgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(7, 4),
                            ProviderSessionId = "resp_skill_1",
                        });
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(AgentConversationRole.Tool, request.Conversation[^1].Role);
                    var toolResult = Assert.IsInstanceOfType<AgentMessagePart.ToolResult>(request.Conversation[^1].Parts.Single());
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value,
                        "<skill_content");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Skill loaded.")]),
                            Usage = CreateUsageSnapshot(11, 6),
                            ProviderSessionId = "resp_skill_2",
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
                        new AgentToolSpec("codealta_skills_activate", "Activate a skill", schema.RootElement.Clone()),
                        (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(payload)]))),
                ],
            });

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Use the review skill.") }).ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.skillActivation"));
        Assert.IsTrue(history.OfType<AgentActivityEvent>().Where(static evt => evt.ActivityId == "call-skill").All(static evt => evt.Kind == AgentActivityKind.Skill));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual(1, persistedState.LoadedSkills.Count);
        Assert.AreEqual("code-review", persistedState.LoadedSkills[0].Name);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        await using var resumedSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            persistedState,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    Assert.IsFalse(
                        request.DeveloperInstructions.Contains("<active_skills>", StringComparison.Ordinal),
                        "Activated skills should stay in replayed conversation context until compaction promotes them into instructions.");
                    Assert.IsTrue(
                        request.Conversation
                            .SelectMany(static message => message.Parts)
                            .OfType<AgentMessagePart.ToolResult>()
                            .SelectMany(static part => part.Result.Items)
                            .OfType<AgentToolResultItem.Text>()
                            .Any(static item =>
                                item.Value.Contains("<skill_content", StringComparison.OrdinalIgnoreCase) &&
                                item.Value.Contains("code-review", StringComparison.Ordinal)),
                        "The activated skill payload should still be replayed as conversation context before compaction.");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Continuing with restored skill context.")]),
                            Usage = CreateUsageSnapshot(15, 5),
                            ProviderSessionId = "resp_skill_3",
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        _ = await resumedSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Continue.") }).ConfigureAwait(false);
        var resumedHistory = await resumedSession.GetHistoryAsync().ConfigureAwait(false);
        Assert.AreEqual(1, resumedHistory.OfType<AgentSystemPromptEvent>().Count());
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_UserSkillInputPersistsUserActivation()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-user-skill-activation") with { WorkingDirectory = temp.Path };
        var state = CreateState("session-user-skill-activation");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "code-review");
        Directory.CreateDirectory(skillRoot);
        var skillFilePath = Path.Combine(skillRoot, "SKILL.md");
        await File.WriteAllTextAsync(skillFilePath, "# Skill").ConfigureAwait(false);
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    Assert.IsFalse(
                        request.DeveloperInstructions.Contains("<active_skills>", StringComparison.Ordinal),
                        "User-activated skills are already present in the current user message and should not be duplicated into instructions before compaction.");
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    Assert.IsTrue(request.Conversation[0].Parts.OfType<AgentMessagePart.Text>().Any(part => part.Value.Contains("<skill_content", StringComparison.Ordinal)));
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Skill is active.")]),
                            Usage = CreateUsageSnapshot(8, 3),
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = new AgentInput(
                    [
                        new AgentInputItem.Skill("code-review", skillFilePath),
                        new AgentInputItem.Text(payload),
                    ]),
                })
            .ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.skillActivation"));
        Assert.IsTrue(history.OfType<AgentActivityEvent>().Any(static evt => evt.Kind == AgentActivityKind.Skill && evt.Phase == AgentActivityPhase.Completed));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual(1, persistedState.LoadedSkills.Count);
        Assert.AreEqual("code-review", persistedState.LoadedSkills[0].Name);
        Assert.AreEqual("user", persistedState.LoadedSkills[0].ActivationMode);
        Assert.IsTrue(persistedState.LoadedSkills[0].ActivationId.StartsWith("user-skill:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AgentSession_ResumeSession_DoesNotPromptPromoteMissingLoadedSkillsBeforeCompaction()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-missing-skill") with { WorkingDirectory = temp.Path };
        var state = CreateState("session-missing-skill");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "code-review");
        Directory.CreateDirectory(skillRoot);
        var skillFilePath = Path.Combine(skillRoot, "SKILL.md");
        await File.WriteAllTextAsync(skillFilePath, "# Skill").ConfigureAwait(false);
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        await using (var initialSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (_, _, _) =>
                {
                    using var arguments = JsonDocument.Parse("""{"skillName":"code-review"}""");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [
                                    new AgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(4, 2),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Loaded.")]),
                        Usage = CreateUsageSnapshot(6, 2),
                    })),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                Tools =
                [
                    new AgentToolDefinition(
                        new AgentToolSpec("codealta_skills_activate", "Activate a skill", schema.RootElement.Clone()),
                        (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(payload)]))),
                ],
            }))
        {
            _ = await initialSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Load skill.") }).ConfigureAwait(false);
        }

        File.Delete(skillFilePath);

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        await using var resumedSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            persistedState!,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    Assert.IsFalse(
                        request.DeveloperInstructions.Contains("<skill_missing", StringComparison.Ordinal),
                        "Missing loaded skills should not be promoted into prompt diagnostics until compaction requires prompt rehydration.");
                    Assert.IsTrue(
                        request.Conversation
                            .SelectMany(static message => message.Parts)
                            .OfType<AgentMessagePart.ToolResult>()
                            .SelectMany(static part => part.Result.Items)
                            .OfType<AgentToolResultItem.Text>()
                            .Any(static item => item.Value.Contains("<skill_content", StringComparison.OrdinalIgnoreCase)),
                        "The historical skill payload should remain replayable conversation context before compaction.");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Missing skill surfaced.")]),
                            Usage = CreateUsageSnapshot(8, 3),
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        _ = await resumedSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Continue.") }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_PreservesLoadedSkillsForReplay()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-compacted-skill") with { WorkingDirectory = temp.Path };
        var state = CreateState("session-compacted-skill");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "code-review");
        Directory.CreateDirectory(skillRoot);
        var skillFilePath = Path.Combine(skillRoot, "SKILL.md");
        await File.WriteAllTextAsync(skillFilePath, "# Skill").ConfigureAwait(false);
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        await using (var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    var summaryText = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    Assert.IsTrue(
                        summaryText.Contains("<conversation>", StringComparison.Ordinal) ||
                        summaryText.Contains("<codealta-oversized-anchor-request", StringComparison.Ordinal),
                        $"Unexpected compaction prompt: {summaryText}");
                    var responseText = request.SystemMessage?.Contains("oversized-anchor reducer", StringComparison.Ordinal) == true
                        ? """
                          ## Task
                          - Keep the activated skill available after compaction.
                          ## Explicit Requirements
                          - Preserve the skill identity.
                          - Keep base-directory guidance.
                          ## Files and Identifiers
                          - .alta/skills/code-review/SKILL.md
                          ## Exact Literals and Errors
                          - "code-review"
                          """
                        : "## Objective\nCompacted skill state";
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(responseText)]),
                        });
                },
                (_, _, _) =>
                {
                    using var arguments = JsonDocument.Parse("""{"skillName":"code-review"}""");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [
                                    new AgentMessagePart.Text("Activate the skill."),
                                    new AgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(9, 4),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Skill is active.")]),
                        Usage = CreateUsageSnapshot(13, 5),
                    })),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                Tools =
                [
                    new AgentToolDefinition(
                        new AgentToolSpec("codealta_skills_activate", "Activate a skill", schema.RootElement.Clone()),
                        (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(payload)]))),
                ],
            }))
        {
            _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Load skill.") }).ConfigureAwait(false);
            await session.CompactAsync().ConfigureAwait(false);
        }

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual(1, persistedState.LoadedSkills.Count);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        await using var resumedSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            persistedState,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, "<active_skills>");
                    StringAssert.Contains(request.DeveloperInstructions, "code-review");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Compacted session restored skill state.")]),
                            Usage = CreateUsageSnapshot(10, 3),
                        });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        _ = await resumedSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Continue after compaction.") }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_ConvertsToolExceptionsIntoFailedToolResults()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-tool-exception");
        var state = CreateState("session-tool-exception");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (_, _, _) =>
                {
                    using var arguments = JsonDocument.Parse("""{"url":"https://example.test/down"}""");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [
                                    new AgentMessagePart.Text("Trying the network tool."),
                                    new AgentMessagePart.ToolCall("call-1", "explode", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(5, 3),
                            ProviderSessionId = "resp_tool_1",
                        });
                },
                (request, _, _) =>
                {
                    var toolMessage = request.Conversation[^1];
                    Assert.AreEqual(AgentConversationRole.Tool, toolMessage.Role);
                    var toolResult = Assert.IsInstanceOfType<AgentMessagePart.ToolResult>(toolMessage.Parts.Single());
                    Assert.IsFalse(toolResult.Result.Success);
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value,
                        "Tool 'explode' failed: upstream failure");
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Recovered after tool failure.")]),
                            Usage = CreateUsageSnapshot(8, 4),
                            ProviderSessionId = "resp_tool_2",
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
                        new AgentToolSpec("explode", "Always fails", schema.RootElement.Clone()),
                        static (_, _) => throw new InvalidOperationException("upstream failure")),
                ],
            });

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Run the failing tool."),
                })
            .ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentRawEvent>().Any(static evt => evt.BackendEventType == "local.toolMessage"));
        Assert.IsTrue(history.OfType<AgentActivityEvent>().Any(static evt =>
            evt.ActivityId == "call-1" &&
            evt.Phase == AgentActivityPhase.Failed &&
            evt.Message is not null &&
            evt.Message.Contains("Tool 'explode' failed: upstream failure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_RefreshesEstimatedWindowUsageAfterToolOutputs()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-usage-refresh");
        var state = CreateState("session-usage-refresh");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var liveEvents = new List<AgentEvent>();
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        using var toolArguments = JsonDocument.Parse("""{"size":1}""");
        var initialUsage = CreateWindowUsageSnapshot(currentTokens: 780, tokenLimit: 1000, inputTokens: 760, outputTokens: 20);
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [
                                new AgentMessagePart.Text("Need tool output."),
                                new AgentMessagePart.ToolCall(
                                    "call-usage",
                                    "emit_large_output",
                                    toolArguments.RootElement.Clone()),
                            ]),
                        Usage = initialUsage,
                    }),
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.State.Usage);
                    Assert.IsTrue(request.State.Usage!.CurrentTokens > 850, $"Expected estimated tokens above threshold, got {request.State.Usage.CurrentTokens}.");
                    Assert.AreEqual(3, request.State.Usage.MessageCount);
                    Assert.AreEqual("Estimated active context", request.State.Usage.Window?.Label);
                    Assert.AreEqual(1000L, request.State.Usage.TokenLimit);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Done.")]),
                            Usage = request.State.Usage,
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
                        new AgentToolSpec("emit_large_output", "Emit large output", schema.RootElement.Clone()),
                        static (_, _) => Task.FromResult(
                            new AgentToolResult(
                                true,
                                [new AgentToolResultItem.Text(new string('x', 420))]))),
                ],
            });
        using var subscription = session.Subscribe(liveEvents.Add);

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Trigger tool output"),
                })
            .ConfigureAwait(false);

        Assert.IsTrue(liveEvents.OfType<AgentSessionUpdateEvent>().Any(static evt =>
            evt.Kind == AgentSessionUpdateKind.UsageUpdated &&
            evt.Usage?.Window?.Label == "Estimated active context" &&
            evt.Usage.CurrentTokens > 850 &&
            evt.Usage.LastOperation is null));
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_TruncatesOversizedToolOutputBeforeNextTurn()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-oversized-tool-output");
        var state = CreateState("session-oversized-tool-output");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var liveEvents = new List<AgentEvent>();
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        using var toolArguments = JsonDocument.Parse("""{"size":100000}""");
        var initialUsage = CreateWindowUsageSnapshot(currentTokens: 780, tokenLimit: 1000, inputTokens: 760, outputTokens: 20);
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [
                                new AgentMessagePart.Text("Need tool output."),
                                new AgentMessagePart.ToolCall(
                                    "call-oversized",
                                    "emit_oversized_output",
                                    toolArguments.RootElement.Clone()),
                            ]),
                        Usage = initialUsage,
                    }),
                (request, _, _) =>
                {
                    var toolResult = Assert.IsInstanceOfType<AgentMessagePart.ToolResult>(request.Conversation[^1].Parts.Single());
                    var text = Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value;
                    Assert.IsTrue(text.Length < 2_000, $"Expected model-visible tool output to be truncated, got {text.Length} characters.");
                    StringAssert.Contains(text, "was truncated from");
                    Assert.IsNotNull(request.State.Usage);
                    Assert.IsTrue(request.State.Usage!.CurrentTokens <= 1000, $"Expected usage to stay within the input limit, got {request.State.Usage.CurrentTokens}.");
                    Assert.AreEqual(1000L, request.State.Usage.TokenLimit);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Done.")]),
                            Usage = request.State.Usage,
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
                        new AgentToolSpec("emit_oversized_output", "Emit oversized output", schema.RootElement.Clone()),
                        static (_, _) => Task.FromResult(
                            new AgentToolResult(
                                true,
                                [new AgentToolResultItem.Text(new string('x', 100_000))]))),
                ],
            });
        using var subscription = session.Subscribe(liveEvents.Add);

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Trigger oversized tool output") }).ConfigureAwait(false);

        var usageEvent = liveEvents.OfType<AgentSessionUpdateEvent>()
            .LastOrDefault(static evt => evt.Kind == AgentSessionUpdateKind.UsageUpdated && evt.Usage?.LastOperation is null);
        Assert.IsNotNull(usageEvent);
        Assert.IsTrue(usageEvent.Usage!.CurrentTokens <= 1000, $"Expected live usage to stay within the input limit, got {usageEvent.Usage.CurrentTokens}.");
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_DoesNotResetWindowWhenProviderReportsContinuationDeltaUsage()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-continuation-usage");
        var state = CreateState("session-continuation-usage");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var models = new[]
        {
            new AgentModelInfo(
                "gpt-5.4",
                "GPT-5.4",
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["contextWindow"] = 272000L,
                }),
        };
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                models,
                summaryHandler: null,
                (request, _, _) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    return Task.FromResult(new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer.")]),
                        Usage = CreateWindowUsageSnapshot(50_000, 272_000, 49_700, 300),
                    });
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    return Task.FromResult(new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer.")]),
                        Usage = CreateWindowUsageSnapshot(1_200, 272_000, 1_100, 100),
                    });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            },
            allowProviderContinuation: true);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt") }).ConfigureAwait(false);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt") }).ConfigureAwait(false);

        var usageEvents = (await session.GetHistoryAsync().ConfigureAwait(false))
            .OfType<AgentSessionUpdateEvent>()
            .Where(static evt => evt.Kind == AgentSessionUpdateKind.UsageUpdated && evt.Usage?.LastOperation is not null)
            .ToArray();
        Assert.AreEqual(2, usageEvents.Length);
        Assert.AreEqual(50_000L, usageEvents[0].Usage?.CurrentTokens);
        Assert.IsTrue(usageEvents[1].Usage?.CurrentTokens > 50_000L, $"Expected continuation usage to build on the previous active window, got {usageEvents[1].Usage?.CurrentTokens}.");
        Assert.AreEqual("Estimated active context", usageEvents[1].Usage?.Window?.Label);
        Assert.AreEqual(1_100L, usageEvents[1].Usage?.LastOperation?.InputTokens);
        Assert.AreEqual(100L, usageEvents[1].Usage?.LastOperation?.OutputTokens);
    }

    [TestMethod]
    public async Task AgentSession_SteerAsync_QueuesPendingInputIntoSameRun()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-steer");
        var state = CreateState("session-steer");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var firstTurnStarted = new TaskCompletionSource<AgentRunId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                async (request, _, cancellationToken) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    firstTurnStarted.TrySetResult(request.RunId);
                    await releaseFirstTurn.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer.")]),
                    };
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    Assert.AreEqual(AgentConversationRole.Assistant, request.Conversation[1].Role);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[2].Role);
                    Assert.AreEqual(
                        "Please add one more detail.",
                        Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[2].Parts.Single()).Value);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Second answer.")]),
                        });
                }),
            CreateOptions(provider, temp.Path));

        var sendTask = session.SendAsync(new AgentSendOptions
        {
            Input = AgentInput.Text("Initial prompt"),
        });

        var activeRunId = await firstTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var steerRunId = await session.SteerAsync(
                new AgentSteerOptions
                {
                    Input = AgentInput.Text("Please add one more detail."),
                    ExpectedRunId = activeRunId,
                })
            .ConfigureAwait(false);

        releaseFirstTurn.TrySetResult();
        var completedRunId = await sendTask.ConfigureAwait(false);

        Assert.AreEqual(activeRunId, steerRunId);
        Assert.AreEqual(activeRunId, completedRunId);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        var userEvents = history
            .OfType<AgentContentCompletedEvent>()
            .Where(evt => evt.Kind == AgentContentKind.User)
            .ToArray();
        Assert.AreEqual(2, userEvents.Length);
        var expectedUserPrompts = new[] { "Initial prompt", "Please add one more detail." };
        CollectionAssert.AreEqual(
            expectedUserPrompts,
            userEvents.Select(static evt => evt.Content).ToArray());
        Assert.IsTrue(userEvents.All(evt => evt.RunId == completedRunId));
        Assert.AreEqual(1, history.OfType<AgentSessionUpdateEvent>().Count(static evt => evt.Kind == AgentSessionUpdateKind.Idle));
    }

    [TestMethod]
    public async Task AgentSession_ReplaysPersistedConversationOnResume()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-replay");
        var state = CreateState("session-replay");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var firstExecutor = new ScriptedTurnExecutor(
            (_, _, _) =>
            {
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Initial answer.")]),
                    });
            });

        await using (var firstSession = new AgentSession(
                         ModelProviderIds.OpenAIResponses,
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
                Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                Assert.AreEqual(AgentConversationRole.Assistant, request.Conversation[1].Role);
                Assert.AreEqual(AgentConversationRole.User, request.Conversation[2].Role);
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Follow-up answer.")]),
                    });
            });

        await using var resumedSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
    public async Task AgentSession_SendAsync_ResolvesEquivalentModelIdsForUsageLimits()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-model-alias");
        var state = CreateState("session-model-alias");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
    public async Task AgentSession_CompactAsync_PersistsCheckpointAndReplaysFromCheckpointPlusSuffix()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-compact");
        var state = CreateState("session-compact");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using (var session = new AgentSession(
                         ModelProviderIds.OpenAIResponses,
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
                                 Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                                 var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                                 StringAssert.Contains(payload, "<conversation>");
                                 return Task.FromResult(
                                     new AgentTurnResponse
                                     {
                                         AssistantMessage = new AgentConversationMessage(
                                             AgentConversationRole.Assistant,
                                             [new AgentMessagePart.Text(
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
                                 new AgentTurnResponse
                                 {
                                     AssistantMessage = new AgentConversationMessage(
                                         AgentConversationRole.Assistant,
                                         [new AgentMessagePart.Text("First answer " + new string('a', 120))]),
                                 }),
                             (_, _, _) => Task.FromResult(
                                 new AgentTurnResponse
                                 {
                                     AssistantMessage = new AgentConversationMessage(
                                         AgentConversationRole.Assistant,
                                         [new AgentMessagePart.Text("Second answer " + new string('b', 120))]),
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
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.KeptMessages.Count >= 1);
        Assert.IsTrue(checkpoint.SummarizedMessageCount >= 1);
        Assert.AreEqual(checkpoint.KeptMessages.Count, checkpoint.KeptMessageCount);
        Assert.IsTrue(checkpoint.SummaryCallCount >= 1);
        Assert.IsTrue(checkpoint.SummaryPromptInputTokens > 0);
        Assert.AreEqual(AgentCompactionSettings.DefaultPostCompactionTargetRatio, checkpoint.TargetRatio!.Value, 0.0001d);
        Assert.IsNotNull(checkpoint.TargetTokens);
        Assert.IsNotNull(checkpoint.TargetMet);
        Assert.IsFalse(string.IsNullOrWhiteSpace(checkpoint.TargetMissReason));
        if (checkpoint.TargetMet == true)
        {
            Assert.AreEqual("none", checkpoint.TargetMissReason);
        }

        Assert.IsNotNull(checkpoint.PlanningAttemptCount);

        var completedUpdate = persistedHistory
            .OfType<AgentSessionUpdateEvent>()
            .Single(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted);
        Assert.IsNotNull(completedUpdate.Details);
        Assert.AreEqual("codealta.localCompaction.v1", completedUpdate.Details.Value.GetProperty("schema").GetString());
        Assert.AreEqual(checkpoint.Summary, completedUpdate.Details.Value.GetProperty("summaryMarkdown").GetString());
        Assert.AreEqual(checkpoint.SummaryCallCount, completedUpdate.Details.Value.GetProperty("summaryCallCount").GetInt32());
        Assert.AreEqual(checkpoint.TargetTokens, completedUpdate.Details.Value.GetProperty("targetTokens").GetInt64());
        Assert.AreEqual(checkpoint.TargetMet, completedUpdate.Details.Value.GetProperty("targetMet").GetBoolean());

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionEventOffset);
        Assert.AreEqual("manual", persistedState.LastCompactionTrigger);
        Assert.AreEqual(checkpoint.ContentId, persistedState.CompactionCheckpointEventId);

        await using var resumedSession = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            persistedState,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsTrue(request.Conversation.Count >= 3);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                        "## Objective");
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[^1].Role);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Post-compaction answer.")]),
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
    public async Task AgentSession_CompactWithOutcomeAsync_PublishesStartedBeforeSummaryCompletes()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with { Enabled = false });
        var summary = CreateSummary("session-compact-started");
        var state = CreateState("session-compact-started");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSummary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveEvents = new List<AgentEvent>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                async (request, _, _) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    summaryStarted.TrySetResult();
                    await releaseSummary.Task.ConfigureAwait(false);
                    return new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text(
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
                    };
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 120))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 120))]),
                    })),
            CreateOptions(provider, temp.Path));
        using var subscription = session.Subscribe(liveEvents.Add);

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 140)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 140)) }).ConfigureAwait(false);
        liveEvents.Clear();

        var compactTask = ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync();
        await summaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.IsTrue(liveEvents.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionStarted));
        Assert.IsFalse(liveEvents.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted));

        releaseSummary.SetResult();
        var outcome = await compactTask.ConfigureAwait(false);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(liveEvents.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted));
    }

    [TestMethod]
    public void AgentCompactionPlanner_ThresholdPreparation_ProtectsLatestUserMessage()
    {
        var instructionBundle = AgentInstructionComposer.Compose(
            new AgentSessionCreateOptions
            {
                ProviderKey = "openai",
                Model = "gpt-5.4",
                WorkingDirectory = Environment.CurrentDirectory,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });
        var firstUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("First prompt " + new string('x', 420))]);
        var firstAssistantMessage = new AgentConversationMessage(
            AgentConversationRole.Assistant,
            [new AgentMessagePart.Text(new string('A', 420))]);
        var secondUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("Second prompt " + new string('y', 40))]);
        var conversation = new[] { firstUserMessage, firstAssistantMessage, secondUserMessage };
        var estimatedPromptTokens = AgentTokenEstimator.EstimatePromptTokens(
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            conversation,
            usage: null).Tokens;
        var fixedTokens = AgentTokenEstimator.EstimatePromptTokens(
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            [],
            usage: null).Tokens;
        var anchorTokens = AgentTokenEstimator.EstimateMessage(secondUserMessage);
        var inputContextLimit = (long)Math.Ceiling((fixedTokens + 64 + anchorTokens + 20) / AgentCompactionSettings.DefaultPostCompactionTargetRatio);
        var compactionRatio = Math.Max(0.20d, Math.Min(0.95d, (estimatedPromptTokens - 1d) / inputContextLimit));

        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            instructionBundle.SystemMessage,
            instructionBundle.DeveloperInstructions,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: inputContextLimit + 20 + 10,
                InputContextLimit: inputContextLimit,
                MaxOutputTokens: 128),
            AgentCompactionSettings.Default with
            {
                Ratio = compactionRatio,
                KeepLastUserMessage = true,
                AllowSplitTurn = true,
            },
            anchorContentId: "user:2");

        Assert.IsNotNull(preparation);
        Assert.AreEqual("user:2", preparation!.AnchorContentId);
        Assert.IsTrue(preparation.MessagesToSummarize.Count >= 1);
        Assert.IsTrue(preparation.TurnPrefixMessages.Contains(secondUserMessage) || preparation.MessagesToKeep.Contains(secondUserMessage));
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_KeepsContiguousNewestSuffix()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("u1 " + new string('a', 320))]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("a1")]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("u2 " + new string('b', 320))]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("a2")]),
        };

        var lastMessageTokens = AgentTokenEstimator.EstimateMessage(conversation[^1]);
        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 2000,
                InputContextLimit: 500,
                MaxOutputTokens: 128),
            AgentCompactionSettings.Default with
            {
                Ratio = 0.80,
                KeepLastUserMessage = false,
                AllowSplitTurn = true,
            },
            checkpointTokenEstimate: 64,
            promptBudgetOverride: lastMessageTokens + 96);

        Assert.IsNotNull(preparation);
        CollectionAssert.AreEqual(new[] { conversation[^1] }, preparation!.MessagesToKeep.ToArray());
        CollectionAssert.AreEqual(
            new[] { conversation[0], conversation[1], conversation[2] },
            preparation.MessagesToSummarize.ToArray());
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_TargetsLowerRetainedPromptBudget()
    {
        var latestUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("Latest prompt")]);
        var suffixMessages = Enumerable.Range(0, 20)
            .Select(index => new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [new AgentMessagePart.Text($"post-anchor assistant {index} " + new string('a', 240))]))
            .ToArray();
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Earlier prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Earlier answer")]),
            latestUserMessage,
        }.Concat(suffixMessages).ToArray();

        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Manual,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 2_500,
                InputContextLimit: 2_000,
                MaxOutputTokens: 500),
            AgentCompactionSettings.Default,
            checkpointTokenEstimate: 64);

        Assert.IsNotNull(preparation);
        CollectionAssert.AreEqual(new[] { latestUserMessage }, preparation!.TurnPrefixMessages.ToArray());
        Assert.IsTrue(preparation.MessagesToKeep.Count < suffixMessages.Length);
        Assert.IsTrue(preparation.MessagesToSummarize.Any(message => suffixMessages.Contains(message)));
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_AllowsExplicitFallbackPromptBudgetUpToHardInputLimit()
    {
        var latestUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("Latest prompt")]);
        var suffixMessages = Enumerable.Range(0, 20)
            .Select(index => new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [new AgentMessagePart.Text($"post-anchor assistant {index} " + new string('a', 240))]))
            .ToArray();
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Earlier prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Earlier answer")]),
            latestUserMessage,
        }.Concat(suffixMessages).ToArray();

        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Manual,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 2_500,
                InputContextLimit: 2_000,
                MaxOutputTokens: 500),
            AgentCompactionSettings.Default,
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 2_000);

        Assert.IsNotNull(preparation);
        var retainedTokens = preparation!.TurnPrefixMessages.Concat(preparation.MessagesToKeep).Sum(AgentTokenEstimator.EstimateMessage);
        Assert.IsTrue(
            retainedTokens <= 2_000,
            $"Expected explicit fallback prompt tokens to stay within the hard input limit, but got {retainedTokens}.");
        Assert.IsTrue(
            retainedTokens > 1_000,
            $"Expected the explicit hard-fit fallback to be allowed above the default preferred target, but got {retainedTokens}.");
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_ThrowsWhenSplitTurnDisabled()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("First prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("First answer")]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Latest prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(new string('x', 480))]),
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 2000,
                InputContextLimit: 300,
                MaxOutputTokens: 128),
            AgentCompactionSettings.Default with
            {
                Ratio = 0.80,
                KeepLastUserMessage = true,
                AllowSplitTurn = false,
            },
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 120));
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_PrefersRecentHighSignalToolOutput()
    {
        using var oldArgs = JsonDocument.Parse("""{"path":"old.log"}""");
        using var recentArgs = JsonDocument.Parse("""{"command":"dotnet test"}""");
        var oldAssistant = new AgentConversationMessage(
            AgentConversationRole.Assistant,
            [
                new AgentMessagePart.Reasoning("Old exploratory reasoning that should be cheaper to omit."),
                new AgentMessagePart.ToolCall("call-old", "read_file", oldArgs.RootElement.Clone()),
            ]);
        var oldTool = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [
                new AgentMessagePart.ToolResult(
                    "call-old",
                    new AgentToolResult(true, [new AgentToolResultItem.Text("old output marker " + new string('a', 8_000))])),
            ]);
        var recentAssistant = new AgentConversationMessage(
            AgentConversationRole.Assistant,
            [
                new AgentMessagePart.Text("Recent verification."),
                new AgentMessagePart.ToolCall("call-new", "shell_command", recentArgs.RootElement.Clone()),
            ]);
        var recentTool = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [
                new AgentMessagePart.ToolResult(
                    "call-new",
                    new AgentToolResult(
                        false,
                        [new AgentToolResultItem.Text("build failed" + Environment.NewLine + "CS1591: Missing XML comment")],
                        Error: "build failed")),
            ]);

        var preparation = new AgentCompactionPreparation(
            Trigger: AgentCompactionTrigger.Threshold,
            MessagesToSummarize: [oldAssistant, oldTool],
            TurnPrefixMessages: [],
            MessagesToKeep: [recentAssistant, recentTool],
            AnchorContentId: "user:latest",
            IsSplitTurn: false,
            TokensBefore: new AgentTokenEstimate(1000, "test", IsEstimated: true),
            PreviousSummary: null);
        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Finish the fix",
            readFiles: [],
            modifiedFiles: [],
            AgentCompactionSettings.Default);

        StringAssert.Contains(result.UserMessage, "build failed");
        StringAssert.Contains(result.UserMessage, "callId=call-old");
        StringAssert.Contains(result.UserMessage, "[Assistant reasoning summary]");
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_EnforcesGlobalToolOutputCapAcrossManyOutputs()
    {
        var messagesToSummarize = new List<AgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 8))
        {
            messagesToSummarize.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall($"call-{index}", "shell_command", JsonSerializer.SerializeToElement(new { command = $"step {index}" }))]));
            messagesToSummarize.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [
                        new AgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(
                                Success: true,
                                [new AgentToolResultItem.Text($"result {index}: " + new string((char)('a' + index), 2_000))],
                                Error: null)),
                    ]));
        }

        var preparation = new AgentCompactionPreparation(
            Trigger: AgentCompactionTrigger.Threshold,
            MessagesToSummarize: messagesToSummarize,
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: "user:latest",
            IsSplitTurn: false,
            TokensBefore: new AgentTokenEstimate(3000, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            AgentCompactionSettings.Default);

        Assert.IsTrue(result.Statistics.SerializedToolResultCharacters <= 6_000);
        Assert.IsTrue(result.Statistics.OmittedToolResultCount >= 1);
        StringAssert.Contains(result.UserMessage, "callId=call-8");
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_OmitsReasoningWhenBudgetIsExhausted()
    {
        var preparation = new AgentCompactionPreparation(
            Trigger: AgentCompactionTrigger.Manual,
            MessagesToSummarize: Enumerable.Range(1, 6)
                .Select(index => new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.Reasoning($"Reasoning block {index} " + new string((char)('a' + index), 1_200))]))
                .ToArray(),
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: null,
            IsSplitTurn: false,
            TokensBefore: new AgentTokenEstimate(800, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            AgentCompactionSettings.Default);

        Assert.IsTrue(result.Statistics.SerializedReasoningCharacters <= 3_000);
        Assert.IsTrue(result.Statistics.OmittedReasoningCount >= 1);
        StringAssert.Contains(result.UserMessage, "[Assistant reasoning summary]");
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_RendersModifiedFilesBeforeReadFiles()
    {
        var preparation = new AgentCompactionPreparation(
            Trigger: AgentCompactionTrigger.Manual,
            MessagesToSummarize:
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Continue")]),
            ],
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: null,
            IsSplitTurn: false,
            TokensBefore: new AgentTokenEstimate(100, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: ["C:\\repo\\older-read.cs", "C:\\repo\\recent-read.cs"],
            modifiedFiles: ["C:\\repo\\recent-edit.cs"],
            settings: AgentCompactionSettings.Default);

        var modifiedIndex = result.UserMessage.IndexOf("### Modified", StringComparison.Ordinal);
        var readIndex = result.UserMessage.IndexOf("### Read", StringComparison.Ordinal);
        Assert.IsTrue(modifiedIndex >= 0);
        Assert.IsTrue(readIndex > modifiedIndex);
    }

    [TestMethod]
    public void AgentCompactionCanonicalizer_Normalize_CollapsesRepeatedReadFileOperations()
    {
        var messages = new List<AgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 3))
        {
            messages.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall($"call-{index}", "read_file", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent/Runtime/Compaction/AgentCompactionSerializer.cs" }))]));
            messages.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [
                        new AgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(true, [new AgentToolResultItem.Text($"file contents chunk {index} " + new string('x', 120))])),
                    ]));
        }

        var units = AgentCompactionCanonicalizer.Normalize(messages);
        Assert.AreEqual(1, units.Count);
        var collapsed = Assert.IsInstanceOfType<AgentCompactionToolInteractionUnit>(units[0]);
        Assert.IsTrue(collapsed.IsCollapsed);
        Assert.AreEqual(3, collapsed.RepeatCount);
        Assert.AreEqual(1, collapsed.ToolCalls.Count);
        Assert.AreEqual(1, collapsed.ToolResults.Count);
    }

    [TestMethod]
    public void AgentCompactionChunker_CreateChunks_KeepsToolInteractionAdjacent()
    {
        var introMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("Intro " + new string('x', 180))]);
        var assistantToolCall = new AgentConversationMessage(
            AgentConversationRole.Assistant,
            [new AgentMessagePart.ToolCall("call-1", "grep", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent", pattern = "compaction" }))]);
        var toolResult = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("match line")]))]);

        var maxTokens = AgentTokenEstimator.EstimateMessage(introMessage) + AgentTokenEstimator.EstimateMessage(assistantToolCall) + 4;
        var chunks = AgentCompactionChunker.CreateChunks(
            [introMessage, assistantToolCall, toolResult],
            (int)maxTokens,
            static chunk => chunk.Sum(AgentTokenEstimator.EstimateMessage));

        Assert.AreEqual(2, chunks.Count);
        CollectionAssert.AreEqual(new[] { introMessage }, chunks[0].ToArray());
        CollectionAssert.AreEqual(new[] { assistantToolCall, toolResult }, chunks[1].ToArray());
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_CollapsesRepeatedLowValueToolActivity()
    {
        var messagesToSummarize = new List<AgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 3))
        {
            messagesToSummarize.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall($"call-{index}", "grep", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent", pattern = "compaction" }))]));
            messagesToSummarize.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [
                        new AgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(true, [new AgentToolResultItem.Text($"match {index}: src/CodeAlta.Agent/File{index}.cs")])),
                    ]));
        }

        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            new AgentCompactionPreparation(
                Trigger: AgentCompactionTrigger.Threshold,
                MessagesToSummarize: messagesToSummarize,
                TurnPrefixMessages: [],
                MessagesToKeep: [],
                AnchorContentId: null,
                IsSplitTurn: false,
                TokensBefore: new AgentTokenEstimate(2000, "test", IsEstimated: true),
                PreviousSummary: null),
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            settings: AgentCompactionSettings.Default);

        StringAssert.Contains(result.UserMessage, "repeated 3 times");
        StringAssert.Contains(result.UserMessage, "repeated successful grep activity");
        Assert.IsFalse(result.UserMessage.Contains("match 1:", StringComparison.Ordinal));
        Assert.IsFalse(result.UserMessage.Contains("match 2:", StringComparison.Ordinal));
        Assert.AreEqual(3, result.Statistics.TotalToolCallCount);
        Assert.AreEqual(1, result.Statistics.SerializedToolCallCount);
        Assert.AreEqual(2, result.Statistics.CollapsedToolCallCount);
        Assert.AreEqual(3, result.Statistics.TotalToolResultCount);
        Assert.AreEqual(1, result.Statistics.SerializedToolResultCount);
        Assert.AreEqual(0, result.Statistics.SerializedToolResultExcerptCount);
    }

    [TestMethod]
    public void AgentCompactionSerializer_BuildSummaryRequestBody_KeepsAllPlaintextMessagesWhenInputFits()
    {
        var summarizedMessages = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("First prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("First answer")]),
        };
        var keptMessages = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Second prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Second answer")]),
        };

        var preparation = new AgentCompactionPreparation(
            Trigger: AgentCompactionTrigger.Threshold,
            MessagesToSummarize: summarizedMessages,
            TurnPrefixMessages: [],
            MessagesToKeep: keptMessages,
            AnchorContentId: "user:2",
            IsSplitTurn: false,
            TokensBefore: new AgentTokenEstimate(200, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Second prompt",
            readFiles: [],
            modifiedFiles: [],
            settings: AgentCompactionSettings.Default);

        Assert.AreEqual(result.TotalMessageCount, result.IncludedMessageCount);
        StringAssert.Contains(result.UserMessage, "First prompt");
        StringAssert.Contains(result.UserMessage, "First answer");
        StringAssert.Contains(result.UserMessage, "Second prompt");
        StringAssert.Contains(result.UserMessage, "Second answer");
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_KeepsNewestMessageWithoutAnchor()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("u1 " + new string('a', 320))]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("a1 " + new string('b', 320))]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("u2 " + new string('c', 220))]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("a2 " + new string('d', 220))]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("u3")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("a3")]),
        };

        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 4096,
                InputContextLimit: 4096,
                MaxOutputTokens: 512),
            (AgentCompactionSettings.Default with
            {
                KeepLastUserMessage = false,
            }),
            checkpointTokenEstimate: 64);

        Assert.IsNotNull(preparation);
        Assert.AreEqual(1, preparation!.MessagesToKeep.Count);
        Assert.AreEqual(AgentConversationRole.Assistant, preparation.MessagesToKeep[0].Role);
        StringAssert.Contains(Assert.IsInstanceOfType<AgentMessagePart.Text>(preparation.MessagesToKeep[0].Parts.Single()).Value, "a3");
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_WiderPlanKeepsMoreContextThanAnchorOnlyFallback()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Earlier prompt " + new string('a', 320))]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Earlier answer " + new string('b', 320))]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Latest prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Recent answer " + new string('x', 320))]),
        };

        var budget = new AgentTokenBudget(
            TotalContextEnvelope: 4096,
            InputContextLimit: 4096,
            MaxOutputTokens: 256);
        var settings = AgentCompactionSettings.Default;

        var widenedPreparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            budget,
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 260,
            keepAnchorOnly: false);
        var anchorOnlyPreparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            budget,
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 260,
            keepAnchorOnly: true);

        Assert.IsNotNull(widenedPreparation);
        Assert.IsNotNull(anchorOnlyPreparation);
        Assert.IsTrue(widenedPreparation!.MessagesToKeep.Count > anchorOnlyPreparation!.MessagesToKeep.Count);
        Assert.AreEqual(1, anchorOnlyPreparation.TurnPrefixMessages.Count);
        Assert.AreEqual(AgentConversationRole.User, anchorOnlyPreparation.TurnPrefixMessages[0].Role);
    }

    [TestMethod]
    public void AgentCompactionPlanner_LargeToolHeavyConversation_FitsInputLimit()
    {
        var conversation = new List<AgentConversationMessage>
        {
            new(AgentConversationRole.User, [new AgentMessagePart.Text("Initial request")]),
        };

        foreach (var index in Enumerable.Range(1, 24))
        {
            conversation.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall($"call-{index}", "read_file", JsonSerializer.SerializeToElement(new { path = $"src/File{index}.cs" }))]));
            conversation.Add(
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [
                        new AgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(
                                true,
                                [new AgentToolResultItem.Text($"file dump {index} " + new string((char)('a' + (index % 20)), 2400))])),
                    ]));
        }

        conversation.Add(new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Latest request")]));

        var settings = AgentCompactionSettings.Default;
        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 400_000,
                InputContextLimit: 400_000,
                MaxOutputTokens: 4_096),
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 128);

        Assert.IsNotNull(preparation);
        var checkpoint = new AgentCompactionCheckpoint
        {
            Version = 2,
            ContentId = "compaction:test",
            Trigger = "manual",
            Summary =
                """
                ## Objective
                Continue the task.
                ## Active User Request
                Latest request
                ## Constraints
                - Keep only continuation-critical details.
                ## Progress
                ### Done
                - Earlier file inspection activity was summarized.
                ### In Progress
                - Continue from the latest request.
                ### Blocked
                - None recorded.
                ## Decisions
                - Omit repetitive file dumps.
                ## Next Steps
                - Continue implementation.
                ## Critical Context
                - The session was dominated by repetitive tool output.
                ## Relevant Files
                - None tracked.
                """,
            TokensBefore = preparation!.TokensBefore.Tokens,
            SummarizedMessageCount = preparation.MessagesToSummarize.Count,
        };

        var compactedConversation = new List<AgentConversationMessage> { checkpoint.CreateMessage() };
        compactedConversation.AddRange(preparation.TurnPrefixMessages);
        compactedConversation.AddRange(preparation.MessagesToKeep);

        var tokensAfter = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage: null,
            developerInstructions: null,
            compactedConversation,
            usage: null).Tokens;
        Assert.IsTrue(tokensAfter <= 400_000);
    }

    [TestMethod]
    public void AgentCompactionPlanner_Preparation_ReducesOversizedLatestUserAnchorByDefault()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Earlier prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Earlier answer")]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Huge prompt " + new string('x', 2200))]),
        };

        var preparation = AgentCompactionPlanner.Prepare(
            AgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new AgentTokenBudget(
                TotalContextEnvelope: 800,
                InputContextLimit: 400,
                MaxOutputTokens: 128),
            AgentCompactionSettings.Default,
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
    public void AgentTokenEstimator_ImageData_UsesBoundedMediaEstimate()
    {
        var base64Image = Convert.ToBase64String(new byte[512 * 1024]);
        var message = new AgentConversationMessage(
            AgentConversationRole.User,
            [
                new AgentMessagePart.Text("Inspect this screenshot."),
                new AgentMessagePart.Data(base64Image, "image/png", "screenshot.png"),
            ]);

        var estimatedTokens = AgentTokenEstimator.EstimateMessage(message);

        Assert.IsTrue(
            estimatedTokens < 2_000,
            $"Expected image data to use a bounded media estimate instead of base64 length, but got {estimatedTokens} tokens.");
    }

    [TestMethod]
    public void AgentMediaCompaction_PruneInlineImages_ReplacesImageDataWithPlaceholder()
    {
        var base64Image = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6 });
        var message = new AgentConversationMessage(
            AgentConversationRole.User,
            [
                new AgentMessagePart.Text("Look at this."),
                new AgentMessagePart.Data(base64Image, "image/png", "screenshot.png"),
            ]);

        var result = AgentMediaCompaction.PruneInlineImages([message]);

        Assert.AreEqual(1, result.PrunedImageCount);
        Assert.AreEqual(base64Image.Length, result.PrunedBase64Characters);
        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual(2, result.Messages[0].Parts.Count);
        Assert.IsInstanceOfType<AgentMessagePart.Text>(result.Messages[0].Parts[0]);
        var placeholder = Assert.IsInstanceOfType<AgentMessagePart.Text>(result.Messages[0].Parts[1]).Value;
        StringAssert.Contains(placeholder, "Image attachment omitted from retained context");
        StringAssert.Contains(placeholder, "mediaType=image/png");
        Assert.IsFalse(result.Messages[0].Parts.OfType<AgentMessagePart.Data>().Any());
        Assert.IsFalse(placeholder.Contains(base64Image, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_WhenOnlyInlineImagesArePrunable_RemovesImageDataFromKeptMessages()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-image-media-only");
        var state = CreateState("session-image-media-only");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var imageBytes = new byte[256 * 1024];
        Array.Fill<byte>(imageBytes, 42);
        var imageBase64 = Convert.ToBase64String(imageBytes);
        var imagePath = Path.Combine(temp.Path, "screenshot.png");
        File.WriteAllBytes(imagePath, imageBytes);

        var summaryPayloads = new List<string>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    StringAssert.Contains(payload, "base64 omitted");
                    Assert.IsFalse(payload.Contains(imageBase64, StringComparison.Ordinal));
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Continue the image review.
                                    ## Active User Request
                                    Inspect the screenshot.
                                    ## Constraints
                                    - Do not retain inline image bytes in compacted history.
                                    ## Progress
                                    ### Done
                                    - Noted the screenshot attachment without copying image bytes.
                                    ### In Progress
                                    - Continue from compacted context.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Represent old images as attachment metadata.
                                    ## Next Steps
                                    - Continue the review if requested.
                                    ## Critical Context
                                    - The prior user turn included a screenshot attachment.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.IsTrue(request.Conversation[0].Parts.OfType<AgentMessagePart.Data>().Any());
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("The screenshot was received.")]),
                        });
                }),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = new AgentInput(
                    [
                        new AgentInputItem.Text("Inspect the screenshot."),
                        new AgentInputItem.LocalImage(imagePath, "screenshot.png", "image/png"),
                    ]),
                })
            .ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryPayloads.Count);
        Assert.AreNotEqual("Nothing to compact.", outcome.Message);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Single(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.AreEqual(0, checkpoint!.SummarizedMessageCount);
        Assert.IsFalse(checkpoint.KeptMessages.SelectMany(static message => message.Parts).OfType<AgentMessagePart.Data>().Any());
        var retainedText = string.Join("\n", checkpoint.KeptMessages.SelectMany(static message => message.Parts.OfType<AgentMessagePart.Text>()).Select(static part => part.Value));
        StringAssert.Contains(retainedText, "Image attachment omitted from retained context");
        Assert.IsFalse(retainedText.Contains(imageBase64, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_DoesNotReplayPastInlineImagesOnLaterTurns()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-image-current-turn-only");
        var state = CreateState("session-image-current-turn-only");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var imageBytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var imageBase64 = Convert.ToBase64String(imageBytes);
        var imagePath = Path.Combine(temp.Path, "screenshot.png");
        File.WriteAllBytes(imagePath, imageBytes);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.IsTrue(request.Conversation[0].Parts.OfType<AgentMessagePart.Data>().Any());
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Saw the image.")]),
                        });
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                    Assert.IsFalse(request.Conversation[0].Parts.OfType<AgentMessagePart.Data>().Any());
                    var firstTurnText = string.Join(
                        "\n",
                        request.Conversation[0].Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
                    StringAssert.Contains(firstTurnText, "Inspect the screenshot.");
                    StringAssert.Contains(firstTurnText, "Image attachment omitted from retained context");
                    Assert.IsFalse(firstTurnText.Contains(imageBase64, StringComparison.Ordinal));
                    Assert.AreEqual(AgentConversationRole.User, request.Conversation[^1].Role);
                    Assert.IsFalse(request.Conversation[^1].Parts.OfType<AgentMessagePart.Data>().Any());
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Continuing without replaying the old image.")]),
                        });
                }),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = new AgentInput(
                    [
                        new AgentInputItem.Text("Inspect the screenshot."),
                        new AgentInputItem.LocalImage(imagePath, "screenshot.png", "image/png"),
                    ]),
                })
            .ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Continue.") }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_UsesSummarizerExecutorAndPreviousSummaryOnUpdate()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-update");
        var state = CreateState("session-summary-update");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
                    var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Third answer " + new string('c', 160))]),
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
    public async Task AgentSession_CompactAsync_DefaultSettings_DoNotPretrimWhenSummaryInputFits()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default);
        var summary = CreateSummary("session-default-settings-fit");
        var state = CreateState("session-default-settings-fit");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (request, _, _) =>
                {
                    summaryPayloads.Add(Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Continue the task.
                                    ## Active User Request
                                    Third prompt
                                    ## Constraints
                                    - Preserve the selected history.
                                    ## Progress
                                    ### Done
                                    - Earlier work summarized.
                                    ### In Progress
                                    - Continue the implementation.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Keep the selected history because it fits.
                                    ## Next Steps
                                    - Continue from the checkpoint.
                                    ## Critical Context
                                    - The selected history fit in one compaction request.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 800))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 800))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Third answer " + new string('c', 800))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 900)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 900)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Third prompt " + new string('z', 900)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryPayloads.Count);
        StringAssert.Contains(summaryPayloads[0], "First prompt");
        StringAssert.Contains(summaryPayloads[0], "First answer");
        StringAssert.Contains(summaryPayloads[0], "Second prompt");
        StringAssert.Contains(summaryPayloads[0], "Second answer");
        StringAssert.Contains(summaryPayloads[0], "Third prompt");
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_UsesTargetAwareSummaryOutputLimitBeforeProviderClamp()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            SummaryOutputRatio = 0.20,
        });
        var summary = CreateSummary("session-summary-output-limit");
        var state = CreateState("session-summary-output-limit");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var observedMaxOutputTokens = new List<int?>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo(
                    "gpt-5.4",
                    "GPT-5.4",
                    Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["contextWindow"] = 4096L,
                        ["inputTokenLimit"] = 3072L,
                        ["outputTokenLimit"] = 320L,
                    })],
                (request, _, _) =>
                {
                    observedMaxOutputTokens.Add(request.MaxOutputTokens);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        CollectionAssert.AreEqual(new int?[] { 122 }, observedMaxOutputTokens);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_CapsSummaryOutputTokenLimitByInputRatio()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-output-ratio");
        var state = CreateState("session-summary-output-ratio");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var observedMaxOutputTokens = new List<int?>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo(
                    "gpt-5.4",
                    "GPT-5.4",
                    Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["contextWindow"] = 20_000L,
                        ["inputTokenLimit"] = 10_000L,
                        ["outputTokenLimit"] = 8_000L,
                    })],
                (request, _, _) =>
                {
                    observedMaxOutputTokens.Add(request.MaxOutputTokens);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
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
                                    - Cap summary output by input ratio.
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        CollectionAssert.AreEqual(new int?[] { 400 }, observedMaxOutputTokens);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_AllowsLargeNecessaryCheckpointWhenItFits()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-ratio-reported");
        var state = CreateState("session-ratio-reported");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text(
                                """
                                ## Objective
                                Continue the task.
                                ## Active User Request
                                Second prompt
                                ## Constraints
                                - Keep recent context verbatim.
                                ## Progress
                                ### Done
                                - Earlier work summarized.
                                ### In Progress
                                - Continue the task.
                                ### Blocked
                                - None recorded.
                                ## Decisions
                                - Keep the recent suffix even if it remains relatively expensive.
                                ## Next Steps
                                - Continue from the retained context.
                                ## Critical Context
                                - The retained suffix dominates the post-compaction footprint.
                                ## Relevant Files
                                - None tracked.
                                """)]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 1200))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 1200))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 1200)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 1200)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        StringAssert.Contains(outcome.Message, "Manual local compaction summarized");
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_UsesProviderSummaryOutputTokenLimit()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            PostCompactionTargetRatio = 0.50,
        });
        var summary = CreateSummary("session-summary-output-clamped");
        var state = CreateState("session-summary-output-clamped");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var observedMaxOutputTokens = new List<int?>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
                            ["contextWindow"] = 4096L,
                            ["inputTokenLimit"] = 2048L,
                            ["outputTokenLimit"] = 64L,
                        }),
                ],
                (request, _, _) =>
                {
                    observedMaxOutputTokens.Add(request.MaxOutputTokens);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
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
                                    - Clamp summary output to the provider limit.
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        CollectionAssert.AreEqual(new int?[] { 64 }, observedMaxOutputTokens);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_ChunksOversizedSummaryInput()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            Enabled = false,
        });
        var summary = CreateSummary("session-chunked-compaction");
        var state = CreateState("session-chunked-compaction");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo(
                    "gpt-5.4",
                    "GPT-5.4",
                    Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["contextWindow"] = 4096L,
                        ["inputTokenLimit"] = 360L,
                        ["outputTokenLimit"] = 512L,
                    })],
                (request, _, _) =>
                {
                    var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
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
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 4_000))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 4_000))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Third answer " + new string('c', 320))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 4_000)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 4_000)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Third prompt " + new string('z', 240)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(summaryPayloads.Count >= 2);
        Assert.IsTrue(summaryPayloads.Skip(1).Any(static payload => payload.Contains("<previous-summary>", StringComparison.Ordinal)));

        var history = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = history
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.ChunkCount > 1);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_TreatsSummaryInputBudgetAsOptimizationTarget()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-input-target");
        var state = CreateState("session-summary-input-target");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        var largePrompt = string.Join(' ', Enumerable.Range(1, 300).Select(static index => $"prompt-token-{index}"));
        var largeAnswer = string.Join(' ', Enumerable.Range(1, 300).Select(static index => $"answer-token-{index}"));
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
                            ["contextWindow"] = 20_000L,
                            ["inputTokenLimit"] = 18_000L,
                            ["outputTokenLimit"] = 8_192L,
                        }),
                ],
                (request, _, _) =>
                {
                    summaryPayloads.Add(Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(
                                    """
                                    ## Objective
                                    Continue the task.
                                    ## Active User Request
                                    Continue after compaction.
                                    ## Constraints
                                    - Do not treat local summary-input targets as hard blockers.
                                    ## Progress
                                    ### Done
                                    - Produced a summary from an over-target request.
                                    ### In Progress
                                    - Continue the user's work.
                                    ### Blocked
                                    - None recorded.
                                    ## Decisions
                                    - Summary-input tokens are optimization targets.
                                    ## Next Steps
                                    - Resume from the checkpoint.
                                    ## Critical Context
                                    - The compaction request intentionally exceeded the configured target.
                                    ## Relevant Files
                                    - None tracked.
                                    """)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + largeAnswer)]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer")]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + largePrompt) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt") }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryPayloads.Count);
        Assert.IsTrue(AgentTokenEstimator.EstimateTextTokens(summaryPayloads[0]) > 120);
    }

    [TestMethod]
    public async Task AgentSession_SendAsync_ReducesOversizedLatestUserAnchorBeforeTurnExecution()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            Ratio = 0.60,
        });
        var summary = CreateSummary("session-oversized-anchor");
        var state = CreateState("session-oversized-anchor");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        var actualTurnPayloads = new List<string>();
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
                    var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                    summaryPayloads.Add(payload);
                    var summaryText = request.SystemMessage?.Contains("oversized-anchor reducer", StringComparison.Ordinal) == true
                        ? """
                          ## Task
                          - Update the compaction system for large sessions.
                          ## Explicit Requirements
                          - Preserve recent knowledge.
                          - Keep the configurable compaction ratio.
                          - Cover very large prompts and oversized attachments.
                          ## Files and Identifiers
                          - doc/runtime.md
                          - tmp/agent_compaction_plan_v2.md
                          ## Exact Literals and Errors
                          - "ratio = 0.95"
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
                          - doc/runtime.md
                          - tmp/agent_compaction_plan_v2.md
                          """;
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text(summaryText)]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Initial answer.")]),
                        Usage = CreateUsageSnapshot(120, 40),
                    }),
                (request, _, _) =>
                {
                    actualTurnPayloads.Add(Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("Handled the oversized request.")]),
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
                    Enumerable.Range(1, 160).Select(index => $"{index}. Keep detail #{index} and mention doc/runtime.md"))),
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
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint!.OversizedAnchorReduced);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_SummarizerFailureLeavesStateUntouched()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-failure");
        var state = CreateState("session-summary-failure");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (_, _, _) => throw new InvalidOperationException("summary failed"),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
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
        Assert.IsTrue(persistedHistory.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionStarted));
        Assert.IsFalse(persistedHistory.OfType<AgentSessionUpdateEvent>().Any(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted));
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_ShrinksOversizedGeneratedSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            Ratio = 0.80,
            KeepLastUserMessage = true,
            AllowSplitTurn = true,
        });
        var summary = CreateSummary("session-summary-too-large");
        var state = CreateState("session-summary-too-large");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryAttempts = 0;
        var shrinkAttempts = 0;
        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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
                            ["contextWindow"] = 9000L,
                            ["inputTokenLimit"] = 8000L,
                            ["outputTokenLimit"] = 64L,
                        }),
                ],
                (request, _, _) =>
                {
                    if (request.SystemMessage?.Contains("CodeAlta compaction summary shrinker", StringComparison.Ordinal) == true)
                    {
                        shrinkAttempts++;
                        return Task.FromResult(
                            new AgentTurnResponse
                            {
                                AssistantMessage = new AgentConversationMessage(
                                    AgentConversationRole.Assistant,
                                    [new AgentMessagePart.Text(
                                        """
                                        ## Objective
                                        Continue the task.
                                        ## Active User Request
                                        Second prompt
                                        ## Constraints
                                        - Keep the summary tight.
                                        ## Progress
                                        ### Done
                                        - Earlier work summarized.
                                        ### In Progress
                                        - Continue.
                                        ### Blocked
                                        - None recorded.
                                        ## Decisions
                                        - Shrink verbose compaction summaries.
                                        ## Next Steps
                                        - Continue from compacted context.
                                        ## Critical Context
                                        - Oversized generated summary was rewritten.
                                        ## Relevant Files
                                        - None tracked.
                                        """)]),
                            });
                    }

                    summaryAttempts++;
                    return Task.FromResult(
                        new AgentTurnResponse
                        {
                            AssistantMessage = new AgentConversationMessage(
                                AgentConversationRole.Assistant,
                                [new AgentMessagePart.Text("## Objective\n" + new string('x', 4000))]),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 160)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 160)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryAttempts);
        Assert.AreEqual(1, shrinkAttempts);
        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionCheckpointEventId);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(AgentTokenEstimator.EstimateTextTokens(checkpoint!.Summary) <= 256);
        Assert.AreEqual(2, checkpoint.SummaryCallCount);
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_NormalizesEmptyGeneratedSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-empty");
        var state = CreateState("session-summary-empty");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("   ")]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        StringAssert.Contains(checkpoint!.Summary, "## Objective");
        StringAssert.Contains(checkpoint.Summary, "Second prompt");
    }

    [TestMethod]
    public async Task AgentSession_CompactAsync_NormalizesMalformedStructuredSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-summary-malformed");
        var state = CreateState("session-summary-malformed");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("## Objective\nThis summary is malformed because required sections are missing.")]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("First answer " + new string('a', 160))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Second answer " + new string('b', 160))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 180)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 180)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionCheckpointEventId);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        StringAssert.Contains(checkpoint!.Summary, "## Objective");
        StringAssert.Contains(checkpoint.Summary, "## Active User Request");
        StringAssert.Contains(checkpoint.Summary, "## Relevant Files");
        StringAssert.Contains(checkpoint.Summary, "required sections are missing.");
    }

    [TestMethod]
    public void AgentTokenEstimator_EstimatePromptTokens_UsesLastOperationPlusTrailingTail()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Answer")]),
            new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("tool output")]))]),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Follow-up")]),
        };

        var estimate = AgentTokenEstimator.EstimatePromptTokens(
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
            150 + AgentTokenEstimator.EstimateMessage(conversation[2]) + AgentTokenEstimator.EstimateMessage(conversation[3]),
            estimate.Tokens);
    }

    [TestMethod]
    public void AgentCompactionCheckpoint_OldJsonWithoutTargetFields_Deserializes()
    {
        var checkpoint = JsonSerializer.Deserialize(
            """
            {
              "version": 2,
              "contentId": "compaction:old",
              "trigger": "manual",
              "summary": "## Objective\nContinue.",
              "tokensBefore": 1000,
              "summarizedMessageCount": 4
            }
            """,
            AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);

        Assert.IsNotNull(checkpoint);
        Assert.AreEqual("compaction:old", checkpoint!.ContentId);
        Assert.IsNull(checkpoint.TargetRatio);
        Assert.IsNull(checkpoint.TargetTokens);
        Assert.IsNull(checkpoint.TargetMet);
        Assert.IsNull(checkpoint.TargetMissReason);
    }

    [TestMethod]
    public void AgentTokenEstimator_EstimatePromptTokens_ReusesEstimatedWindowWithoutMarkingItExact()
    {
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Prompt")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Answer")]),
        };

        var estimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage: "System",
            developerInstructions: null,
            conversation,
            new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(
                    CurrentTokens: 222,
                    TokenLimit: 1000,
                    MessageCount: conversation.Length,
                    Label: "Estimated active context"),
                Scope: AgentUsageScope.CurrentWindow,
                Source: AgentUsageSource.LocalProviderUsage,
                UpdatedAt: DateTimeOffset.UtcNow));

        Assert.AreEqual("window-snapshot", estimate.Source);
        Assert.IsTrue(estimate.IsEstimated);
        Assert.AreEqual(222L, estimate.Tokens);
    }

    [TestMethod]
    public void AgentTokenEstimator_EstimatePromptTokens_UsesWindowSnapshotAcrossCheckpointAndLocalTail()
    {
        var checkpoint = new AgentCompactionCheckpoint
        {
            Version = 1,
            ContentId = "compaction:1",
            Trigger = "manual",
            Summary = "## Objective\nCheckpoint",
            TokensBefore = 200,
            SummarizedMessageCount = 2,
        }.CreateMessage();
        var followUp = new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Follow-up")]);
        var toolOutput = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("tool output")]))]);
        var conversation = new[] { checkpoint, followUp, toolOutput };

        var estimate = AgentTokenEstimator.EstimatePromptTokens(
            systemMessage: "System",
            developerInstructions: null,
            conversation,
            new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(
                    CurrentTokens: 180,
                    TokenLimit: 1000,
                    MessageCount: 2,
                    Label: "Post-compaction window"),
                Scope: AgentUsageScope.Compaction,
                Source: AgentUsageSource.RecoveredHistory,
                UpdatedAt: DateTimeOffset.UtcNow));

        Assert.AreEqual("window-snapshot+local-tail", estimate.Source);
        Assert.IsTrue(estimate.IsEstimated);
        Assert.AreEqual(180L + AgentTokenEstimator.EstimateMessage(toolOutput), estimate.Tokens);
    }

    [TestMethod]
    public void AgentTokenEstimator_EstimatePromptTokens_DoesNotReuseLastOperationAcrossCheckpoint()
    {
        var conversation = new[]
        {
            new AgentCompactionCheckpoint
            {
                Version = 1,
                ContentId = "compaction:1",
                Trigger = "manual",
                Summary = "## Objective\nCheckpoint",
                TokensBefore = 200,
                SummarizedMessageCount = 2,
            }.CreateMessage(),
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Follow-up")]),
        };

        var estimate = AgentTokenEstimator.EstimatePromptTokens(
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
    public async Task AgentSession_SendAsync_OverflowCompactsAndRetriesOnce()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(AgentCompactionSettings.Default with
        {
            Ratio = 0.95,
            KeepLastUserMessage = true,
            AllowSplitTurn = true,
        });
        var summary = CreateSummary("session-overflow");
        var state = CreateState("session-overflow");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var attempt = 0;
        var executor = new ScriptedTurnExecutor(
            [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
            (request, _, _) =>
            {
                var payload = Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
                StringAssert.Contains(payload, "<conversation>");
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text(
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
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Initial answer " + new string('a', 80))]),
                }),
            (request, _, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new AgentTurnExecutionException(new AgentTurnFailure("maximum context length exceeded", IsContextOverflow: true));
                }

                Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                StringAssert.Contains(
                    Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                    "codealta-compaction-checkpoint");
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Recovered after overflow.")]),
                        Usage = CreateUsageSnapshot(40, 20),
                    });
            },
            (request, _, _) =>
            {
                attempt++;
                Assert.AreEqual(AgentConversationRole.User, request.Conversation[0].Role);
                StringAssert.Contains(
                    Assert.IsInstanceOfType<AgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value,
                    "codealta-compaction-checkpoint");
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("Recovered after overflow.")]),
                        Usage = CreateUsageSnapshot(40, 20),
                    });
            });

        await using var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
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

    private static string GetExpectedPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
    }

    private static string GetExpectedDefaultShellLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell)
            ? "/bin/sh"
            : shell.Trim();
    }

    private static AgentSessionCreateOptions CreateOptions(ModelProviderRuntimeDescriptor provider, string workingDirectory)
    {
        return new AgentSessionCreateOptions
        {
            ProviderKey = provider.ProviderKey,
            Model = "gpt-5.4",
            WorkingDirectory = workingDirectory,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
    }

    [TestMethod]
    public async Task AgentSession_DisposeAsyncDisposesProviderSessionState()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-dispose");
        var state = CreateState("session-dispose");
        var executor = new CleanupRecordingTurnExecutor();
        var session = new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            executor,
            CreateOptions(provider, temp.Path),
            allowProviderContinuation: true);

        await session.DisposeAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "session-dispose" }, executor.DisposedSessionIds);
    }

    private static int FindEventIndex(IReadOnlyList<AgentEvent> events, Func<AgentEvent, bool> predicate)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (predicate(events[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static ModelProviderRuntimeDescriptor CreateProvider(AgentCompactionSettings? compaction = null)
    {
        return new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
            Profile = new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                MaxTokensFieldName = "max_output_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            Compaction = compaction ?? AgentCompactionSettings.Default,
        };
    }

    private static AgentSessionSummary CreateSummary(string sessionId)
    {
        return new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\repo",
            CreatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
        };
    }

    private static AgentSessionState CreateState(string sessionId)
    {
        return new AgentSessionState
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

    private static AgentSessionUsage CreateWindowUsageSnapshot(long currentTokens, long tokenLimit, long inputTokens, long outputTokens)
    {
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                CurrentTokens: currentTokens,
                TokenLimit: tokenLimit,
                MessageCount: null,
                Label: "Active context window"),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.4-2026-03-05",
                InputTokens: inputTokens,
                OutputTokens: outputTokens),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.LocalProviderUsage,
            UpdatedAt: DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"));
    }

    private static string CreateSkillPayload(string skillName, string skillFilePath, string body)
    {
        var skillRootPath = Path.GetDirectoryName(skillFilePath)!;
        var baseDirectoryUri = new Uri(skillRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
        return
            $"""
            <skill_content name="{skillName}" source="project" source_kind="ProjectAlta" source_id="project-alta:{skillRootPath}" path="{skillFilePath}" root="{skillRootPath}" base_directory="{baseDirectoryUri}">
            # Skill: {skillName}

            {body}

            Base directory: {baseDirectoryUri}
            Relative paths in this skill resolve against this directory.

            <skill_files>
            </skill_files>
            </skill_content>
            """;
    }

    private sealed class CleanupRecordingTurnExecutor : IModelProviderTurnExecutor, IAgentProviderSessionCleanup
    {
        public List<string> DisposedSessionIds { get; } = [];

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("gpt-5.4", "GPT-5.4")]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Done.")]),
                });

        public ValueTask DisposeProviderSessionAsync(string sessionId)
        {
            DisposedSessionIds.Add(sessionId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SessionUpdateTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("gpt-5.4", "GPT-5.4")]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => CreateResponseAsync();

        public async Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
            CancellationToken cancellationToken = default)
        {
            await onSessionUpdate(
                    new AgentTurnSessionUpdate
                    {
                        Kind = AgentSessionUpdateKind.Reconnecting,
                        Message = "Reconnecting to ChatGPT/Codex... 1/5",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return await CreateResponseAsync().ConfigureAwait(false);
        }

        private static Task<AgentTurnResponse> CreateResponseAsync()
            => Task.FromResult(
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Recovered after reconnect.")]),
                });
    }

    private sealed class ScriptedTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>? _summaryHandler;
        private readonly Queue<Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>> _steps;

        public ScriptedTurnExecutor(params Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>[] steps)
            : this(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                summaryHandler: null,
                steps)
        {
        }

        public ScriptedTurnExecutor(
            IReadOnlyList<AgentModelInfo> models,
            Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>? summaryHandler = null,
            params Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>[] steps)
        {
            _models = models;
            _summaryHandler = summaryHandler;
            _steps = new Queue<Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>>(steps);
        }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_models);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            if (_summaryHandler is not null &&
                (request.SystemMessage?.Contains("CodeAlta compaction summarizer", StringComparison.Ordinal) == true ||
                 request.SystemMessage?.Contains("CodeAlta compaction summary shrinker", StringComparison.Ordinal) == true ||
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

    private sealed class AliasAwareTurnExecutor : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
            ModelProviderRuntimeDescriptor provider,
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

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            if (request.SystemMessage?.Contains("CodeAlta compaction summarizer", StringComparison.Ordinal) == true ||
                request.SystemMessage?.Contains("CodeAlta compaction summary shrinker", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(
                    new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(
                            AgentConversationRole.Assistant,
                            [new AgentMessagePart.Text("## Objective\nSummary")]),
                    });
            }

            var usage = AgentUsageFactory.CreateOperationUsage(
                modelId: "gpt-5.4-2026-03-05",
                modelInfo: request.ModelInfo,
                inputTokens: 1200,
                outputTokens: 50,
                totalTokens: 1250,
                cachedInputTokens: null,
                reasoningTokens: null,
                updatedAt: DateTimeOffset.UtcNow);

            return Task.FromResult(
                new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(
                        AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Done.")]),
                    Usage = usage,
                });
        }
    }
}
