using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentSessionTests
{
    [TestMethod]
    public void LocalAgentInstructionComposer_ComposesDeveloperInstructionsAndLargestContextFilesPerDirectory()
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
        StringAssert.Contains(bundle.RuntimeContext, $"Current date: {DateTimeOffset.Now:yyyy-MM-dd}");
        StringAssert.Contains(bundle.RuntimeContext, "Platform: Windows");
        StringAssert.Contains(bundle.RuntimeContext, "Default shell for `shell_command`: `pwsh`");
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
    public void LocalAgentInstructionComposer_ComposesActiveSkillsSection()
    {
        var skillFilePath = @"C:\skills\code-review\SKILL.md";
        var payload = CreateSkillPayload("code-review", skillFilePath, "Review code for regressions.");

        var bundle = LocalAgentInstructionComposer.Compose(
            new AgentSessionCreateOptions
            {
                Model = "gpt-5.4",
                WorkingDirectory = @"C:\repo",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            },
            [
                new LocalAgentLoadedSkillState
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
    public async Task LocalAgentSession_SendAsync_RunsToolLoopAndPersistsReplayableEvents()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-1");
        var state = CreateState("session-1");
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
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, $"Current date: {DateTimeOffset.Now:yyyy-MM-dd}");
                    StringAssert.Contains(request.DeveloperInstructions, "Platform: Windows");
                    StringAssert.Contains(request.DeveloperInstructions, "Default shell for `shell_command`: `pwsh`");
                    StringAssert.Contains(request.DeveloperInstructions, $"Current working directory: `{Path.GetFullPath(temp.Path)}`");
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
        var systemPromptIndex = FindEventIndex(history, static evt => evt is AgentSystemPromptEvent);
        var firstUserIndex = FindEventIndex(history, static evt => evt is AgentRawEvent { BackendEventType: "local.userMessage" });
        Assert.IsTrue(systemPromptIndex >= 0 && firstUserIndex >= 0 && systemPromptIndex < firstUserIndex);
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
        Assert.IsTrue(persistedHistory.OfType<AgentSystemPromptEvent>().Any());
        Assert.IsTrue(persistedHistory.OfType<AgentContentCompletedEvent>().Any(static evt => evt.Kind == AgentContentKind.Assistant));

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_124", persistedState.ProviderSessionId);
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_PersistsSkillActivationAndRestoresLoadedSkills()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
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
        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
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
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [
                                    new LocalAgentMessagePart.Text("Load the review skill."),
                                    new LocalAgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(7, 4),
                            ProviderSessionId = "resp_skill_1",
                        });
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(LocalAgentConversationRole.Tool, request.Conversation[^1].Role);
                    var toolResult = Assert.IsInstanceOfType<LocalAgentMessagePart.ToolResult>(request.Conversation[^1].Parts.Single());
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value,
                        "<skill_content");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Skill loaded.")]),
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
                        new AgentToolSpec("codealta.skills.activate", "Activate a skill", schema.RootElement.Clone()),
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
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, "<active_skills>");
                    StringAssert.Contains(request.DeveloperInstructions, "code-review");
                    StringAssert.Contains(request.DeveloperInstructions, "Review code for regressions.");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Continuing with restored skill context.")]),
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
    }

    [TestMethod]
    public async Task LocalAgentSession_SendAsync_UserSkillInputPersistsUserActivation()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
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

        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, "<active_skills>");
                    StringAssert.Contains(request.DeveloperInstructions, "code-review");
                    StringAssert.Contains(request.DeveloperInstructions, "Review code for regressions.");
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                    Assert.IsTrue(request.Conversation[0].Parts.OfType<LocalAgentMessagePart.Text>().Any(part => part.Value.Contains("<skill_content", StringComparison.Ordinal)));
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Skill is active.")]),
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
    public async Task LocalAgentSession_ResumeSession_SurfacesMissingLoadedSkills()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
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
        await using (var initialSession = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
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
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [
                                    new LocalAgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(4, 2),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Loaded.")]),
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
                        new AgentToolSpec("codealta.skills.activate", "Activate a skill", schema.RootElement.Clone()),
                        (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(payload)]))),
                ],
            }))
        {
            _ = await initialSession.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Load skill.") }).ConfigureAwait(false);
        }

        File.Delete(skillFilePath);

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        await using var resumedSession = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            persistedState!,
            persistedHistory,
            store,
            new ScriptedTurnExecutor(
                (request, _, _) =>
                {
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, "<skill_missing");
                    StringAssert.Contains(request.DeveloperInstructions, "no longer available on disk");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Missing skill surfaced.")]),
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
    public async Task LocalAgentSession_CompactAsync_PreservesLoadedSkillsForReplay()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
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
                    var summaryText = Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value;
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
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text(responseText)]),
                        });
                },
                (_, _, _) =>
                {
                    using var arguments = JsonDocument.Parse("""{"skillName":"code-review"}""");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [
                                    new LocalAgentMessagePart.Text("Activate the skill."),
                                    new LocalAgentMessagePart.ToolCall("call-skill", "codealta_skills_activate", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(9, 4),
                        });
                },
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Skill is active.")]),
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
                        new AgentToolSpec("codealta.skills.activate", "Activate a skill", schema.RootElement.Clone()),
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
                    Assert.IsNotNull(request.DeveloperInstructions);
                    StringAssert.Contains(request.DeveloperInstructions, "<active_skills>");
                    StringAssert.Contains(request.DeveloperInstructions, "code-review");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Compacted session restored skill state.")]),
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
    public async Task LocalAgentSession_SendAsync_ConvertsToolExceptionsIntoFailedToolResults()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-tool-exception");
        var state = CreateState("session-tool-exception");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
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
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [
                                    new LocalAgentMessagePart.Text("Trying the network tool."),
                                    new LocalAgentMessagePart.ToolCall("call-1", "explode", arguments.RootElement.Clone()),
                                ]),
                            Usage = CreateUsageSnapshot(5, 3),
                            ProviderSessionId = "resp_tool_1",
                        });
                },
                (request, _, _) =>
                {
                    var toolMessage = request.Conversation[^1];
                    Assert.AreEqual(LocalAgentConversationRole.Tool, toolMessage.Role);
                    var toolResult = Assert.IsInstanceOfType<LocalAgentMessagePart.ToolResult>(toolMessage.Parts.Single());
                    Assert.IsFalse(toolResult.Result.Success);
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<AgentToolResultItem.Text>(toolResult.Result.Items.Single()).Value,
                        "Tool 'explode' failed: upstream failure");
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Recovered after tool failure.")]),
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
    public async Task LocalAgentSession_SendAsync_RefreshesEstimatedWindowUsageAfterToolOutputs()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-usage-refresh");
        var state = CreateState("session-usage-refresh");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        using var toolArguments = JsonDocument.Parse("""{"size":1}""");
        var initialUsage = CreateWindowUsageSnapshot(currentTokens: 780, tokenLimit: 1000, inputTokens: 760, outputTokens: 20);
        await using var session = new LocalAgentSession(
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
                            [
                                new LocalAgentMessagePart.Text("Need tool output."),
                                new LocalAgentMessagePart.ToolCall(
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
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Done.")]),
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

        _ = await session.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Trigger tool output"),
                })
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalAgentSession_SteerAsync_QueuesPendingInputIntoSameRun()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-steer");
        var state = CreateState("session-steer");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var firstTurnStarted = new TaskCompletionSource<AgentRunId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new LocalAgentSession(
            AgentBackendIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new ScriptedTurnExecutor(
                async (request, _, cancellationToken) =>
                {
                    Assert.AreEqual(1, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                    firstTurnStarted.TrySetResult(request.RunId);
                    await releaseFirstTurn.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer.")]),
                    };
                },
                (request, _, _) =>
                {
                    Assert.AreEqual(3, request.Conversation.Count);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[0].Role);
                    Assert.AreEqual(LocalAgentConversationRole.Assistant, request.Conversation[1].Role);
                    Assert.AreEqual(LocalAgentConversationRole.User, request.Conversation[2].Role);
                    Assert.AreEqual(
                        "Please add one more detail.",
                        Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[2].Parts.Single()).Value);
                    return Task.FromResult(
                        new LocalAgentTurnResponse
                        {
                            AssistantMessage = new LocalAgentConversationMessage(
                                LocalAgentConversationRole.Assistant,
                                [new LocalAgentMessagePart.Text("Second answer.")]),
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
    public async Task LocalAgentSession_ReplaysPersistedConversationOnResume()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-replay");
        var state = CreateState("session-replay");
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
        Assert.AreEqual(checkpoint.KeptMessages.Count, checkpoint.KeptMessageCount);
        Assert.IsTrue(checkpoint.SummaryCallCount >= 1);
        Assert.IsTrue(checkpoint.SummaryPromptInputTokens > 0);

        var completedUpdate = persistedHistory
            .OfType<AgentSessionUpdateEvent>()
            .Single(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted);
        Assert.IsNotNull(completedUpdate.Details);
        Assert.AreEqual("codealta.localCompaction.v1", completedUpdate.Details.Value.GetProperty("schema").GetString());
        Assert.AreEqual(checkpoint.Summary, completedUpdate.Details.Value.GetProperty("summaryMarkdown").GetString());
        Assert.AreEqual(checkpoint.SummaryCallCount, completedUpdate.Details.Value.GetProperty("summaryCallCount").GetInt32());

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
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_EnforcesGlobalToolOutputCapAcrossManyOutputs()
    {
        var messagesToSummarize = new List<LocalAgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 4))
        {
            messagesToSummarize.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.ToolCall($"call-{index}", "shell_command", JsonSerializer.SerializeToElement(new { command = $"step {index}" }))]));
            messagesToSummarize.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Tool,
                    [
                        new LocalAgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(
                                Success: index == 4,
                                [new AgentToolResultItem.Text($"result {index}: " + new string((char)('a' + index), 240))],
                                Error: index == 4 ? null : $"error {index}")),
                    ]));
        }

        var preparation = new LocalAgentCompactionPreparation(
            Trigger: LocalAgentCompactionTrigger.Threshold,
            MessagesToSummarize: messagesToSummarize,
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: "user:latest",
            IsSplitTurn: false,
            TokensBefore: new LocalAgentTokenEstimate(3000, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            LocalAgentCompactionSettings.Default with
            {
                ToolResultCharsPerItem = 120,
                ToolResultCharsTotal = 180,
            });

        Assert.IsTrue(result.Statistics.SerializedToolResultCharacters <= 180);
        Assert.IsTrue(result.Statistics.OmittedToolResultCount >= 2);
        StringAssert.Contains(result.UserMessage, "callId=call-4");
    }

    [TestMethod]
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_OmitsReasoningWhenBudgetIsExhausted()
    {
        var preparation = new LocalAgentCompactionPreparation(
            Trigger: LocalAgentCompactionTrigger.Manual,
            MessagesToSummarize:
            [
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [
                        new LocalAgentMessagePart.Reasoning("First long reasoning block " + new string('x', 300)),
                    ]),
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [
                        new LocalAgentMessagePart.Reasoning("Second long reasoning block " + new string('y', 300)),
                    ]),
            ],
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: null,
            IsSplitTurn: false,
            TokensBefore: new LocalAgentTokenEstimate(800, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            LocalAgentCompactionSettings.Default with
            {
                ReasoningCharsPerItem = 80,
                ReasoningCharsTotal = 80,
            });

        Assert.IsTrue(result.Statistics.SerializedReasoningCharacters <= 80);
        Assert.IsTrue(result.Statistics.OmittedReasoningCount >= 1);
        StringAssert.Contains(result.UserMessage, "[Assistant reasoning summary]");
    }

    [TestMethod]
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_RendersModifiedFilesBeforeReadFiles()
    {
        var preparation = new LocalAgentCompactionPreparation(
            Trigger: LocalAgentCompactionTrigger.Manual,
            MessagesToSummarize:
            [
                new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Continue")]),
            ],
            TurnPrefixMessages: [],
            MessagesToKeep: [],
            AnchorContentId: null,
            IsSplitTurn: false,
            TokensBefore: new LocalAgentTokenEstimate(100, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Continue",
            readFiles: ["C:\\repo\\older-read.cs", "C:\\repo\\recent-read.cs"],
            modifiedFiles: ["C:\\repo\\recent-edit.cs"],
            settings: LocalAgentCompactionSettings.Default);

        var modifiedIndex = result.UserMessage.IndexOf("### Modified", StringComparison.Ordinal);
        var readIndex = result.UserMessage.IndexOf("### Read", StringComparison.Ordinal);
        Assert.IsTrue(modifiedIndex >= 0);
        Assert.IsTrue(readIndex > modifiedIndex);
    }

    [TestMethod]
    public void LocalAgentCompactionCanonicalizer_Normalize_CollapsesRepeatedReadFileOperations()
    {
        var messages = new List<LocalAgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 3))
        {
            messages.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.ToolCall($"call-{index}", "read_file", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent/LocalRuntime/Compaction/LocalAgentCompactionSerializer.cs" }))]));
            messages.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Tool,
                    [
                        new LocalAgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(true, [new AgentToolResultItem.Text($"file contents chunk {index} " + new string('x', 120))])),
                    ]));
        }

        var units = LocalAgentCompactionCanonicalizer.Normalize(messages);
        Assert.AreEqual(1, units.Count);
        var collapsed = Assert.IsInstanceOfType<LocalAgentCompactionToolInteractionUnit>(units[0]);
        Assert.IsTrue(collapsed.IsCollapsed);
        Assert.AreEqual(3, collapsed.RepeatCount);
        Assert.AreEqual(1, collapsed.ToolCalls.Count);
        Assert.AreEqual(1, collapsed.ToolResults.Count);
    }

    [TestMethod]
    public void LocalAgentCompactionChunker_CreateChunks_KeepsToolInteractionAdjacent()
    {
        var introMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text("Intro " + new string('x', 180))]);
        var assistantToolCall = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Assistant,
            [new LocalAgentMessagePart.ToolCall("call-1", "grep", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent", pattern = "compaction" }))]);
        var toolResult = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Tool,
            [new LocalAgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("match line")]))]);

        var maxTokens = LocalAgentTokenEstimator.EstimateMessage(introMessage) + LocalAgentTokenEstimator.EstimateMessage(assistantToolCall) + 4;
        var chunks = LocalAgentCompactionChunker.CreateChunks(
            [introMessage, assistantToolCall, toolResult],
            (int)maxTokens,
            static chunk => chunk.Sum(LocalAgentTokenEstimator.EstimateMessage));

        Assert.AreEqual(2, chunks.Count);
        CollectionAssert.AreEqual(new[] { introMessage }, chunks[0].ToArray());
        CollectionAssert.AreEqual(new[] { assistantToolCall, toolResult }, chunks[1].ToArray());
    }

    [TestMethod]
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_CollapsesRepeatedLowValueToolActivity()
    {
        var messagesToSummarize = new List<LocalAgentConversationMessage>();
        foreach (var index in Enumerable.Range(1, 3))
        {
            messagesToSummarize.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.ToolCall($"call-{index}", "grep", JsonSerializer.SerializeToElement(new { path = "src/CodeAlta.Agent", pattern = "compaction" }))]));
            messagesToSummarize.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Tool,
                    [
                        new LocalAgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(true, [new AgentToolResultItem.Text($"match {index}: src/CodeAlta.Agent/File{index}.cs")])),
                    ]));
        }

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            new LocalAgentCompactionPreparation(
                Trigger: LocalAgentCompactionTrigger.Threshold,
                MessagesToSummarize: messagesToSummarize,
                TurnPrefixMessages: [],
                MessagesToKeep: [],
                AnchorContentId: null,
                IsSplitTurn: false,
                TokensBefore: new LocalAgentTokenEstimate(2000, "test", IsEstimated: true),
                PreviousSummary: null),
            latestUserRequest: "Continue",
            readFiles: [],
            modifiedFiles: [],
            settings: LocalAgentCompactionSettings.Default);

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
    public void LocalAgentCompactionSerializer_BuildSummaryRequestBody_KeepsAllPlaintextMessagesWhenInputFits()
    {
        var summarizedMessages = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("First prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("First answer")]),
        };
        var keptMessages = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Second prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Second answer")]),
        };

        var preparation = new LocalAgentCompactionPreparation(
            Trigger: LocalAgentCompactionTrigger.Threshold,
            MessagesToSummarize: summarizedMessages,
            TurnPrefixMessages: [],
            MessagesToKeep: keptMessages,
            AnchorContentId: "user:2",
            IsSplitTurn: false,
            TokensBefore: new LocalAgentTokenEstimate(200, "test", IsEstimated: true),
            PreviousSummary: null);

        var result = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest: "Second prompt",
            readFiles: [],
            modifiedFiles: [],
            settings: LocalAgentCompactionSettings.Default);

        Assert.AreEqual(result.TotalMessageCount, result.IncludedMessageCount);
        StringAssert.Contains(result.UserMessage, "First prompt");
        StringAssert.Contains(result.UserMessage, "First answer");
        StringAssert.Contains(result.UserMessage, "Second prompt");
        StringAssert.Contains(result.UserMessage, "Second answer");
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
    public void LocalAgentCompactionPlanner_Preparation_WiderPlanKeepsMoreContextThanAnchorOnlyFallback()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Earlier prompt " + new string('a', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Earlier answer " + new string('b', 320))]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Latest prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Recent answer " + new string('x', 320))]),
        };

        var budget = new LocalAgentTokenBudget(
            ContextWindow: 4096,
            InputTokenLimit: 4096,
            OutputTokenLimit: 256,
            UsablePromptBudget: 4096,
            ReservedOutputTokens: 64,
            ReservedOverheadTokens: 64);
        var settings = LocalAgentCompactionSettings.Default with
        {
            RecentSuffixTargetTokens = 96,
        };

        var widenedPreparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            budget,
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 180,
            keepAnchorOnly: false);
        var anchorOnlyPreparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            budget,
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 64,
            promptBudgetOverride: 180,
            keepAnchorOnly: true);

        Assert.IsNotNull(widenedPreparation);
        Assert.IsNotNull(anchorOnlyPreparation);
        Assert.IsTrue(widenedPreparation!.MessagesToKeep.Count > anchorOnlyPreparation!.MessagesToKeep.Count);
        Assert.AreEqual(1, anchorOnlyPreparation.TurnPrefixMessages.Count);
        Assert.AreEqual(LocalAgentConversationRole.User, anchorOnlyPreparation.TurnPrefixMessages[0].Role);
    }

    [TestMethod]
    public void LocalAgentCompactionPlanner_LargeToolHeavyConversation_StaysWithinV2CompressionTargets()
    {
        var conversation = new List<LocalAgentConversationMessage>
        {
            new(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Initial request")]),
        };

        foreach (var index in Enumerable.Range(1, 24))
        {
            conversation.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.ToolCall($"call-{index}", "read_file", JsonSerializer.SerializeToElement(new { path = $"src/File{index}.cs" }))]));
            conversation.Add(
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Tool,
                    [
                        new LocalAgentMessagePart.ToolResult(
                            $"call-{index}",
                            new AgentToolResult(
                                true,
                                [new AgentToolResultItem.Text($"file dump {index} " + new string((char)('a' + (index % 20)), 2400))])),
                    ]));
        }

        conversation.Add(new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Latest request")]));

        var settings = LocalAgentCompactionSettings.Default with
        {
            RecentSuffixTargetTokens = 160,
        };
        var preparation = LocalAgentCompactionPlanner.Prepare(
            LocalAgentCompactionTrigger.Threshold,
            systemMessage: null,
            developerInstructions: null,
            conversation,
            usage: null,
            new LocalAgentTokenBudget(
                ContextWindow: 400_000,
                InputTokenLimit: 400_000,
                OutputTokenLimit: 4_096,
                UsablePromptBudget: 400_000,
                ReservedOutputTokens: settings.ReservedOutputTokens,
                ReservedOverheadTokens: settings.ReservedOverheadTokens),
            settings,
            anchorContentId: "user:latest",
            checkpointTokenEstimate: 128);

        Assert.IsNotNull(preparation);
        var checkpoint = new LocalAgentCompactionCheckpoint
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

        var compactedConversation = new List<LocalAgentConversationMessage> { checkpoint.CreateMessage() };
        compactedConversation.AddRange(preparation.TurnPrefixMessages);
        compactedConversation.AddRange(preparation.MessagesToKeep);

        var tokensAfter = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage: null,
            developerInstructions: null,
            compactedConversation,
            usage: null).Tokens;
        var ratio = (double)tokensAfter / preparation.TokensBefore.Tokens;

        Assert.IsTrue(ratio <= 0.06d, $"Expected a representative large tool-heavy session to compact to 6% or less, but got {ratio:P2}.");
        Assert.IsTrue(ratio <= settings.TargetContextRatioMax, $"Expected the post-compaction ratio {ratio:P2} to stay within the v2 max target {settings.TargetContextRatioMax:P2}.");
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
    public async Task LocalAgentSession_CompactAsync_DefaultSettings_DoNotPretrimWhenSummaryInputFits()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default);
        var summary = CreateSummary("session-default-settings-fit");
        var state = CreateState("session-default-settings-fit");
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
                    summaryPayloads.Add(Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
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
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 800))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 800))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Third answer " + new string('c', 800))]),
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
    public async Task LocalAgentSession_CompactAsync_ReportsCompressionRatioWhenAboveTargetMax()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            TargetContextRatioMax = 0.01,
            RecentSuffixTargetTokens = 16_000,
        });
        var summary = CreateSummary("session-ratio-reported");
        var state = CreateState("session-ratio-reported");
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
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text(
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
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + new string('a', 1200))]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer " + new string('b', 1200))]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + new string('x', 1200)) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt " + new string('y', 1200)) }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        StringAssert.Contains(outcome.Message, "Post-compaction ratio");
        StringAssert.Contains(outcome.Message, "exceeded target");
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_ClampsSummaryOutputTokensToProviderLimit()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            SummaryOutputTokens = 320,
            ReservedOutputTokens = 64,
            RecentSuffixTargetTokens = 160,
        });
        var summary = CreateSummary("session-summary-output-clamped");
        var state = CreateState("session-summary-output-clamped");
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
        CollectionAssert.AreEqual(new int?[] { 64 }, observedMaxOutputTokens);
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
    public async Task LocalAgentSession_CompactAsync_TreatsSummaryInputBudgetAsOptimizationTarget()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            SummaryInputTokens = 120,
            RecentSuffixTargetTokens = 180,
            MaxChunkPasses = 1,
        });
        var summary = CreateSummary("session-summary-input-target");
        var state = CreateState("session-summary-input-target");
        await store.UpsertSessionAsync(summary).ConfigureAwait(false);
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var summaryPayloads = new List<string>();
        var largePrompt = string.Join(' ', Enumerable.Range(1, 300).Select(static index => $"prompt-token-{index}"));
        var largeAnswer = string.Join(' ', Enumerable.Range(1, 300).Select(static index => $"answer-token-{index}"));
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
                            ["contextWindow"] = 20_000L,
                            ["inputTokenLimit"] = 18_000L,
                            ["outputTokenLimit"] = 8_192L,
                        }),
                ],
                (request, _, _) =>
                {
                    summaryPayloads.Add(Assert.IsInstanceOfType<LocalAgentMessagePart.Text>(request.Conversation[0].Parts.Single()).Value);
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
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("First answer " + largeAnswer)]),
                    }),
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("Second answer")]),
                    })),
            CreateOptions(provider, temp.Path));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("First prompt " + largePrompt) }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Second prompt") }).ConfigureAwait(false);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryPayloads.Count);
        Assert.IsTrue(LocalAgentTokenEstimator.EstimateTextTokens(summaryPayloads[0]) > provider.Compaction!.SummaryInputTokens);
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
    public async Task LocalAgentSession_CompactAsync_AllowsOversizedGeneratedSummary()
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
                            ["contextWindow"] = 9000L,
                            ["inputTokenLimit"] = 8000L,
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

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().ConfigureAwait(false);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(1, summaryAttempts);
        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionCheckpointEventId);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(LocalAgentTokenEstimator.EstimateTextTokens(checkpoint!.Summary) > 256);
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_NormalizesEmptyGeneratedSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            RecentSuffixTargetTokens = 160,
        });
        var summary = CreateSummary("session-summary-empty");
        var state = CreateState("session-summary-empty");
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
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("   ")]),
                    }),
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

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        StringAssert.Contains(checkpoint!.Summary, "## Objective");
        StringAssert.Contains(checkpoint.Summary, "Second prompt");
    }

    [TestMethod]
    public async Task LocalAgentSession_CompactAsync_NormalizesMalformedStructuredSummary()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider(LocalAgentCompactionSettings.Default with
        {
            RecentSuffixTargetTokens = 160,
        });
        var summary = CreateSummary("session-summary-malformed");
        var state = CreateState("session-summary-malformed");
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
                (_, _, _) => Task.FromResult(
                    new LocalAgentTurnResponse
                    {
                        AssistantMessage = new LocalAgentConversationMessage(
                            LocalAgentConversationRole.Assistant,
                            [new LocalAgentMessagePart.Text("## Objective\nThis summary is malformed because required sections are missing.")]),
                    }),
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

        var persistedState = await store.GetStateAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.IsNotNull(persistedState.CompactionCheckpointEventId);

        var persistedHistory = await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId).ConfigureAwait(false);
        var checkpointEvent = persistedHistory
            .OfType<AgentRawEvent>()
            .Last(static evt => evt.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        StringAssert.Contains(checkpoint!.Summary, "## Objective");
        StringAssert.Contains(checkpoint.Summary, "## Active User Request");
        StringAssert.Contains(checkpoint.Summary, "## Relevant Files");
        StringAssert.Contains(checkpoint.Summary, "required sections are missing.");
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
    public void LocalAgentTokenEstimator_EstimatePromptTokens_ReusesEstimatedWindowWithoutMarkingItExact()
    {
        var conversation = new[]
        {
            new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Prompt")]),
            new LocalAgentConversationMessage(LocalAgentConversationRole.Assistant, [new LocalAgentMessagePart.Text("Answer")]),
        };

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
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
    public void LocalAgentTokenEstimator_EstimatePromptTokens_UsesWindowSnapshotAcrossCheckpointAndLocalTail()
    {
        var checkpoint = new LocalAgentCompactionCheckpoint
        {
            Version = 1,
            ContentId = "compaction:1",
            Trigger = "manual",
            Summary = "## Objective\nCheckpoint",
            TokensBefore = 200,
            SummarizedMessageCount = 2,
        }.CreateMessage();
        var followUp = new LocalAgentConversationMessage(LocalAgentConversationRole.User, [new LocalAgentMessagePart.Text("Follow-up")]);
        var toolOutput = new LocalAgentConversationMessage(
            LocalAgentConversationRole.Tool,
            [new LocalAgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("tool output")]))]);
        var conversation = new[] { checkpoint, followUp, toolOutput };

        var estimate = LocalAgentTokenEstimator.EstimatePromptTokens(
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
        Assert.AreEqual(180L + LocalAgentTokenEstimator.EstimateMessage(toolOutput), estimate.Tokens);
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
