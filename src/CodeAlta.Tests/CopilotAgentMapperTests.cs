using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotAgentMapperTests
{
    [TestMethod]
    public void ToSessionConfig_MapsCoreOptions()
    {
        var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement;
        var options = new AgentSessionCreateOptions
        {
            Model = "gpt-5",
            WorkingDirectory = @"C:\repo",
            Streaming = true,
            ReasoningEffort = AgentReasoningEffort.High,
            McpServers = new Dictionary<string, AgentMcpServerConfig>(StringComparer.Ordinal)
            {
                ["local-docs"] = new AgentLocalMcpServerConfig("dotnet")
                {
                    Arguments = ["run", "--server"],
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["DOTNET_ENVIRONMENT"] = "Development"
                    },
                    WorkingDirectory = @"C:\repo\mcp",
                    ToolTimeout = TimeSpan.FromSeconds(30)
                }
            },
            SystemMessage = "System guidance",
            DeveloperInstructions = "Developer guidance",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("echo_tool", "Echoes input", toolSchema),
                    static (_, _) => Task.FromResult<AgentToolResult>(
                        new(
                            true,
                            [new AgentToolResultItem.Text("ok")])))
            ]
        };

        var config = CopilotAgentMapper.ToSessionConfig(options);

        Assert.AreEqual("gpt-5", config.Model);
        Assert.AreEqual(@"C:\repo", config.WorkingDirectory);
        Assert.IsTrue(config.Streaming);
        Assert.AreEqual("high", config.ReasoningEffort);
        Assert.IsNotNull(config.SystemMessage);
        Assert.AreEqual(SystemMessageMode.Append, config.SystemMessage.Mode);
        Assert.IsTrue(config.SystemMessage.Content!.Contains("System guidance", StringComparison.Ordinal));
        Assert.IsTrue(config.SystemMessage.Content.Contains("Developer guidance", StringComparison.Ordinal));
        Assert.IsNotNull(config.OnPermissionRequest);
        Assert.IsNotNull(config.Tools);
        Assert.AreEqual(1, config.Tools.Count);
        Assert.IsNotNull(config.McpServers);
        var mcpConfig = (McpLocalServerConfig)config.McpServers["local-docs"];
        Assert.AreEqual("dotnet", mcpConfig.Command);
        CollectionAssert.AreEqual(new[] { "run", "--server" }, mcpConfig.Args);
        Assert.AreEqual(@"C:\repo\mcp", mcpConfig.Cwd);
        Assert.AreEqual("Development", mcpConfig.Env!["DOTNET_ENVIRONMENT"]);
        Assert.AreEqual(30000, mcpConfig.Timeout);
        CollectionAssert.AreEqual(new[] { "*" }, mcpConfig.Tools);
    }

    [TestMethod]
    public void ToResumeSessionConfig_MapsRemoteMcpServerConfiguration()
    {
        var options = new AgentSessionResumeOptions
        {
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            McpServers = new Dictionary<string, AgentMcpServerConfig>(StringComparer.Ordinal)
            {
                ["remote-docs"] = new AgentRemoteMcpServerConfig("https://example.com/mcp")
                {
                    Transport = AgentMcpRemoteTransport.Sse,
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["X-Test"] = "42"
                    },
                    EnabledTools = ["lookup", "search"],
                    ToolTimeout = TimeSpan.FromSeconds(5)
                }
            }
        };

        var config = CopilotAgentMapper.ToResumeSessionConfig(options);

        Assert.IsNotNull(config.McpServers);
        var mcpConfig = (McpRemoteServerConfig)config.McpServers["remote-docs"];
        Assert.AreEqual("https://example.com/mcp", mcpConfig.Url);
        Assert.AreEqual("sse", mcpConfig.Type);
        Assert.AreEqual("42", mcpConfig.Headers!["X-Test"]);
        CollectionAssert.AreEqual(new[] { "lookup", "search" }, mcpConfig.Tools);
        Assert.AreEqual(5000, mcpConfig.Timeout);
    }

#if CODEALTA_LOCAL_COPILOT_SDK
    [TestMethod]
    public async Task ToSessionConfig_PublishesPermissionRequestAndResolutionEvents()
    {
        var bridge = new CopilotSessionCallbackBridge();
        var publishedEvents = new List<AgentEvent>();
        bridge.AttachPublisher(publishedEvents.Add);
        var options = new AgentSessionCreateOptions
        {
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowForSession))
        };

        var config = CopilotAgentMapper.ToSessionConfig(options, bridge);
        var request = new PermissionRequest
        {
            Kind = "custom-tool",
            ToolCallId = "tool-123",
            ExtensionData = new Dictionary<string, object>
            {
                ["toolName"] = JsonDocument.Parse("\"echo_tool\"").RootElement.Clone()
            }
        };

        var result = await config.OnPermissionRequest!(request, new PermissionInvocation { SessionId = "session-1" });

        Assert.AreEqual(PermissionRequestResultKind.Approved, result.Kind);
        Assert.AreEqual(2, publishedEvents.Count);
        Assert.IsInstanceOfType<AgentGenericPermissionRequest>(publishedEvents[0]);
        var permissionRequest = (AgentGenericPermissionRequest)publishedEvents[0];
        Assert.AreEqual("session-1", permissionRequest.SessionId);
        Assert.AreEqual("tool-123", permissionRequest.InteractionId);
        Assert.AreEqual("custom-tool", permissionRequest.Kind);
        Assert.AreEqual("echo_tool", permissionRequest.Raw.GetProperty("toolName").GetString());

        Assert.IsInstanceOfType<AgentInteractionEvent>(publishedEvents[1]);
        var resolved = (AgentInteractionEvent)publishedEvents[1];
        Assert.AreEqual(AgentInteractionKind.PermissionResolved, resolved.Kind);
        Assert.AreEqual("tool-123", resolved.InteractionId);
        Assert.IsTrue(resolved.Details.HasValue);
        var permissionDetails = resolved.Details.Value;
        Assert.AreEqual("AllowForSession", permissionDetails.GetProperty("decisionKind").GetString());
    }
#endif

    [TestMethod]
    public async Task ToSessionConfig_PublishesUserInputRequestAndResolutionEvents()
    {
        var bridge = new CopilotSessionCallbackBridge();
        var publishedEvents = new List<AgentEvent>();
        bridge.AttachPublisher(publishedEvents.Add);
        var options = new AgentSessionCreateOptions
        {
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) =>
                Task.FromResult(new AgentUserInputResponse(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["answer"] = "choice-b"
                    }))
        };

        var config = CopilotAgentMapper.ToSessionConfig(options, bridge);
        var request = new UserInputRequest
        {
            Question = "Pick one",
            Choices = new List<string> { "choice-a", "choice-b" },
            AllowFreeform = false
        };

        var result = await config.OnUserInputRequest!(request, new UserInputInvocation { SessionId = "session-1" });

        Assert.AreEqual("choice-b", result.Answer);
        Assert.IsFalse(result.WasFreeform);
        Assert.AreEqual(2, publishedEvents.Count);
        Assert.IsInstanceOfType<AgentUserInputRequest>(publishedEvents[0]);
        var inputRequest = (AgentUserInputRequest)publishedEvents[0];
        Assert.AreEqual("session-1", inputRequest.SessionId);
        Assert.AreEqual("Pick one", inputRequest.Form.Prompts[0].Question);
        Assert.IsFalse(inputRequest.Form.Prompts[0].AllowFreeform);
        CollectionAssert.AreEqual(new[] { "choice-a", "choice-b" }, inputRequest.Form.Prompts[0].Options!.Select(static option => option.Label).ToArray());

        Assert.IsInstanceOfType<AgentInteractionEvent>(publishedEvents[1]);
        var resolved = (AgentInteractionEvent)publishedEvents[1];
        Assert.AreEqual(AgentInteractionKind.UserInputResolved, resolved.Kind);
        Assert.IsTrue(resolved.Details.HasValue);
        var userInputDetails = resolved.Details.Value;
        Assert.AreEqual(1, userInputDetails.GetProperty("answerCount").GetInt32());
        Assert.AreEqual("choice-b", userInputDetails.GetProperty("answers").GetProperty("answer").GetString());
    }

    [TestMethod]
    public void ToSessionConfig_SanitizesCopilotToolNames()
    {
        var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement;
        var options = new AgentSessionCreateOptions
        {
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("codealta.tasks.create", "Creates a task", toolSchema),
                    static (_, _) => Task.FromResult<AgentToolResult>(
                        new(
                            true,
                            [new AgentToolResultItem.Text("ok")]))),
                new AgentToolDefinition(
                    new AgentToolSpec("codealta_tasks_create", "Creates another task", toolSchema),
                    static (_, _) => Task.FromResult<AgentToolResult>(
                        new(
                            true,
                            [new AgentToolResultItem.Text("ok")]))),
            ]
        };

        var config = CopilotAgentMapper.ToSessionConfig(options);
        var tools = config.Tools!.ToArray();

        Assert.IsNotNull(config.Tools);
        Assert.AreEqual(2, tools.Length);
        Assert.AreEqual("codealta_tasks_create", tools[0].Name);
        Assert.AreEqual("codealta_tasks_create_2", tools[1].Name);
    }

    [TestMethod]
    public void ToAgentModelInfo_MapsReasoningEffortMetadata()
    {
        var model = new ModelInfo
        {
            Id = "gpt-5",
            Name = "GPT-5",
            SupportedReasoningEfforts = ["minimal", "xhigh", "unknown"],
            DefaultReasoningEffort = "medium"
        };

        var mapped = CopilotAgentMapper.ToAgentModelInfo(model);

        Assert.AreEqual("gpt-5", mapped.Id);
        Assert.AreEqual("GPT-5", mapped.DisplayName);
        Assert.IsNull(mapped.Description);
        Assert.AreEqual(AgentReasoningEffort.Medium, mapped.DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Minimal, AgentReasoningEffort.XHigh },
            mapped.SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public void ToAgentSessionMetadata_MapsTypedCopilotDetails()
    {
        var metadata = CopilotAgentMapper.ToAgentSessionMetadata(
            new SessionMetadata
            {
                SessionId = "session-1",
                StartTime = DateTime.SpecifyKind(DateTime.Parse("2026-03-20T10:00:00"), DateTimeKind.Utc),
                ModifiedTime = DateTime.SpecifyKind(DateTime.Parse("2026-03-20T10:05:00"), DateTimeKind.Utc),
                Summary = "Review parser",
                IsRemote = true,
                Context = new SessionContext
                {
                    Cwd = @"C:\code\CodeAlta",
                    GitRoot = @"C:\code\CodeAlta",
                    Repository = "xoofx/codealta",
                    Branch = "main",
                }
            });

        var details = Assert.IsInstanceOfType<CopilotSessionMetadataDetails>(metadata.Details);
        Assert.IsTrue(details.IsRemote);
    }

    [TestMethod]
    public void ToSendMessageOptions_MapsAttachmentsAndPromptFallbacks()
    {
        var options = new AgentSendOptions
        {
            Input = new AgentInput(
            [
                new AgentInputItem.Text("Review these files"),
                new AgentInputItem.File("Program.cs", "Program.cs", new AgentLineRange(2, 8)),
                new AgentInputItem.Directory("src", "source"),
                new AgentInputItem.Selection(
                    "App.cs",
                    "Selection",
                    "Console.WriteLine(\"hello\");",
                    new AgentSelectionRange(
                        new AgentPosition(1, 3),
                        new AgentPosition(4, 1))),
                new AgentInputItem.ImageUrl("https://example.com/image.png"),
                new AgentInputItem.LocalImage(@"C:\img\local.png"),
                new AgentInputItem.Skill("linter", "/skills/linter"),
                new AgentInputItem.Mention("workspace", "/mentions/workspace")
            ])
        };

        var mapped = CopilotAgentMapper.ToSendMessageOptions(options);

        Assert.AreEqual("enqueue", mapped.Mode);
        Assert.IsNotNull(mapped.Attachments);
        Assert.AreEqual(3, mapped.Attachments.Count);

        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemFile>(mapped.Attachments[0]);
        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemDirectory>(mapped.Attachments[1]);
        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemSelection>(mapped.Attachments[2]);

        var fileAttachment = (UserMessageDataAttachmentsItemFile)mapped.Attachments[0];
        Assert.AreEqual("Program.cs", fileAttachment.Path);
        Assert.IsNotNull(fileAttachment.LineRange);
        Assert.AreEqual(2d, fileAttachment.LineRange.Start);
        Assert.AreEqual(8d, fileAttachment.LineRange.End);

        Assert.IsTrue(mapped.Prompt.Contains("Review these files", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[image-url] https://example.com/image.png", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains(@"[local-image] C:\img\local.png", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[skill] name=linter path=/skills/linter", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[mention] name=workspace path=/mentions/workspace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToSteerMessageOptions_UsesImmediateMode()
    {
        var options = new AgentSteerOptions
        {
            Input = AgentInput.Text("continue")
        };

        var mapped = CopilotAgentMapper.ToSteerMessageOptions(options);

        Assert.AreEqual("immediate", mapped.Mode);
        Assert.AreEqual("continue", mapped.Prompt);
    }

    [TestMethod]
    public void ToAgentEvent_MapsKnownAndRawSessionEvents()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:00:00+00:00");
        var deltaEvent = new AssistantMessageDeltaEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageDeltaData
            {
                MessageId = "msg-1",
                DeltaContent = "delta"
            }
        };

        var mappedDelta = CopilotAgentMapper.ToAgentEvent("session-1", deltaEvent);
        Assert.IsInstanceOfType<AgentContentDeltaEvent>(mappedDelta);
        var normalizedDelta = (AgentContentDeltaEvent)mappedDelta;
        Assert.AreEqual(AgentContentKind.Assistant, normalizedDelta.Kind);
        Assert.AreEqual("delta", normalizedDelta.Delta);
        Assert.AreEqual("msg-1", normalizedDelta.RunId?.Value);

        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-2",
                Content = "final message"
            }
        };

        var mappedMessage = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mappedMessage);
        var normalizedMessage = (AgentContentCompletedEvent)mappedMessage;
        Assert.AreEqual(AgentContentKind.Assistant, normalizedMessage.Kind);
        Assert.AreEqual("final message", normalizedMessage.Content);
        Assert.AreEqual("msg-2", normalizedMessage.RunId?.Value);

        var unknownEvent = new PendingMessagesModifiedEvent
        {
            Timestamp = timestamp,
            Data = new PendingMessagesModifiedData()
        };

        var mappedUnknown = CopilotAgentMapper.ToAgentEvent("session-1", unknownEvent);
        Assert.IsInstanceOfType<AgentRawEvent>(mappedUnknown);
        var raw = (AgentRawEvent)mappedUnknown;
        Assert.AreEqual("pending_messages.modified", raw.BackendEventType);
    }

    [TestMethod]
    public void ToAgentEvent_MapsWorkspaceFileChangedToDiffUpdate()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-20T12:00:00+00:00");
        var workspaceChanged = new SessionWorkspaceFileChangedEvent
        {
            Timestamp = timestamp,
            Data = new SessionWorkspaceFileChangedData
            {
                Path = "src/NewFile.cs",
                Operation = SessionWorkspaceFileChangedDataOperation.Create,
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", workspaceChanged);

        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(mapped);
        var update = (AgentSessionUpdateEvent)mapped;
        Assert.AreEqual(AgentSessionUpdateKind.DiffUpdated, update.Kind);
        Assert.IsTrue(update.Details.HasValue);
        Assert.AreEqual("src/NewFile.cs", update.Details.Value.GetProperty("path").GetString());
        Assert.AreEqual("create", update.Details.Value.GetProperty("operation").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsCommentaryAssistantMessagesToReasoning()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-12T20:04:41+00:00");
        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-1",
                Content = "I’m doing one final status check before handing off.",
                Phase = "commentary"
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mapped);
        Assert.AreEqual(AgentContentKind.Reasoning, ((AgentContentCompletedEvent)mapped).Kind);
    }

    [TestMethod]
    public void ToAgentEvent_MapsAssistantMessagesWithoutPhaseAndWithoutToolRequestsToAssistant()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-14T13:02:45+00:00");
        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-3",
                Content = "Perfect! Now I have enough information.",
                Phase = null
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mapped);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)mapped).Kind);
    }

    [TestMethod]
    public void ToAgentEvent_MapsAssistantMessagesWithoutPhaseButWithToolRequestsToReasoning()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-14T13:02:45+00:00");
        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-3",
                Content = "Perfect! Now I have enough information.",
                Phase = null,
                ToolRequests =
                [
                    new AssistantMessageDataToolRequestsItem
                    {
                        ToolCallId = "tool-1",
                        Name = "view",
                    }
                ]
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mapped);
        Assert.AreEqual(AgentContentKind.Reasoning, ((AgentContentCompletedEvent)mapped).Kind);
    }

    [TestMethod]
    public void ToAgentEvent_MapsFinalAnswerAssistantMessagesToAssistant()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-12T20:05:00+00:00");
        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-2",
                Content = "Fixed issue #6.",
                Phase = "final_answer"
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mapped);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)mapped).Kind);
    }

    [TestMethod]
    public void ToHistoryEvents_EmitsEmbeddedReasoningWhenNoExplicitReasoningWasSeen()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-14T13:50:17+00:00");
        var mapped = CopilotAgentMapper.ToHistoryEvents(
            "session-1",
            [
                new UserMessageEvent
                {
                    Timestamp = timestamp.AddSeconds(-2),
                    Data = new UserMessageData
                    {
                        Content = "Tell me about Tomlyn.",
                        InteractionId = "interaction-1"
                    }
                },
                new AssistantTurnStartEvent
                {
                    Timestamp = timestamp.AddSeconds(-1),
                    Data = new AssistantTurnStartData
                    {
                        TurnId = "0",
                        InteractionId = "interaction-1"
                    }
                },
                new AssistantMessageEvent
                {
                    Timestamp = timestamp,
                    Data = new AssistantMessageData
                    {
                        MessageId = "msg-4",
                        InteractionId = "interaction-1",
                        Phase = "final_answer",
                        Content = "Final answer",
                        ReasoningText = "Considering project structure"
                    }
                }
            ]);

        Assert.AreEqual(4, mapped.Count);
        Assert.AreEqual(AgentContentKind.User, ((AgentContentCompletedEvent)mapped[0]).Kind);
        Assert.IsInstanceOfType<AgentActivityEvent>(mapped[1]);
        Assert.AreEqual(AgentContentKind.Reasoning, ((AgentContentCompletedEvent)mapped[2]).Kind);
        Assert.AreEqual("Considering project structure", ((AgentContentCompletedEvent)mapped[2]).Content);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)mapped[3]).Kind);
    }

    [TestMethod]
    public void ToHistoryEvents_SuppressesEmbeddedReasoningWhenExplicitReasoningWasSeen()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-14T13:50:17+00:00");
        var mapped = CopilotAgentMapper.ToHistoryEvents(
            "session-1",
            [
                new UserMessageEvent
                {
                    Timestamp = timestamp.AddSeconds(-3),
                    Data = new UserMessageData
                    {
                        Content = "Tell me about Tomlyn.",
                        InteractionId = "interaction-1"
                    }
                },
                new AssistantTurnStartEvent
                {
                    Timestamp = timestamp.AddSeconds(-2),
                    Data = new AssistantTurnStartData
                    {
                        TurnId = "0",
                        InteractionId = "interaction-1"
                    }
                },
                new AssistantReasoningEvent
                {
                    Timestamp = timestamp.AddSeconds(-1),
                    Data = new AssistantReasoningData
                    {
                        ReasoningId = "reasoning-1",
                        Content = "Explicit reasoning"
                    }
                },
                new AssistantMessageEvent
                {
                    Timestamp = timestamp,
                    Data = new AssistantMessageData
                    {
                        MessageId = "msg-4",
                        InteractionId = "interaction-1",
                        Phase = "final_answer",
                        Content = "Final answer",
                        ReasoningText = "Embedded reasoning"
                    }
                }
            ]);

        var reasoningContents = mapped
            .OfType<AgentContentCompletedEvent>()
            .Where(item => item.Kind == AgentContentKind.Reasoning)
            .Select(item => item.Content)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "Explicit reasoning" }, reasoningContents);
    }

    [TestMethod]
    public void ToAgentEvent_MapsUsageEventToSessionUpdate()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:00:00+00:00");
        using var quotaSnapshot = JsonDocument.Parse("""{"entitlementRequests":1500,"usedRequests":477,"usageAllowedWithExhaustion":true,"isUnlimitedEntitlement":false}""");
        var usageEvent = new AssistantUsageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantUsageData
            {
                Model = "gpt-5",
                InputTokens = 1200,
                OutputTokens = 300,
                CacheReadTokens = 75,
                CacheWriteTokens = 25,
                Duration = 420,
                Cost = 1.25,
                QuotaSnapshots = new Dictionary<string, object>
                {
                    ["sample"] = quotaSnapshot.RootElement.Clone()
                },
                CopilotUsage = new AssistantUsageDataCopilotUsage
                {
                    TotalNanoAiu = 99,
                    TokenDetails =
                    [
                        new AssistantUsageDataCopilotUsageTokenDetailsItem
                        {
                            BatchSize = 1,
                            CostPerBatch = 0,
                            TokenType = "input",
                            TokenCount = 1200
                        },
                        new AssistantUsageDataCopilotUsageTokenDetailsItem
                        {
                            BatchSize = 1,
                            CostPerBatch = 0,
                            TokenType = "output",
                            TokenCount = 300
                        }
                    ]
                }
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", usageEvent);

        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(mapped);
        var usage = (AgentSessionUpdateEvent)mapped;
        Assert.AreEqual(AgentSessionUpdateKind.UsageUpdated, usage.Kind);
        Assert.IsTrue(usage.Message!.Contains("gpt-5", StringComparison.Ordinal));
        Assert.IsNotNull(usage.Usage);
        Assert.AreEqual(AgentUsageScope.LastOperation, usage.Usage.Scope);
        Assert.AreEqual(AgentUsageSource.CopilotAssistantUsage, usage.Usage.Source);
        Assert.IsNotNull(usage.Usage.LastOperation);
        var details = Assert.IsInstanceOfType<CopilotSessionUsageDetails>(usage.Usage.Details);
        Assert.IsNotNull(details.LastAssistantUsage);
        Assert.AreEqual(1200L, details.LastAssistantUsage.InputTokens);
        Assert.AreEqual(300L, details.LastAssistantUsage.OutputTokens);
        Assert.AreEqual(99d, details.LastAssistantUsage.TotalNanoAiu);
        Assert.AreEqual(1, details.QuotaSnapshots!.Length);
        Assert.AreEqual("sample", details.QuotaSnapshots[0].Name);
        var quotaDetails = Assert.IsInstanceOfType<CopilotRequestQuotaDetails>(details.QuotaSnapshots[0].Details);
        Assert.AreEqual(1500L, quotaDetails.EntitlementRequests);
        Assert.AreEqual(477L, quotaDetails.UsedRequests);
        Assert.AreEqual(true, quotaDetails.UsageAllowedWithExhaustion);
    }

    [TestMethod]
    public void ToAgentEvent_MapsSessionUsageInfoIntoNormalizedUsage()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:05:00+00:00");
        var sessionUsage = new SessionUsageInfoEvent
        {
            Timestamp = timestamp,
            Data = new SessionUsageInfoData
            {
                CurrentTokens = 12345,
                TokenLimit = 200000,
                MessagesLength = 18
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", sessionUsage);

        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(mapped);
        var usage = (AgentSessionUpdateEvent)mapped;
        Assert.AreEqual(AgentSessionUpdateKind.UsageUpdated, usage.Kind);
        Assert.IsNotNull(usage.Usage);
        Assert.AreEqual(AgentUsageScope.CurrentWindow, usage.Usage.Scope);
        Assert.AreEqual(AgentUsageSource.CopilotSessionUsageInfo, usage.Usage.Source);
        Assert.IsNotNull(usage.Usage.Window);
        Assert.AreEqual("Active context window", usage.Usage.Window.Label);
        Assert.AreEqual(12345L, usage.Usage.CurrentTokens);
        Assert.AreEqual(200000L, usage.Usage.TokenLimit);
        Assert.AreEqual(18, usage.Usage.MessageCount);
    }

    [TestMethod]
    public void CreateCopilotQuotaUsage_MapsAccountQuotaSnapshots()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-19T07:00:00+00:00");
        var usage = CopilotAgentMapper.CreateCopilotQuotaUsage(
            timestamp,
            new AccountGetQuotaResult
            {
                QuotaSnapshots = new Dictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>
                {
                    ["premium_interactions"] = new()
                    {
                        EntitlementRequests = 1500,
                        UsedRequests = 477,
                        RemainingPercentage = 68.2,
                        Overage = 0,
                        OverageAllowedWithExhaustedQuota = true,
                        ResetDate = "2026-03-20T00:00:00Z"
                    },
                    ["chat"] = new()
                    {
                        EntitlementRequests = -1,
                        UsedRequests = 0,
                        RemainingPercentage = 100,
                        Overage = 0,
                        OverageAllowedWithExhaustedQuota = true,
                        ResetDate = "2026-03-20T00:00:00Z"
                    }
                }
            });

        Assert.IsNotNull(usage);
        Assert.AreEqual(AgentUsageScope.RateLimitOnly, usage.Scope);
        Assert.AreEqual(AgentUsageSource.CopilotAccountQuota, usage.Source);
        var details = Assert.IsInstanceOfType<CopilotSessionUsageDetails>(usage.Details);
        Assert.AreEqual(2, details.QuotaSnapshots!.Length);
        Assert.AreEqual("chat", details.QuotaSnapshots[0].Name);
        var unlimited = Assert.IsInstanceOfType<CopilotRequestQuotaDetails>(details.QuotaSnapshots[0].Details);
        Assert.AreEqual(true, unlimited.IsUnlimitedEntitlement);
        Assert.IsNull(unlimited.EntitlementRequests);
        Assert.AreEqual("premium_interactions", details.QuotaSnapshots[1].Name);
        var premium = Assert.IsInstanceOfType<CopilotRequestQuotaDetails>(details.QuotaSnapshots[1].Details);
        Assert.AreEqual(1500L, premium.EntitlementRequests);
        Assert.AreEqual(477L, premium.UsedRequests);
        Assert.AreEqual(true, premium.UsageAllowedWithExhaustion);
        Assert.AreEqual(DateTimeOffset.Parse("2026-03-20T00:00:00Z"), premium.ResetDate);
    }

    [TestMethod]
    public void ToAgentEvent_MapsCompactionAndTruncationIntoNormalizedWindowSnapshots()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:10:00+00:00");
        var compactionStarted = new SessionCompactionStartEvent
        {
            Timestamp = timestamp,
            Data = new SessionCompactionStartData
            {
                SystemTokens = 1000,
                ConversationTokens = 87500,
                ToolDefinitionsTokens = 1500
            }
        };
        var compactionEvent = new SessionCompactionCompleteEvent
        {
            Timestamp = timestamp,
            Data = new SessionCompactionCompleteData
            {
                Success = true,
                PreCompactionTokens = 90000,
                PostCompactionTokens = 45000,
                PreCompactionMessagesLength = 40,
                MessagesRemoved = 10,
                TokensRemoved = 45000,
                CompactionTokensUsed = new SessionCompactionCompleteDataCompactionTokensUsed
                {
                    Input = 1200,
                    Output = 300,
                    CachedInput = 800
                }
            }
        };

        var mappedCompactionStarted = (AgentSessionUpdateEvent)CopilotAgentMapper.ToAgentEvent("session-1", compactionStarted);
        var mappedCompaction = (AgentSessionUpdateEvent)CopilotAgentMapper.ToAgentEvent("session-1", compactionEvent);

        Assert.AreEqual(AgentSessionUpdateKind.CompactionStarted, mappedCompactionStarted.Kind);
        Assert.AreEqual("Compaction started at 90000 tokens.", mappedCompactionStarted.Message);
        Assert.AreEqual(
            "Compaction completed (90000 -> 45000 tokens, 10 messages removed).",
            mappedCompaction.Message);
        Assert.AreEqual(AgentUsageScope.Compaction, mappedCompaction.Usage!.Scope);
        Assert.AreEqual(AgentUsageSource.CopilotCompactionComplete, mappedCompaction.Usage.Source);
        Assert.AreEqual("Post-compaction window", mappedCompaction.Usage.Window!.Label);
        Assert.AreEqual(45000L, mappedCompaction.Usage.CurrentTokens);
        Assert.AreEqual(30, mappedCompaction.Usage.MessageCount);
        Assert.IsNotNull(mappedCompaction.Usage.LastOperation);
        Assert.AreEqual("Compaction call", mappedCompaction.Usage.LastOperation.Label);
        Assert.AreEqual(800L, mappedCompaction.Usage.LastOperation.CachedInputTokens);

        var truncationEvent = new SessionTruncationEvent
        {
            Timestamp = timestamp,
            Data = new SessionTruncationData
            {
                TokenLimit = 200000,
                PreTruncationTokensInMessages = 250000,
                PreTruncationMessagesLength = 60,
                PostTruncationTokensInMessages = 180000,
                PostTruncationMessagesLength = 42,
                TokensRemovedDuringTruncation = 70000,
                MessagesRemovedDuringTruncation = 18,
                PerformedBy = "BasicTruncator"
            }
        };

        var mappedTruncation = (AgentSessionUpdateEvent)CopilotAgentMapper.ToAgentEvent("session-1", truncationEvent);

        Assert.AreEqual(AgentUsageScope.Truncation, mappedTruncation.Usage!.Scope);
        Assert.AreEqual(AgentUsageSource.CopilotTruncation, mappedTruncation.Usage.Source);
        Assert.AreEqual("Post-truncation window", mappedTruncation.Usage.Window!.Label);
        Assert.AreEqual(180000L, mappedTruncation.Usage.CurrentTokens);
        Assert.AreEqual(200000L, mappedTruncation.Usage.TokenLimit);
        Assert.AreEqual(42, mappedTruncation.Usage.MessageCount);
    }

    [TestMethod]
    public void ToAgentEvent_MapsUserMessageEventToUserContent()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:00:00+00:00");
        var userMessageEvent = new UserMessageEvent
        {
            Timestamp = timestamp,
            Data = new UserMessageData
            {
                Content = "Please investigate the parser failure.",
                InteractionId = "interaction-1"
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", userMessageEvent);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(mapped);
        var content = (AgentContentCompletedEvent)mapped;
        Assert.AreEqual(AgentContentKind.User, content.Kind);
        Assert.AreEqual("Please investigate the parser failure.", content.Content);
        Assert.AreEqual("interaction-1", content.ContentId);
    }

    [TestMethod]
    public void ToAgentEvent_MapsReasoningPlanAndToolLifecycleEvents()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:00:00+00:00");

        var reasoningDeltaEvent = new AssistantReasoningDeltaEvent
        {
            Timestamp = timestamp,
            Data = new AssistantReasoningDeltaData
            {
                ReasoningId = "reasoning-1",
                DeltaContent = "think"
            }
        };

        var mappedReasoning = CopilotAgentMapper.ToAgentEvent("session-1", reasoningDeltaEvent);
        Assert.IsInstanceOfType<AgentContentDeltaEvent>(mappedReasoning);
        var reasoning = (AgentContentDeltaEvent)mappedReasoning;
        Assert.AreEqual(AgentContentKind.Reasoning, reasoning.Kind);
        Assert.AreEqual("reasoning-1", reasoning.ContentId);
        Assert.AreEqual("think", reasoning.Delta);

        var planChangedEvent = new SessionPlanChangedEvent
        {
            Timestamp = timestamp,
            Data = new SessionPlanChangedData
            {
                Operation = SessionPlanChangedDataOperation.Update
            }
        };

        var mappedPlan = CopilotAgentMapper.ToAgentEvent("session-1", planChangedEvent);
        Assert.IsInstanceOfType<AgentPlanSnapshotEvent>(mappedPlan);
        var plan = (AgentPlanSnapshotEvent)mappedPlan;
        Assert.AreEqual(AgentPlanChangeKind.Updated, plan.Snapshot.ChangeKind);
        Assert.IsNull(plan.Snapshot.Steps);

        var toolProgressEvent = new ToolExecutionProgressEvent
        {
            Timestamp = timestamp,
            Data = new ToolExecutionProgressData
            {
                ToolCallId = "tool-1",
                ProgressMessage = "running"
            }
        };

        var mappedToolProgress = CopilotAgentMapper.ToAgentEvent("session-1", toolProgressEvent);
        Assert.IsInstanceOfType<AgentActivityEvent>(mappedToolProgress);
        var toolProgress = (AgentActivityEvent)mappedToolProgress;
        Assert.AreEqual(AgentActivityKind.ToolCall, toolProgress.Kind);
        Assert.AreEqual(AgentActivityPhase.Progressed, toolProgress.Phase);
        Assert.AreEqual("tool-1", toolProgress.ActivityId);
        Assert.AreEqual("running", toolProgress.Message);
        Assert.IsTrue(toolProgress.Details.HasValue);
        Assert.AreEqual("tool-1", toolProgress.Details.Value.GetProperty("toolCallId").GetString());

        var subagentStartedEvent = new SubagentStartedEvent
        {
            Timestamp = timestamp,
            Data = new SubagentStartedData
            {
                ToolCallId = "tool-2",
                AgentName = "worker",
                AgentDisplayName = "Worker",
                AgentDescription = "Executes the task"
            }
        };

        var mappedSubagent = CopilotAgentMapper.ToAgentEvent("session-1", subagentStartedEvent);
        Assert.IsInstanceOfType<AgentActivityEvent>(mappedSubagent);
        var subagent = (AgentActivityEvent)mappedSubagent;
        Assert.AreEqual(AgentActivityKind.Subagent, subagent.Kind);
        Assert.AreEqual(AgentActivityPhase.Started, subagent.Phase);
        Assert.AreEqual("tool-2", subagent.ActivityId);
        Assert.AreEqual("Worker", subagent.Name);
        Assert.IsTrue(subagent.Details.HasValue);
        Assert.AreEqual("Executes the task", subagent.Details.Value.GetProperty("agentDescription").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsPowershellToolExecutionStartToCommandExecution()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        using var argumentsDocument = JsonDocument.Parse(
            """
            {
              "command": "git --no-pager status --short",
              "description": "Check repo status",
              "initial_wait": 30
            }
            """);

        var toolStartEvent = new ToolExecutionStartEvent
        {
            Timestamp = timestamp,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "tool-1",
                ToolName = "powershell",
                Arguments = argumentsDocument.RootElement.Clone()
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", toolStartEvent);

        Assert.IsInstanceOfType<AgentActivityEvent>(mapped);
        var activity = (AgentActivityEvent)mapped;
        Assert.AreEqual(AgentActivityKind.CommandExecution, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Started, activity.Phase);
        Assert.AreEqual("git --no-pager status --short", activity.Name);
        Assert.IsTrue(activity.Details.HasValue);
        Assert.AreEqual("powershell", activity.Details.Value.GetProperty("toolName").GetString());
        Assert.AreEqual("git --no-pager status --short", activity.Details.Value.GetProperty("command").GetString());
        Assert.AreEqual("Check repo status", activity.Details.Value.GetProperty("description").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsSqlToolExecutionCompleteUsingTypedDetails()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var toolCompleteEvent = new ToolExecutionCompleteEvent
        {
            Timestamp = timestamp,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "tool-2",
                Success = true,
                Model = "gpt-5.4",
                InteractionId = "interaction-1",
                Result = new ToolExecutionCompleteDataResult
                {
                    Content = "3 row(s) returned.",
                    DetailedContent = "SQL: SELECT id, status FROM todos;"
                },
                ToolTelemetry = new Dictionary<string, object>
                {
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["queryType"] = "SELECT"
                    },
                    ["restrictedProperties"] = new Dictionary<string, object>
                    {
                        ["query"] = "SELECT id, status FROM todos;"
                    },
                    ["metrics"] = new Dictionary<string, object>
                    {
                        ["rowsReturned"] = 3
                    }
                }
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", toolCompleteEvent);

        Assert.IsInstanceOfType<AgentActivityEvent>(mapped);
        var activity = (AgentActivityEvent)mapped;
        Assert.AreEqual(AgentActivityKind.ToolCall, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Completed, activity.Phase);
        Assert.AreEqual("sql", activity.Name);
        Assert.AreEqual("3 row(s) returned.", activity.Message);
        Assert.IsTrue(activity.Details.HasValue);
        Assert.AreEqual("sql", activity.Details.Value.GetProperty("toolName").GetString());
        Assert.AreEqual("SELECT id, status FROM todos;", activity.Details.Value.GetProperty("query").GetString());
        Assert.AreEqual("SELECT", activity.Details.Value.GetProperty("queryType").GetString());
        Assert.AreEqual("3 row(s) returned.", activity.Details.Value.GetProperty("result").GetProperty("content").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsNonZeroTerminalExitAsFailedCommandExecution()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var toolCompleteEvent = new ToolExecutionCompleteEvent
        {
            Timestamp = timestamp,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "tool-3",
                Success = true,
                Result = new ToolExecutionCompleteDataResult
                {
                    Content = "fatal: not a git repository\n<exited with exit code 128>",
                    DetailedContent = "fatal: not a git repository\n<exited with exit code 128>"
                }
            }
        };

        var mapped = CopilotAgentMapper.ToAgentEvent("session-1", toolCompleteEvent);

        Assert.IsInstanceOfType<AgentActivityEvent>(mapped);
        var activity = (AgentActivityEvent)mapped;
        Assert.AreEqual(AgentActivityKind.CommandExecution, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Failed, activity.Phase);
        Assert.AreEqual("fatal: not a git repository\n<exited with exit code 128>", activity.Message);
        Assert.IsTrue(activity.Details.HasValue);
        Assert.AreEqual(true, activity.Details.Value.GetProperty("success").GetBoolean());
        Assert.AreEqual(128, activity.Details.Value.GetProperty("exitCode").GetInt32());
    }
}
