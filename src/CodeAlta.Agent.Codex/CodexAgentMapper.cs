using System.IO;
using System.Text;
using System.Text.Json;
using CodeAlta.CodexSdk;
using CodexThread = CodeAlta.CodexSdk.Thread;
using V2ReasoningEffort = CodeAlta.CodexSdk.ReasoningEffort;
using V2AskForApproval = CodeAlta.CodexSdk.AskForApproval;
using V2UserInput = CodeAlta.CodexSdk.UserInput;

namespace CodeAlta.Agent.Codex;

internal static class CodexAgentMapper
{
    public static AgentModelInfo ToAgentModelInfo(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var supportedReasoningEfforts = model.SupportedReasoningEfforts
            .Select(x => ToAgentReasoningEffort(x.ReasoningEffort))
            .Distinct()
            .ToArray();
        AgentReasoningEffort? defaultReasoningEffort = ToAgentReasoningEffort(model.DefaultReasoningEffort);
        if (supportedReasoningEfforts.Length > 0 &&
            defaultReasoningEffort == AgentReasoningEffort.XHigh &&
            defaultReasoningEffort is { } concreteDefaultReasoningEffort &&
            Array.IndexOf(supportedReasoningEfforts, concreteDefaultReasoningEffort) < 0)
        {
            defaultReasoningEffort = null;
        }

        var capabilities = new Dictionary<string, object?>
        {
            ["isDefault"] = model.IsDefault,
            ["hidden"] = model.Hidden,
            ["supportsPersonality"] = model.SupportsPersonality,
            ["upgrade"] = model.Upgrade,
            ["supportedReasoningEfforts"] = model.SupportedReasoningEfforts
                .Select(x => x.ReasoningEffort.ToString().ToLowerInvariant())
                .ToArray()
        };

        return new AgentModelInfo(
            model.Id,
            DisplayName: model.DisplayName,
            Description: model.Description,
            Provider: null,
            DefaultReasoningEffort: defaultReasoningEffort,
            SupportedReasoningEfforts: supportedReasoningEfforts,
            Capabilities: capabilities);
    }

    public static AgentSessionMetadata ToAgentSessionMetadata(CodexThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var repository = TryExtractRepository(thread.GitInfo?.OriginUrl);
        var context = new AgentSessionContext(
            Cwd: thread.Cwd,
            GitRoot: null,
            Repository: repository,
            Branch: thread.GitInfo?.Branch);

        return new AgentSessionMetadata(
            SessionId: thread.Id,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(thread.CreatedAt),
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(thread.UpdatedAt),
            Summary: thread.Preview,
            Context: context,
            WorkspacePath: thread.Path,
            Details: new CodexSessionMetadataDetails(
                ModelProvider: string.IsNullOrWhiteSpace(thread.ModelProvider) ? null : thread.ModelProvider,
                Source: thread.Source.ToString(),
                Status: thread.Status.ToString(),
                IsEphemeral: thread.Ephemeral,
                ThreadName: thread.Name));
    }

    public static bool MatchesFilter(CodexThread thread, AgentSessionListFilter? filter)
    {
        if (filter is null)
            return true;

        if (filter.Cwd is not null && !string.Equals(filter.Cwd, thread.Cwd, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.Branch is not null && !string.Equals(filter.Branch, thread.GitInfo?.Branch, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.Repository is not null)
        {
            var repository = TryExtractRepository(thread.GitInfo?.OriginUrl);
            if (!string.Equals(filter.Repository, repository, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static ThreadStartParams ToThreadStartParams(
        AgentSessionCreateOptions options,
        V2AskForApproval approvalPolicy,
        SandboxMode? sandboxMode)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ThreadStartParams
        {
            ApprovalPolicy = approvalPolicy,
            BaseInstructions = options.SystemMessage,
            Config = CreateThreadConfig(options.ReasoningEffort, options.McpServers),
            DeveloperInstructions = options.DeveloperInstructions,
            Cwd = options.WorkingDirectory,
            Model = options.Model,
            Sandbox = sandboxMode
        };
    }

    public static ThreadResumeParams ToThreadResumeParams(
        string threadId,
        AgentSessionResumeOptions options,
        V2AskForApproval approvalPolicy,
        SandboxMode? sandboxMode)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(options);

        return new ThreadResumeParams
        {
            ThreadId = threadId,
            ApprovalPolicy = approvalPolicy,
            BaseInstructions = options.SystemMessage,
            Config = CreateThreadConfig(options.ReasoningEffort, options.McpServers),
            DeveloperInstructions = options.DeveloperInstructions,
            Cwd = options.WorkingDirectory,
            Model = options.Model,
            Sandbox = sandboxMode
        };
    }

    public static TurnStartParams ToTurnStartParams(
        string threadId,
        AgentInput input,
        string? workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        SandboxMode? sandboxMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(input);

        return new TurnStartParams
        {
            ThreadId = threadId,
            Input = ToTurnInput(input),
            Cwd = workingDirectory,
            Model = model,
            Effort = ToCodexReasoningEffort(reasoningEffort),
            SandboxPolicy = CreateSandboxPolicy(sandboxMode, workingDirectory),
        };
    }

    public static TurnSteerParams ToTurnSteerParams(
        string threadId,
        AgentRunId expectedRunId,
        AgentInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId.Value);
        ArgumentNullException.ThrowIfNull(input);

        return new TurnSteerParams
        {
            ThreadId = threadId,
            ExpectedTurnId = expectedRunId.Value,
            Input = ToTurnInput(input)
        };
    }

    public static List<V2UserInput> ToTurnInput(AgentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var turnInput = new List<V2UserInput>(input.Items.Count);
        var textFallbackBuilder = new StringBuilder();

        foreach (var item in input.Items)
        {
            switch (item)
            {
                case AgentInputItem.Text text:
                    turnInput.Add(new V2UserInput.TextUserInput { Text = text.Value });
                    break;
                case AgentInputItem.ImageUrl imageUrl:
                    turnInput.Add(new V2UserInput.ImageUserInput { Url = imageUrl.Url });
                    break;
                case AgentInputItem.LocalImage localImage:
                    turnInput.Add(new V2UserInput.LocalImageUserInput { Path = localImage.Path });
                    break;
                case AgentInputItem.Skill skill:
                    turnInput.Add(new V2UserInput.SkillUserInput { Name = skill.Name, Path = skill.Path });
                    break;
                case AgentInputItem.Mention mention:
                    turnInput.Add(new V2UserInput.MentionUserInput { Name = mention.Name, Path = mention.Path });
                    break;
                case AgentInputItem.File file:
                    AppendAttachmentFallback(textFallbackBuilder, "file", file.Path, file.DisplayName, file.LineRange, content: null);
                    break;
                case AgentInputItem.Directory directory:
                    AppendAttachmentFallback(textFallbackBuilder, "directory", directory.Path, directory.DisplayName, directory.LineRange, content: null);
                    break;
                case AgentInputItem.Selection selection:
                    var lineRange = new AgentLineRange(selection.Range.Start.Line, selection.Range.End.Line);
                    AppendAttachmentFallback(textFallbackBuilder, "selection", selection.FilePath, selection.DisplayName, lineRange, selection.SelectedText);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item, "Unsupported input item.");
            }
        }

        if (textFallbackBuilder.Length > 0)
        {
            turnInput.Add(new V2UserInput.TextUserInput
            {
                Text = textFallbackBuilder.ToString()
            });
        }

        if (turnInput.Count == 0)
        {
            turnInput.Add(new V2UserInput.TextUserInput { Text = string.Empty });
        }

        return turnInput;
    }

    public static CommandExecutionRequestApprovalResponse ToCommandApprovalResponse(AgentPermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        CommandExecutionApprovalDecision mappedDecision;
        switch (decision.Kind)
        {
            case AgentPermissionDecisionKind.AllowOnce when decision.ExecPolicyAmendment is { Count: > 0 } amendment:
                mappedDecision = new CommandExecutionApprovalDecision.AcceptWithExecpolicyAmendment
                {
                    ExecpolicyAmendment = [.. amendment]
                };
                break;
            case AgentPermissionDecisionKind.AllowOnce when decision.NetworkPolicyAmendment is { } networkPolicyAmendment:
                mappedDecision = new CommandExecutionApprovalDecision.ApplyNetworkPolicyAmendment
                {
                    NetworkPolicyAmendment = ToNetworkPolicyAmendment(networkPolicyAmendment)
                };
                break;
            case AgentPermissionDecisionKind.AllowOnce:
                mappedDecision = new CommandExecutionApprovalDecision.Accept();
                break;
            case AgentPermissionDecisionKind.AllowForSession:
                mappedDecision = new CommandExecutionApprovalDecision.AcceptForSession();
                break;
            case AgentPermissionDecisionKind.Deny:
                mappedDecision = new CommandExecutionApprovalDecision.Decline();
                break;
            case AgentPermissionDecisionKind.Cancel:
                mappedDecision = new CommandExecutionApprovalDecision.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision.Kind, "Unsupported decision kind.");
        }

        return new CommandExecutionRequestApprovalResponse
        {
            Decision = mappedDecision
        };
    }

    public static FileChangeRequestApprovalResponse ToFileApprovalResponse(AgentPermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var mappedDecision = decision.Kind switch
        {
            AgentPermissionDecisionKind.AllowOnce => FileChangeApprovalDecision.Accept,
            AgentPermissionDecisionKind.AllowForSession => FileChangeApprovalDecision.AcceptForSession,
            AgentPermissionDecisionKind.Deny => FileChangeApprovalDecision.Decline,
            AgentPermissionDecisionKind.Cancel => FileChangeApprovalDecision.Cancel,
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision.Kind, "Unsupported decision kind.")
        };

        return new FileChangeRequestApprovalResponse
        {
            Decision = mappedDecision
        };
    }

    public static PermissionsRequestApprovalResponse ToPermissionsApprovalResponse(
        AgentPermissionDecision decision,
        PermissionsRequestApprovalParams request)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(request);

        return new PermissionsRequestApprovalResponse
        {
            Permissions = decision.Kind is AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession
                ? ToGrantedPermissionProfile(request.Permissions)
                : new GrantedPermissionProfile(),
            Scope = decision.Kind == AgentPermissionDecisionKind.AllowForSession
                ? PermissionGrantScope.Session
                : PermissionGrantScope.Turn
        };
    }

    public static AgentUserInputRequest ToAgentUserInputRequest(ToolRequestUserInputParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var prompts = parameters.Questions.Select(question =>
            new AgentUserInputPrompt(
                Id: question.Id,
                Question: question.Question,
                Header: string.IsNullOrWhiteSpace(question.Header) ? null : question.Header,
                Options: question.Options?.Select(static option => new AgentUserInputOption(option.Label, option.Description)).ToArray(),
                AllowFreeform: question.IsOther.GetValueOrDefault(true),
                IsSecret: question.IsSecret.GetValueOrDefault(false)))
            .ToArray();

        return new AgentUserInputRequest(
            AgentBackendIds.Codex,
            parameters.ThreadId,
            DateTimeOffset.UtcNow,
            new AgentRunId(parameters.TurnId),
            parameters.ItemId,
            new AgentUserInputForm(prompts));
    }

    public static ToolRequestUserInputResponse ToToolRequestUserInputResponse(AgentUserInputResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var answers = response.Answers.ToDictionary(
            pair => pair.Key,
            pair => new ToolRequestUserInputAnswer { Answers = [pair.Value] },
            StringComparer.Ordinal);

        return new ToolRequestUserInputResponse
        {
            Answers = answers
        };
    }

    public static ToolRequestUserInputResponse CreateEmptyToolRequestUserInputResponse(ToolRequestUserInputParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var answers = parameters.Questions.ToDictionary(
            question => question.Id,
            _ => new ToolRequestUserInputAnswer { Answers = [string.Empty] },
            StringComparer.Ordinal);

        return new ToolRequestUserInputResponse
        {
            Answers = answers
        };
    }

    public static AgentUserInputRequest ToAgentUserInputRequest(
        string sessionId,
        string interactionId,
        string message,
        JsonElement schema,
        string? turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);

        var required = GetRequiredProperties(schema);
        var prompts = new List<AgentUserInputPrompt>();
        if (schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var prompt = ToUserInputPrompt(property.Name, property.Value, message, required);
                if (prompt is not null)
                {
                    prompts.Add(prompt);
                }
            }
        }

        if (prompts.Count == 0)
        {
            prompts.Add(new AgentUserInputPrompt(
                Id: "value",
                Question: message,
                Header: schema.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String
                    ? titleProperty.GetString()
                    : null,
                AllowFreeform: true));
        }

        return new AgentUserInputRequest(
            AgentBackendIds.Codex,
            sessionId,
            DateTimeOffset.UtcNow,
            turnId is null ? null : new AgentRunId(turnId),
            interactionId,
            new AgentUserInputForm(prompts));
    }

    public static McpServerElicitationRequestResponse ToAcceptedMcpElicitationResponse(
        JsonElement schema,
        AgentUserInputResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!response.Answers.TryGetValue(property.Name, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                content[property.Name] = ConvertUserInputValue(property.Name, value, property.Value);
            }
        }

        return CreateAcceptedMcpElicitationResponse(
            CreateObjectElement(writer =>
            {
                foreach (var pair in content)
                {
                    writer.WritePropertyName(pair.Key);
                    pair.Value.WriteTo(writer);
                }
            }));
    }

    public static McpServerElicitationRequestResponse CreateAcceptedMcpElicitationResponse(
        JsonElement? content = null,
        JsonElement? meta = null)
        => new()
        {
            Action = McpServerElicitationAction.Accept,
            Content = content,
            Meta = meta
        };

    public static McpServerElicitationRequestResponse CreateDeclinedMcpElicitationResponse()
        => new()
        {
            Action = McpServerElicitationAction.Decline
        };

    public static McpServerElicitationRequestResponse CreateCanceledMcpElicitationResponse()
        => new()
        {
            Action = McpServerElicitationAction.Cancel
        };

    public static JsonElement SerializeMcpElicitationSchema(McpElicitationSchema? schema)
        => schema is null
            ? CreateNullElement()
            : JsonSerializer.SerializeToElement(schema, CodexJsonSerializerContext.Default.McpElicitationSchema);

    public static bool IsMcpToolApprovalElicitation(McpServerElicitationRequestParams.Form parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return TryGetMetaString(parameters.Meta, "codex_approval_kind") == "mcp_tool_call" &&
            IsNullOrEmptyObjectSchema(SerializeMcpElicitationSchema(parameters.RequestedSchema));
    }

    public static bool SupportsSessionPersistence(McpServerElicitationRequestParams.Form parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return MetaContainsValue(parameters.Meta, "persist", "session");
    }

    public static JsonElement CreateMcpSessionPersistenceMeta()
        => CreateObjectElement(writer => writer.WriteString("persist", "session"));

    public static JsonElement CreateEmptyObjectElement()
        => CreateObjectElement(static _ => { });

    public static DynamicToolCallResponse ToDynamicToolCallResponse(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var contentItems = new List<DynamicToolCallOutputContentItem>(result.Items.Count);
        foreach (var item in result.Items)
        {
            switch (item)
            {
                case AgentToolResultItem.Text text:
                    contentItems.Add(new DynamicToolCallOutputContentItem.InputTextDynamicToolCallOutputContentItem
                    {
                        Text = text.Value
                    });
                    break;
                case AgentToolResultItem.ImageUrl image:
                    contentItems.Add(new DynamicToolCallOutputContentItem.InputImageDynamicToolCallOutputContentItem
                    {
                        ImageUrl = image.Url
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item, "Unsupported tool result item.");
            }
        }

        if (!result.Success && result.Error is not null)
        {
            contentItems.Add(new DynamicToolCallOutputContentItem.InputTextDynamicToolCallOutputContentItem
            {
                Text = result.Error
            });
        }

        return new DynamicToolCallResponse
        {
            Success = result.Success,
            ContentItems = contentItems
        };
    }

    public static AgentEvent ToAgentEvent(string sessionId, CodexNotification notification, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(notification);

        return notification switch
        {
            CodexNotification.ThreadStarted => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Started,
                "Thread started."),

            CodexNotification.ThreadArchived => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Info,
                "Thread archived."),

            CodexNotification.ThreadUnarchived => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Info,
                "Thread unarchived."),

            CodexNotification.ThreadNameUpdated nameUpdated => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.TitleChanged,
                nameUpdated.Data.ThreadName),

            CodexNotification.ThreadStatusChanged statusChanged => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Info,
                $"Thread status: {statusChanged.Data.Status}."),

            CodexNotification.ThreadClosed => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Shutdown,
                "Thread closed."),

            CodexNotification.TurnStarted turnStarted => CreateActivity(
                sessionId,
                timestamp,
                new AgentRunId(turnStarted.Data.Turn.Id),
                AgentActivityKind.Turn,
                AgentActivityPhase.Started,
                turnStarted.Data.Turn.Id,
                null,
                "turn",
                null),

            CodexNotification.TurnDiffUpdated turnDiffUpdated => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.DiffUpdated,
                "Turn diff updated.",
                new AgentRunId(turnDiffUpdated.Data.TurnId),
                CreateDiffDetails(turnDiffUpdated.Data.Diff)),

            CodexNotification.AgentMessageDelta delta => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(delta.Data.TurnId),
                AgentContentKind.Assistant,
                delta.Data.ItemId,
                delta.Data.TurnId,
                delta.Data.Delta),

            CodexNotification.ReasoningTextDelta reasoning => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(reasoning.Data.TurnId),
                AgentContentKind.Reasoning,
                reasoning.Data.ItemId,
                reasoning.Data.TurnId,
                reasoning.Data.Delta),

            CodexNotification.ReasoningSummaryTextDelta reasoningSummary => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(reasoningSummary.Data.TurnId),
                AgentContentKind.ReasoningSummary,
                reasoningSummary.Data.ItemId,
                reasoningSummary.Data.TurnId,
                reasoningSummary.Data.Delta),

            CodexNotification.PlanDelta planDelta => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(planDelta.Data.TurnId),
                AgentContentKind.Plan,
                planDelta.Data.ItemId,
                planDelta.Data.TurnId,
                planDelta.Data.Delta),

            CodexNotification.CommandExecutionOutputDelta commandOutput => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(commandOutput.Data.TurnId),
                AgentContentKind.CommandOutput,
                commandOutput.Data.ItemId,
                commandOutput.Data.TurnId,
                commandOutput.Data.Delta),

            CodexNotification.FileChangeOutputDelta fileChangeOutput => new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(fileChangeOutput.Data.TurnId),
                AgentContentKind.FileChangeOutput,
                fileChangeOutput.Data.ItemId,
                fileChangeOutput.Data.TurnId,
                fileChangeOutput.Data.Delta),

            CodexNotification.McpToolCallProgress mcpToolProgress => CreateActivity(
                sessionId,
                timestamp,
                new AgentRunId(mcpToolProgress.Data.TurnId),
                AgentActivityKind.McpToolCall,
                AgentActivityPhase.Progressed,
                mcpToolProgress.Data.ItemId,
                mcpToolProgress.Data.TurnId,
                "MCP tool call",
                mcpToolProgress.Data.Message),

            CodexNotification.CommandExecutionTerminalInteraction terminalInteraction => CreateActivity(
                sessionId,
                timestamp,
                new AgentRunId(terminalInteraction.Data.TurnId),
                AgentActivityKind.CommandExecution,
                AgentActivityPhase.Progressed,
                terminalInteraction.Data.ItemId,
                terminalInteraction.Data.TurnId,
                "terminal interaction",
                terminalInteraction.Data.Stdin),

            CodexNotification.TurnPlanUpdated planUpdated => new AgentPlanSnapshotEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(planUpdated.Data.TurnId),
                new AgentPlanSnapshot(
                    ChangeKind: AgentPlanChangeKind.Updated,
                    Explanation: planUpdated.Data.Explanation,
                    Steps: planUpdated.Data.Plan.Select(static step => new AgentPlanStep(
                        step.Step,
                        step.Status switch
                        {
                            TurnPlanStepStatus.Pending => AgentPlanStepStatus.Pending,
                            TurnPlanStepStatus.InProgress => AgentPlanStepStatus.InProgress,
                            TurnPlanStepStatus.Completed => AgentPlanStepStatus.Completed,
                            _ => null
                        })).ToArray())),

            CodexNotification.ItemStarted itemStarted => ToItemStartedEvent(sessionId, itemStarted.Data, timestamp),

            CodexNotification.ItemCompleted itemCompleted => ToItemCompletedEvent(sessionId, itemCompleted.Data, timestamp),

            CodexNotification.RawResponseItemCompleted rawResponseItemCompleted => ToRawResponseItemCompletedEvent(
                sessionId,
                rawResponseItemCompleted.Data,
                timestamp),

            CodexNotification.TurnCompleted turnCompleted => new AgentSessionUpdateEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(turnCompleted.Data.Turn.Id),
                AgentSessionUpdateKind.Idle,
                null),

            CodexNotification.ThreadTokenUsageUpdated tokenUsage => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.UsageUpdated,
                BuildCodexUsageMessage(tokenUsage.Data.TokenUsage.Total.TotalTokens, tokenUsage.Data.TokenUsage.ModelContextWindow),
                new AgentRunId(tokenUsage.Data.TurnId),
                usage: CreateCodexThreadUsage(timestamp, tokenUsage.Data.TokenUsage)),

            CodexNotification.AccountRateLimitsUpdated rateLimitsUpdated => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.UsageUpdated,
                "Codex rate limits updated.",
                usage: CreateCodexRateLimitUsage(timestamp, rateLimitsUpdated.Data.RateLimits)),

            CodexNotification.ThreadCompacted threadCompacted => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.CompactionCompleted,
                "Thread compacted.",
                new AgentRunId(threadCompacted.Data.TurnId)),

            CodexNotification.ConfigWarning configWarning => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Warning,
                configWarning.Data.Summary),

            CodexNotification.DeprecationNotice deprecationNotice => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.Warning,
                deprecationNotice.Data.Summary),

            CodexNotification.ModelRerouted modelRerouted => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.ModelChanged,
                $"{modelRerouted.Data.FromModel} → {modelRerouted.Data.ToModel}",
                new AgentRunId(modelRerouted.Data.TurnId)),

            CodexNotification.ServerRequestResolved resolved => new AgentInteractionEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                null,
                AgentInteractionKind.PermissionResolved,
                resolved.Data.RequestId.ToString(),
                "Server request resolved."),

            CodexNotification.Error error => new AgentErrorEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                error.Data.Error.Message,
                null,
                new AgentRunId(error.Data.TurnId)),

            _ => new AgentRawEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                notification.GetType().Name,
                SerializeNotification(notification),
                TryGetTurnId(notification))
        };
    }

    public static IReadOnlyList<AgentEvent> ToHistoryEvents(string sessionId, CodexThread thread)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(thread);

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(thread.UpdatedAt);
        var events = new List<AgentEvent>();
        foreach (var turn in thread.Turns)
        {
            var runId = new AgentRunId(turn.Id);
            foreach (var item in turn.Items)
            {
                events.Add(ToItemCompletedEvent(
                    sessionId,
                    new ItemCompletedNotification
                    {
                        ThreadId = sessionId,
                        TurnId = turn.Id,
                        Item = item
                    },
                    timestamp));
            }

            if (turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Interrupted)
            {
                events.Add(new AgentSessionUpdateEvent(
                    AgentBackendIds.Codex,
                    sessionId,
                    timestamp,
                    runId,
                    AgentSessionUpdateKind.Idle,
                    null));
            }
        }

        return events;
    }

    private static AgentSessionUpdateEvent CreateSessionUpdate(
        string sessionId,
        DateTimeOffset timestamp,
        AgentSessionUpdateKind kind,
        string? message,
        AgentRunId? runId = null,
        JsonElement? details = null,
        AgentSessionUsage? usage = null)
    {
        return new AgentSessionUpdateEvent(
            AgentBackendIds.Codex,
            sessionId,
            timestamp,
            runId,
            kind,
            message,
            details,
            usage);
    }

    private static AgentSessionUsage CreateCodexThreadUsage(DateTimeOffset timestamp, ThreadTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new AgentSessionUsage(
            Window: CreateCodexWindowUsage(ToCodexTokenUsage(usage.Last), usage.ModelContextWindow),
            LastOperation: ToAgentOperationUsage(usage.Last, "Last turn"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexThreadTokenUsageUpdated,
            UpdatedAt: timestamp,
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: ToCodexTokenUsage(usage.Last),
                TotalUsage: ToCodexTokenUsage(usage.Total),
                ModelContextWindow: usage.ModelContextWindow));
    }

    private static AgentSessionUsage CreateCodexRateLimitUsage(DateTimeOffset timestamp, RateLimitSnapshot rateLimits)
    {
        ArgumentNullException.ThrowIfNull(rateLimits);

        return new AgentSessionUsage(
            RateLimits: ToAgentRateLimitSummary(rateLimits, "Account rate limits"),
            Scope: AgentUsageScope.RateLimitOnly,
            Source: AgentUsageSource.CodexAccountRateLimitsUpdated,
            UpdatedAt: timestamp,
            Details: new CodexSessionUsageDetails(
                RateLimits: ToCodexRateLimitSnapshot(rateLimits)));
    }

    private static AgentWindowUsageSnapshot? CreateCodexWindowUsage(CodexTokenUsage? usage, long? modelContextWindow)
    {
        if (usage is null || modelContextWindow is not > 0)
        {
            return null;
        }

        return new AgentWindowUsageSnapshot(
            usage.TotalTokens,
            modelContextWindow,
            null,
            "Active context window");
    }

    private static AgentOperationUsageSnapshot ToAgentOperationUsage(TokenUsageBreakdown usage, string label)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new AgentOperationUsageSnapshot(
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            CachedInputTokens: usage.CachedInputTokens,
            ReasoningTokens: usage.ReasoningOutputTokens,
            Label: label);
    }

    private static AgentRateLimitSummary? ToAgentRateLimitSummary(RateLimitSnapshot? rateLimits, string label)
    {
        if (rateLimits is null)
        {
            return null;
        }

        return new AgentRateLimitSummary(
            Name: rateLimits.LimitName ?? rateLimits.LimitId,
            PlanType: rateLimits.PlanType?.ToString(),
            Primary: ToAgentRateLimitWindow(rateLimits.Primary),
            Secondary: ToAgentRateLimitWindow(rateLimits.Secondary),
            Label: label);
    }

    private static AgentRateLimitWindow? ToAgentRateLimitWindow(RateLimitWindow? window)
    {
        if (window is null)
        {
            return null;
        }

        return new AgentRateLimitWindow(
            window.UsedPercent,
            window.ResetsAt is { } resetsAt ? DateTimeOffset.FromUnixTimeSeconds(resetsAt) : null,
            window.WindowDurationMins);
    }

    private static CodexTokenUsage ToCodexTokenUsage(TokenUsageBreakdown usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new CodexTokenUsage(
            usage.CachedInputTokens,
            usage.InputTokens,
            usage.OutputTokens,
            usage.ReasoningOutputTokens,
            usage.TotalTokens);
    }

    private static CodexRateLimitSnapshot? ToCodexRateLimitSnapshot(RateLimitSnapshot? rateLimits)
    {
        if (rateLimits is null)
        {
            return null;
        }

        return new CodexRateLimitSnapshot(
            rateLimits.LimitId,
            rateLimits.LimitName,
            rateLimits.PlanType?.ToString(),
            ToCodexRateLimitWindow(rateLimits.Primary),
            ToCodexRateLimitWindow(rateLimits.Secondary));
    }

    private static CodexRateLimitWindow? ToCodexRateLimitWindow(RateLimitWindow? window)
    {
        if (window is null)
        {
            return null;
        }

        return new CodexRateLimitWindow(
            window.UsedPercent,
            window.ResetsAt is { } resetsAt ? DateTimeOffset.FromUnixTimeSeconds(resetsAt) : null,
            window.WindowDurationMins);
    }

    private static string BuildCodexUsageMessage(long? totalThreadTokens, long? modelContextWindow)
    {
        var parts = new List<string>();
        if (totalThreadTokens is { } total)
        {
            parts.Add(FormattableString.Invariant($"{total:0} total thread tokens"));
        }

        if (modelContextWindow is > 0)
        {
            parts.Add(FormattableString.Invariant($"{modelContextWindow:0} token window"));
        }

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : "Token usage updated.";
    }

    private static AgentActivityEvent CreateActivity(
        string sessionId,
        DateTimeOffset timestamp,
        AgentRunId? runId,
        AgentActivityKind kind,
        AgentActivityPhase phase,
        string activityId,
        string? parentActivityId,
        string? name,
        string? message,
        JsonElement? details = null)
    {
        return new AgentActivityEvent(
            AgentBackendIds.Codex,
            sessionId,
            timestamp,
            runId,
            kind,
            phase,
            activityId,
            parentActivityId,
            name,
            message,
            details);
    }

    private static AgentEvent ToItemStartedEvent(
        string sessionId,
        ItemStartedNotification notification,
        DateTimeOffset timestamp)
    {
        var runId = new AgentRunId(notification.TurnId);
        return notification.Item switch
        {
            ThreadItem.CommandExecutionThreadItem commandExecution => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CommandExecution,
                AgentActivityPhase.Started,
                commandExecution.Id,
                notification.TurnId,
                ResolveCommandText(commandExecution.CommandActions, commandExecution.Command),
                commandExecution.Cwd,
                CreateCommandExecutionDetails(commandExecution)),

            ThreadItem.FileChangeThreadItem fileChange => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.FileChange,
                AgentActivityPhase.Started,
                fileChange.Id,
                notification.TurnId,
                "file change",
                null,
                CreateFileChangeDetails(fileChange)),

            ThreadItem.McpToolCallThreadItem mcpToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.McpToolCall,
                AgentActivityPhase.Started,
                mcpToolCall.Id,
                notification.TurnId,
                mcpToolCall.Tool,
                mcpToolCall.Server,
                CreateMcpToolCallDetails(mcpToolCall)),

            ThreadItem.DynamicToolCallThreadItem dynamicToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.DynamicToolCall,
                AgentActivityPhase.Started,
                dynamicToolCall.Id,
                notification.TurnId,
                dynamicToolCall.Tool,
                null,
                CreateDynamicToolCallDetails(dynamicToolCall)),

            ThreadItem.CollabAgentToolCallThreadItem collabAgentToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CollabAgentToolCall,
                AgentActivityPhase.Started,
                collabAgentToolCall.Id,
                notification.TurnId,
                collabAgentToolCall.Tool.ToString(),
                collabAgentToolCall.Prompt,
                CreateCollabAgentToolCallDetails(collabAgentToolCall)),

            ThreadItem.WebSearchThreadItem webSearch => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.WebSearch,
                AgentActivityPhase.Started,
                webSearch.Id,
                notification.TurnId,
                DescribeWebSearchAction(webSearch.Action, webSearch.Query),
                webSearch.Query,
                CreateWebSearchDetails(webSearch)),

            ThreadItem.ImageGenerationThreadItem imageGeneration => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.ImageGeneration,
                ToImageGenerationPhase(imageGeneration.Status, AgentActivityPhase.Started),
                imageGeneration.Id,
                notification.TurnId,
                "image generation",
                imageGeneration.RevisedPrompt,
                CreateImageGenerationDetails(imageGeneration)),

            ThreadItem.ContextCompactionThreadItem contextCompaction => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.Compaction,
                AgentActivityPhase.Started,
                contextCompaction.Id,
                notification.TurnId,
                "context compaction",
                null),

            ThreadItem.EnteredReviewModeThreadItem enteredReviewMode => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.ModeChanged,
                $"Entered review mode: {enteredReviewMode.Review}.",
                runId),

            ThreadItem.ExitedReviewModeThreadItem exitedReviewMode => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.ModeChanged,
                $"Exited review mode: {exitedReviewMode.Review}.",
                runId),

            _ => new AgentRawEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                nameof(CodexNotification.ItemStarted),
                SerializeNotification(new CodexNotification.ItemStarted(notification)),
                runId)
        };
    }

    private static AgentEvent ToItemCompletedEvent(
        string sessionId,
        ItemCompletedNotification notification,
        DateTimeOffset timestamp)
    {
        var runId = new AgentRunId(notification.TurnId);
        return notification.Item switch
        {
            ThreadItem.UserMessageThreadItem userMessage => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                AgentContentKind.User,
                userMessage.Id,
                notification.TurnId,
                ExtractUserMessageText(userMessage)),

            ThreadItem.AgentMessageThreadItem message => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                ToAgentContentKind(message.Phase),
                message.Id,
                notification.TurnId,
                message.Text),

            ThreadItem.ReasoningThreadItem reasoning => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                AgentContentKind.Reasoning,
                reasoning.Id,
                notification.TurnId,
                ExtractReasoningThreadText(reasoning)),

            ThreadItem.PlanThreadItem plan => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                AgentContentKind.Plan,
                plan.Id,
                notification.TurnId,
                plan.Text),

            ThreadItem.CommandExecutionThreadItem commandExecution => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CommandExecution,
                ToActivityPhase(commandExecution.Status),
                commandExecution.Id,
                notification.TurnId,
                ResolveCommandText(commandExecution.CommandActions, commandExecution.Command),
                commandExecution.AggregatedOutput,
                CreateCommandExecutionDetails(commandExecution)),

            ThreadItem.FileChangeThreadItem fileChange => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.FileChange,
                ToActivityPhase(fileChange.Status),
                fileChange.Id,
                notification.TurnId,
                "file change",
                fileChange.Changes.Count == 0 ? null : $"{fileChange.Changes.Count} change(s)",
                CreateFileChangeDetails(fileChange)),

            ThreadItem.McpToolCallThreadItem mcpToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.McpToolCall,
                ToActivityPhase(mcpToolCall.Status),
                mcpToolCall.Id,
                notification.TurnId,
                mcpToolCall.Tool,
                GetMcpToolCallMessage(mcpToolCall),
                CreateMcpToolCallDetails(mcpToolCall)),

            ThreadItem.DynamicToolCallThreadItem dynamicToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.DynamicToolCall,
                ToActivityPhase(dynamicToolCall.Status),
                dynamicToolCall.Id,
                notification.TurnId,
                dynamicToolCall.Tool,
                dynamicToolCall.Success is { } success
                    ? success ? "Dynamic tool call succeeded." : "Dynamic tool call failed."
                    : null,
                CreateDynamicToolCallDetails(dynamicToolCall)),

            ThreadItem.CollabAgentToolCallThreadItem collabAgentToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CollabAgentToolCall,
                ToActivityPhase(collabAgentToolCall.Status),
                collabAgentToolCall.Id,
                notification.TurnId,
                collabAgentToolCall.Tool.ToString(),
                collabAgentToolCall.Prompt,
                CreateCollabAgentToolCallDetails(collabAgentToolCall)),

            ThreadItem.WebSearchThreadItem webSearch => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.WebSearch,
                AgentActivityPhase.Completed,
                webSearch.Id,
                notification.TurnId,
                DescribeWebSearchAction(webSearch.Action, webSearch.Query),
                webSearch.Query,
                CreateWebSearchDetails(webSearch)),

            ThreadItem.ImageGenerationThreadItem imageGeneration => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.ImageGeneration,
                ToImageGenerationPhase(imageGeneration.Status, AgentActivityPhase.Completed),
                imageGeneration.Id,
                notification.TurnId,
                "image generation",
                imageGeneration.RevisedPrompt,
                CreateImageGenerationDetails(imageGeneration)),

            ThreadItem.ContextCompactionThreadItem contextCompaction => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.Compaction,
                AgentActivityPhase.Completed,
                contextCompaction.Id,
                notification.TurnId,
                "context compaction",
                null),

            ThreadItem.EnteredReviewModeThreadItem enteredReviewMode => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.ModeChanged,
                $"Entered review mode: {enteredReviewMode.Review}.",
                runId),

            ThreadItem.ExitedReviewModeThreadItem exitedReviewMode => CreateSessionUpdate(
                sessionId,
                timestamp,
                AgentSessionUpdateKind.ModeChanged,
                $"Exited review mode: {exitedReviewMode.Review}.",
                runId),

            _ => new AgentRawEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                nameof(CodexNotification.ItemCompleted),
                SerializeNotification(new CodexNotification.ItemCompleted(notification)),
                runId)
        };
    }

    private static AgentEvent ToRawResponseItemCompletedEvent(
        string sessionId,
        RawResponseItemCompletedNotification notification,
        DateTimeOffset timestamp)
    {
        var runId = new AgentRunId(notification.TurnId);
        return notification.Item switch
        {
            ResponseItem.MessageResponseItem message => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                ToAgentContentKind(message.Phase),
                message.Id ?? $"message:{notification.TurnId}",
                notification.TurnId,
                ExtractMessageResponseText(message)),

            ResponseItem.ReasoningResponseItem reasoning => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                AgentContentKind.Reasoning,
                $"reasoning:{notification.TurnId}",
                notification.TurnId,
                ExtractReasoningResponseText(reasoning)),

            ResponseItem.LocalShellCallResponseItem localShellCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CommandExecution,
                ToActivityPhase(localShellCall.Status),
                localShellCall.CallId ?? localShellCall.Id ?? $"local-shell:{notification.TurnId}",
                notification.TurnId,
                DescribeLocalShellAction(localShellCall.Action),
                null,
                CreateLocalShellCallDetails(localShellCall)),

            ResponseItem.FunctionCallResponseItem functionCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Requested,
                functionCall.CallId,
                notification.TurnId,
                functionCall.Name,
                null,
                CreateFunctionCallDetails(functionCall)),

            ResponseItem.FunctionCallOutputResponseItem functionCallOutput => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.ToolCall,
                ToFunctionCallOutputPhase(functionCallOutput.Output),
                functionCallOutput.CallId,
                notification.TurnId,
                "function call output",
                GetFunctionCallOutputMessage(functionCallOutput.Output),
                CreateFunctionCallOutputDetails(functionCallOutput)),

            ResponseItem.CustomToolCallResponseItem customToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.DynamicToolCall,
                ToCustomToolCallPhase(customToolCall.Status),
                customToolCall.CallId,
                notification.TurnId,
                customToolCall.Name,
                null,
                CreateCustomToolCallDetails(customToolCall)),

            ResponseItem.CustomToolCallOutputResponseItem customToolCallOutput => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.DynamicToolCall,
                ToFunctionCallOutputPhase(customToolCallOutput.Output),
                customToolCallOutput.CallId,
                notification.TurnId,
                "custom tool output",
                GetFunctionCallOutputMessage(customToolCallOutput.Output),
                CreateCustomToolCallOutputDetails(customToolCallOutput)),

            ResponseItem.WebSearchCallResponseItem webSearchCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.WebSearch,
                ToStatusPhase(webSearchCall.Status, AgentActivityPhase.Completed),
                webSearchCall.Id ?? $"web-search:{notification.TurnId}",
                notification.TurnId,
                DescribeWebSearchAction(webSearchCall.Action, fallbackQuery: null),
                null,
                CreateWebSearchCallDetails(webSearchCall)),

            ResponseItem.ImageGenerationCallResponseItem imageGenerationCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.ImageGeneration,
                ToImageGenerationPhase(imageGenerationCall.Status, AgentActivityPhase.Completed),
                imageGenerationCall.Id,
                notification.TurnId,
                "image generation",
                imageGenerationCall.RevisedPrompt,
                CreateImageGenerationCallDetails(imageGenerationCall)),

            ResponseItem.CompactionResponseItem => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.Compaction,
                AgentActivityPhase.Completed,
                $"compaction:{notification.TurnId}",
                notification.TurnId,
                "context compaction",
                null),

            _ => new AgentRawEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                nameof(CodexNotification.RawResponseItemCompleted),
                CreateRawElement(
                    notification.Item.GetType().Name,
                    notification.ThreadId,
                    notification.TurnId),
                runId)
        };
    }

    private static AgentActivityPhase ToActivityPhase(CommandExecutionStatus status)
    {
        return status switch
        {
            CommandExecutionStatus.InProgress => AgentActivityPhase.Progressed,
            CommandExecutionStatus.Completed => AgentActivityPhase.Completed,
            CommandExecutionStatus.Failed => AgentActivityPhase.Failed,
            CommandExecutionStatus.Declined => AgentActivityPhase.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported command execution status.")
        };
    }

    private static AgentActivityPhase ToActivityPhase(PatchApplyStatus status)
    {
        return status switch
        {
            PatchApplyStatus.InProgress => AgentActivityPhase.Progressed,
            PatchApplyStatus.Completed => AgentActivityPhase.Completed,
            PatchApplyStatus.Failed => AgentActivityPhase.Failed,
            PatchApplyStatus.Declined => AgentActivityPhase.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported file change status.")
        };
    }

    private static AgentActivityPhase ToActivityPhase(McpToolCallStatus status)
    {
        return status switch
        {
            McpToolCallStatus.InProgress => AgentActivityPhase.Progressed,
            McpToolCallStatus.Completed => AgentActivityPhase.Completed,
            McpToolCallStatus.Failed => AgentActivityPhase.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported MCP tool status.")
        };
    }

    private static AgentActivityPhase ToActivityPhase(DynamicToolCallStatus status)
    {
        return status switch
        {
            DynamicToolCallStatus.InProgress => AgentActivityPhase.Progressed,
            DynamicToolCallStatus.Completed => AgentActivityPhase.Completed,
            DynamicToolCallStatus.Failed => AgentActivityPhase.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported dynamic tool status.")
        };
    }

    private static AgentActivityPhase ToActivityPhase(CollabAgentToolCallStatus status)
    {
        return status switch
        {
            CollabAgentToolCallStatus.InProgress => AgentActivityPhase.Progressed,
            CollabAgentToolCallStatus.Completed => AgentActivityPhase.Completed,
            CollabAgentToolCallStatus.Failed => AgentActivityPhase.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported collaboration status.")
        };
    }

    private static AgentActivityPhase ToActivityPhase(LocalShellStatus status)
    {
        return status switch
        {
            LocalShellStatus.InProgress => AgentActivityPhase.Progressed,
            LocalShellStatus.Completed => AgentActivityPhase.Completed,
            LocalShellStatus.Incomplete => AgentActivityPhase.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported local shell status.")
        };
    }

    private static AgentActivityPhase ToStatusPhase(string? status, AgentActivityPhase defaultPhase)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultPhase,
            "requested" => AgentActivityPhase.Requested,
            "started" or "in_progress" or "in-progress" => AgentActivityPhase.Started,
            "completed" or "succeeded" or "success" => AgentActivityPhase.Completed,
            "failed" or "error" => AgentActivityPhase.Failed,
            "cancelled" or "canceled" or "declined" or "incomplete" => AgentActivityPhase.Canceled,
            _ => defaultPhase
        };
    }

    private static AgentActivityPhase ToImageGenerationPhase(string? status, AgentActivityPhase defaultPhase)
        => ToStatusPhase(status, defaultPhase);

    private static AgentActivityPhase ToCustomToolCallPhase(string? status)
        => ToStatusPhase(status, AgentActivityPhase.Requested);

    private static AgentActivityPhase ToFunctionCallOutputPhase(FunctionCallOutputBody payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return AgentActivityPhase.Completed;
    }

    private static string? GetFunctionCallOutputMessage(FunctionCallOutputBody payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return TryGetFunctionCallOutputText(payload);
    }

    public static bool TryGetThreadId(CodexNotification notification, out string? threadId)
    {
        ArgumentNullException.ThrowIfNull(notification);

        threadId = notification switch
        {
            CodexNotification.ThreadStarted value => value.Data.Thread.Id,
            CodexNotification.ThreadArchived value => value.Data.ThreadId,
            CodexNotification.ThreadUnarchived value => value.Data.ThreadId,
            CodexNotification.ThreadNameUpdated value => value.Data.ThreadId,
            CodexNotification.TurnStarted value => value.Data.ThreadId,
            CodexNotification.TurnCompleted value => value.Data.ThreadId,
            CodexNotification.TurnDiffUpdated value => value.Data.ThreadId,
            CodexNotification.TurnPlanUpdated value => value.Data.ThreadId,
            CodexNotification.ItemStarted value => value.Data.ThreadId,
            CodexNotification.ItemCompleted value => value.Data.ThreadId,
            CodexNotification.RawResponseItemCompleted value => value.Data.ThreadId,
            CodexNotification.AgentMessageDelta value => value.Data.ThreadId,
            CodexNotification.PlanDelta value => value.Data.ThreadId,
            CodexNotification.CommandExecutionOutputDelta value => value.Data.ThreadId,
            CodexNotification.FileChangeOutputDelta value => value.Data.ThreadId,
            CodexNotification.McpToolCallProgress value => value.Data.ThreadId,
            CodexNotification.ReasoningSummaryTextDelta value => value.Data.ThreadId,
            CodexNotification.ReasoningSummaryPartAdded value => value.Data.ThreadId,
            CodexNotification.ReasoningTextDelta value => value.Data.ThreadId,
            CodexNotification.ThreadTokenUsageUpdated value => value.Data.ThreadId,
            CodexNotification.ThreadCompacted value => value.Data.ThreadId,
            CodexNotification.Error value => value.Data.ThreadId,
            _ => null
        };

        return threadId is not null;
    }

    public static bool TryGetThreadId(ServerRequest request, out string? threadId)
    {
        ArgumentNullException.ThrowIfNull(request);

        threadId = request switch
        {
            ServerRequest.ItemCommandExecutionRequestApprovalRequest value => value.Params.ThreadId,
            ServerRequest.ItemFileChangeRequestApprovalRequest value => value.Params.ThreadId,
            ServerRequest.ItemToolRequestUserInputRequest value => value.Params.ThreadId,
            ServerRequest.McpServerElicitationRequestRequest value => value.Params.ThreadId,
            ServerRequest.ItemPermissionsRequestApprovalRequest value => value.Params.ThreadId,
            ServerRequest.ItemToolCallRequest value => value.Params.ThreadId,
            _ => null
        };

        return threadId is not null;
    }

    public static bool TryGetThreadId(CodexUnknownServerRequest request, out string? threadId)
    {
        ArgumentNullException.ThrowIfNull(request);

        threadId = request.Params.ValueKind == JsonValueKind.Object &&
                   request.Params.TryGetProperty("threadId", out var threadIdProp) &&
                   threadIdProp.ValueKind == JsonValueKind.String
            ? threadIdProp.GetString()
            : null;

        return threadId is not null;
    }

    public static AgentPermissionRequest ToPermissionRequest(
        string sessionId,
        CommandExecutionRequestApprovalParams data)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(data);

        return new AgentCommandPermissionRequest(
            AgentBackendIds.Codex,
            sessionId,
            DateTimeOffset.UtcNow,
            new AgentRunId(data.TurnId),
            data.ApprovalId ?? data.ItemId,
            data.ApprovalId,
            data.Command,
            data.Cwd,
            data.CommandActions?.Select(ToAgentCommandPreviewAction).ToArray(),
            data.Reason,
            data.NetworkApprovalContext is null
                ? null
                : new AgentNetworkAccessRequest(
                    data.NetworkApprovalContext.Host,
                    data.NetworkApprovalContext.Protocol.ToString().ToLowerInvariant()),
            data.ProposedExecpolicyAmendment,
            data.ProposedNetworkPolicyAmendments?.Select(ToAgentNetworkPolicyAmendment).ToArray());
    }

    public static AgentPermissionRequest ToPermissionRequest(
        string sessionId,
        FileChangeRequestApprovalParams data)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(data);

        return new AgentFileChangePermissionRequest(
            AgentBackendIds.Codex,
            sessionId,
            DateTimeOffset.UtcNow,
            new AgentRunId(data.TurnId),
            data.ItemId,
            data.GrantRoot,
            data.Reason);
    }

    public static AgentPermissionRequest ToPermissionRequest(
        string sessionId,
        PermissionsRequestApprovalParams data)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(data);

        return new AgentGenericPermissionRequest(
            AgentBackendIds.Codex,
            sessionId,
            DateTimeOffset.UtcNow,
            new AgentRunId(data.TurnId),
            data.ItemId,
            Kind: "permissionsRequestApproval",
            JsonSerializer.SerializeToElement(data, CodexJsonSerializerContext.Default.PermissionsRequestApprovalParams));
    }

    public static AgentRunId? TryGetTurnId(CodexNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var turnId = notification switch
        {
            CodexNotification.TurnStarted value => value.Data.Turn.Id,
            CodexNotification.TurnCompleted value => value.Data.Turn.Id,
            CodexNotification.TurnDiffUpdated value => value.Data.TurnId,
            CodexNotification.TurnPlanUpdated value => value.Data.TurnId,
            CodexNotification.ItemStarted value => value.Data.TurnId,
            CodexNotification.ItemCompleted value => value.Data.TurnId,
            CodexNotification.RawResponseItemCompleted value => value.Data.TurnId,
            CodexNotification.AgentMessageDelta value => value.Data.TurnId,
            CodexNotification.PlanDelta value => value.Data.TurnId,
            CodexNotification.CommandExecutionOutputDelta value => value.Data.TurnId,
            CodexNotification.FileChangeOutputDelta value => value.Data.TurnId,
            CodexNotification.McpToolCallProgress value => value.Data.TurnId,
            CodexNotification.ReasoningSummaryTextDelta value => value.Data.TurnId,
            CodexNotification.ReasoningSummaryPartAdded value => value.Data.TurnId,
            CodexNotification.ReasoningTextDelta value => value.Data.TurnId,
            CodexNotification.ThreadTokenUsageUpdated value => value.Data.TurnId,
            CodexNotification.Error value => value.Data.TurnId,
            _ => null
        };

        return turnId is null ? null : new AgentRunId(turnId);
    }

    public static string? TryExtractRepository(string? originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            return null;

        var trimmed = originUrl.Trim();
        var path = trimmed switch
        {
            var value when value.StartsWith("git@", StringComparison.OrdinalIgnoreCase) =>
                value[(value.IndexOf(':') + 1)..],
            _ when Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) => uri.AbsolutePath.Trim('/'),
            _ => trimmed
        };

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        return $"{segments[^2]}/{segments[^1]}";
    }

    private static void AppendAttachmentFallback(
        StringBuilder builder,
        string type,
        string path,
        string? displayName,
        AgentLineRange? lineRange,
        string? content)
    {
        if (builder.Length > 0)
            builder.AppendLine();

        builder.AppendLine($"[{type}] {displayName ?? path}");
        builder.AppendLine($"path: {path}");
        if (lineRange is not null)
            builder.AppendLine($"lines: {lineRange.StartLine}-{lineRange.EndLine}");
        if (!string.IsNullOrEmpty(content))
        {
            builder.AppendLine("content:");
            builder.AppendLine(content);
        }
    }

    private static JsonElement SerializeNotification(CodexNotification notification)
    {
        return notification switch
        {
            CodexNotification.Unknown value => value.Params,
            _ => CreateRawElement(
                notification.GetType().Name,
                TryGetThreadId(notification, out var threadId) ? threadId : null,
                TryGetTurnId(notification)?.Value)
        };
    }

    private static JsonElement CreateRawElement(string type, string? threadId = null, string? turnId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            if (threadId is not null)
                writer.WriteString("threadId", threadId);
            if (turnId is not null)
                writer.WriteString("turnId", turnId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateRawElementFromCommand(CommandExecutionRequestApprovalParams data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("itemId", data.ItemId);
            writer.WriteString("threadId", data.ThreadId);
            writer.WriteString("turnId", data.TurnId);
            if (data.ApprovalId is not null)
                writer.WriteString("approvalId", data.ApprovalId);
            if (data.Command is not null)
                writer.WriteString("command", data.Command);
            if (data.Cwd is not null)
                writer.WriteString("cwd", data.Cwd);
            if (data.Reason is not null)
                writer.WriteString("reason", data.Reason);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateRawElementFromFileChange(FileChangeRequestApprovalParams data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("itemId", data.ItemId);
            writer.WriteString("threadId", data.ThreadId);
            writer.WriteString("turnId", data.TurnId);
            if (data.GrantRoot is not null)
                writer.WriteString("grantRoot", data.GrantRoot);
            if (data.Reason is not null)
                writer.WriteString("reason", data.Reason);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateDiffDetails(string diff)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("diff", diff);
        });
    }

    private static JsonElement CreateCommandExecutionDetails(ThreadItem.CommandExecutionThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("command", ResolveCommandText(item.CommandActions, item.Command));
            writer.WriteString("rawCommand", item.Command);
            WriteCommandActions(writer, item.CommandActions);
            writer.WriteString("cwd", item.Cwd);
            writer.WriteString("status", item.Status.ToString());
            if (item.ProcessId is not null)
                writer.WriteString("processId", item.ProcessId);
            if (item.ExitCode is not null)
                writer.WriteNumber("exitCode", item.ExitCode.Value);
            if (item.DurationMs is not null)
                writer.WriteNumber("durationMs", item.DurationMs.Value);
        });
    }

    private static JsonElement CreateFileChangeDetails(ThreadItem.FileChangeThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("status", item.Status.ToString());
            writer.WriteNumber("changeCount", item.Changes.Count);
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            foreach (var change in item.Changes)
            {
                writer.WriteStartObject();
                writer.WriteString("path", change.Path);
                writer.WriteString("diff", change.Diff);
                writer.WritePropertyName("kind");
                JsonSerializer.Serialize(writer, change.Kind, CodexJsonSerializerContext.Default.PatchChangeKind);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static ISet<string>? GetRequiredProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var requiredProperty) ||
            requiredProperty.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return requiredProperty
            .EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.String)
            .Select(static entry => entry.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static AgentUserInputPrompt? ToUserInputPrompt(
        string propertyName,
        JsonElement schema,
        string message,
        ISet<string>? required)
    {
        var type = TryGetStringProperty(schema, "type");
        var header = schema.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String
            ? titleProperty.GetString()
            : null;
        var description = schema.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
            ? descriptionProperty.GetString()
            : null;

        var question = description ?? header ?? propertyName;
        if (!string.IsNullOrWhiteSpace(message))
        {
            question = $"{message}{Environment.NewLine}{question}";
        }

        return type switch
        {
            "boolean" => new AgentUserInputPrompt(
                propertyName,
                question,
                Header: header,
                Options:
                [
                    new AgentUserInputOption("true"),
                    new AgentUserInputOption("false"),
                ],
                AllowFreeform: false),
            "string" => new AgentUserInputPrompt(
                propertyName,
                question,
                Header: header,
                Options: GetStringOptions(schema),
                AllowFreeform: GetStringOptions(schema) is null,
                IsSecret: false),
            "integer" or "number" => new AgentUserInputPrompt(
                propertyName,
                required is not null && required.Contains(propertyName)
                    ? $"{question}{Environment.NewLine}A value is required."
                    : question,
                Header: header,
                AllowFreeform: true),
            "array" => new AgentUserInputPrompt(
                propertyName,
                $"{question}{Environment.NewLine}Provide a comma-separated list.",
                Header: header,
                Options: GetMultiSelectOptions(schema),
                AllowFreeform: true),
            _ => null,
        };
    }

    private static IReadOnlyList<AgentUserInputOption>? GetStringOptions(JsonElement schema)
    {
        if (schema.TryGetProperty("oneOf", out var oneOf) &&
            oneOf.ValueKind == JsonValueKind.Array)
        {
            var options = new List<AgentUserInputOption>();
            foreach (var entry in oneOf.EnumerateArray())
            {
                var label = entry.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String
                    ? titleProperty.GetString()
                    : null;
                var value = entry.TryGetProperty("const", out var valueProperty) && valueProperty.ValueKind == JsonValueKind.String
                    ? valueProperty.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                options.Add(new AgentUserInputOption(value!, label));
            }

            return options.Count == 0 ? null : options;
        }

        if (!schema.TryGetProperty("enum", out var enumValues) ||
            enumValues.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var enumOptions = enumValues
            .EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.String)
            .Select(static entry => new AgentUserInputOption(entry.GetString()!))
            .ToArray();
        return enumOptions.Length == 0 ? null : enumOptions;
    }

    private static IReadOnlyList<AgentUserInputOption>? GetMultiSelectOptions(JsonElement schema)
    {
        if (!schema.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (items.TryGetProperty("oneOf", out var oneOf) &&
            oneOf.ValueKind == JsonValueKind.Array)
        {
            var options = new List<AgentUserInputOption>();
            foreach (var entry in oneOf.EnumerateArray())
            {
                var label = entry.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String
                    ? titleProperty.GetString()
                    : null;
                var value = entry.TryGetProperty("const", out var valueProperty) && valueProperty.ValueKind == JsonValueKind.String
                    ? valueProperty.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                options.Add(new AgentUserInputOption(value!, label));
            }

            return options.Count == 0 ? null : options;
        }

        if (!items.TryGetProperty("enum", out var enumValues) ||
            enumValues.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var enumOptions = enumValues
            .EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.String)
            .Select(static entry => new AgentUserInputOption(entry.GetString()!))
            .ToArray();
        return enumOptions.Length == 0 ? null : enumOptions;
    }

    private static JsonElement ConvertUserInputValue(string propertyName, string value, JsonElement schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        return TryGetStringProperty(schema, "type") switch
        {
            "boolean" => CreateBoolElement(ParseBoolean(value, propertyName)),
            "integer" => CreateInt64Element(ParseInt64(value, propertyName)),
            "number" => CreateNumberElement(ParseDouble(value, propertyName)),
            "array" => CreateStringArrayElement(
                value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            _ => CreateStringElement(value),
        };
    }

    private static bool ParseBoolean(string value, string propertyName)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "y" or "1" => true,
            "no" or "n" or "0" => false,
            _ => throw new InvalidOperationException(
                $"The elicitation response for '{propertyName}' must be a boolean value."),
        };
    }

    private static long ParseInt64(string value, string propertyName)
    {
        if (long.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"The elicitation response for '{propertyName}' must be an integer value.");
    }

    private static double ParseDouble(string value, string propertyName)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"The elicitation response for '{propertyName}' must be a numeric value.");
    }

    private static bool IsNullOrEmptyObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return schema.ValueKind == JsonValueKind.Object &&
            string.Equals(TryGetStringProperty(schema, "type"), "object", StringComparison.Ordinal) &&
            schema.TryGetProperty("properties", out var propertiesProperty) &&
            propertiesProperty.ValueKind == JsonValueKind.Object &&
            !propertiesProperty.EnumerateObject().Any();
    }

    private static string? TryGetMetaString(JsonElement? meta, string propertyName)
    {
        if (meta is not { ValueKind: JsonValueKind.Object } metaValue ||
            !metaValue.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static bool MetaContainsValue(JsonElement? meta, string propertyName, string expectedValue)
    {
        if (meta is not { ValueKind: JsonValueKind.Object } metaValue ||
            !metaValue.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal),
            JsonValueKind.Array => property
                .EnumerateArray()
                .Any(value => value.ValueKind == JsonValueKind.String &&
                              string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal)),
            _ => false
        };
    }

    private static JsonElement CreateMcpToolCallDetails(ThreadItem.McpToolCallThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("tool", item.Tool);
            writer.WriteString("server", item.Server);
            writer.WriteString("status", item.Status.ToString());
            if (item.DurationMs is not null)
                writer.WriteNumber("durationMs", item.DurationMs.Value);
            writer.WritePropertyName("arguments");
            item.Arguments.WriteTo(writer);
            if (item.Error?.Message is not null)
                writer.WriteString("error", item.Error.Message);
            if (item.Result is { } result)
            {
                writer.WritePropertyName("result");
                WriteMcpToolCallResult(writer, result);
            }

            if (TryGetMcpToolCallOutput(item.Result) is { } output)
            {
                writer.WritePropertyName("output");
                writer.WriteStartObject();
                writer.WriteString("body", output);
                writer.WriteEndObject();
            }
        });
    }

    private static JsonElement CreateDynamicToolCallDetails(ThreadItem.DynamicToolCallThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("tool", item.Tool);
            writer.WriteString("status", item.Status.ToString());
            if (item.Success is not null)
                writer.WriteBoolean("success", item.Success.Value);
            if (item.DurationMs is not null)
                writer.WriteNumber("durationMs", item.DurationMs.Value);
            writer.WritePropertyName("arguments");
            item.Arguments.WriteTo(writer);
        });
    }

    private static JsonElement CreateCollabAgentToolCallDetails(ThreadItem.CollabAgentToolCallThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("tool", item.Tool.ToString());
            writer.WriteString("status", item.Status.ToString());
            writer.WriteString("senderThreadId", item.SenderThreadId);
            writer.WritePropertyName("receiverThreadIds");
            writer.WriteStartArray();
            foreach (var receiverThreadId in item.ReceiverThreadIds)
            {
                writer.WriteStringValue(receiverThreadId);
            }

            writer.WriteEndArray();
        });
    }

    private static JsonElement CreateWebSearchDetails(ThreadItem.WebSearchThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("query", item.Query);
            if (item.Action is not null)
            {
                writer.WriteString("action", DescribeWebSearchAction(item.Action, item.Query));
            }
        });
    }

    private static JsonElement CreateImageGenerationDetails(ThreadItem.ImageGenerationThreadItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("status", item.Status);
            if (item.RevisedPrompt is not null)
                writer.WriteString("revisedPrompt", item.RevisedPrompt);
            if (!string.IsNullOrWhiteSpace(item.Result))
                writer.WriteString("result", item.Result);
        });
    }

    private static AgentRunId? CreateRunId(string? turnId)
        => string.IsNullOrWhiteSpace(turnId) ? null : new AgentRunId(turnId);

    private static string ResolveCommandText(IReadOnlyList<CommandAction>? commandActions, string? rawCommand)
    {
        if (commandActions is { Count: > 0 })
        {
            var parsedText = commandActions
                .Select(GetCommandActionText)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (parsedText.Length > 0)
            {
                return string.Join(" ; ", parsedText);
            }
        }

        return rawCommand ?? string.Empty;
    }

    private static string GetCommandActionText(CommandAction commandAction)
    {
        ArgumentNullException.ThrowIfNull(commandAction);

        return commandAction switch
        {
            CommandAction.ReadCommandAction read => read.Command,
            CommandAction.ListFilesCommandAction listFiles => listFiles.Command,
            CommandAction.SearchCommandAction search => search.Command,
            CommandAction.UnknownCommandAction unknown => unknown.Command,
            _ => commandAction.ToString() ?? string.Empty
        };
    }

    private static void WriteCommandActions(Utf8JsonWriter writer, IReadOnlyList<CommandAction>? commandActions)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (commandActions is not { Count: > 0 })
        {
            return;
        }

        writer.WritePropertyName("commandActions");
        JsonSerializer.Serialize(writer, commandActions, CodexJsonSerializerContext.Default.ListCodeAltaCodexSdkCommandAction);
    }

    private static string? TryGetMcpToolCallContentText(IReadOnlyList<JsonElement>? content)
    {
        if (content is not { Count: > 0 })
        {
            return null;
        }

        var parts = new List<string>(content.Count);
        foreach (var item in content)
        {
            if (item.ValueKind == JsonValueKind.Object &&
                TryGetStringProperty(item, "text") is { } text &&
                !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
                continue;
            }

            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }

                continue;
            }

            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parts.Add(item.GetRawText());
            }
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    private static string? GetMcpToolCallMessage(ThreadItem.McpToolCallThreadItem item)
        => item.Error?.Message ?? TryGetMcpToolCallOutput(item.Result);

    private static string? TryGetMcpToolCallOutput(McpToolCallResult? result)
        => result is null ? null : TryGetMcpToolCallContentText(result.Content);

    private static void WriteMcpToolCallResult(Utf8JsonWriter writer, McpToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        JsonSerializer.Serialize(writer, result, CodexJsonSerializerContext.Default.McpToolCallResult);
    }

    private static JsonElement CreateLocalShellCallDetails(ResponseItem.LocalShellCallResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("status", item.Status.ToString());
            if (item.CallId is not null)
                writer.WriteString("callId", item.CallId);
            if (item.Id is not null)
                writer.WriteString("id", item.Id);
            if (TryResolveLocalShellCommandText(item.Action, out var command))
                writer.WriteString("command", command);
            writer.WritePropertyName("action");
            JsonSerializer.Serialize(writer, item.Action);
        });
    }

    private static JsonElement CreateFunctionCallDetails(ResponseItem.FunctionCallResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("name", item.Name);
            writer.WriteString("callId", item.CallId);
            writer.WritePropertyName("arguments");
            WriteStringifiedJsonValue(writer, item.Arguments);
        });
    }

    private static JsonElement CreateFunctionCallOutputDetails(ResponseItem.FunctionCallOutputResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("callId", item.CallId);
            writer.WritePropertyName("output");
            WriteFunctionCallOutputPayload(writer, item.Output);
        });
    }

    private static JsonElement CreateCustomToolCallDetails(ResponseItem.CustomToolCallResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("name", item.Name);
            writer.WriteString("callId", item.CallId);
            writer.WritePropertyName("input");
            WriteStringifiedJsonValue(writer, item.Input);
            if (item.Status is not null)
                writer.WriteString("status", item.Status);
        });
    }

    private static JsonElement CreateCustomToolCallOutputDetails(ResponseItem.CustomToolCallOutputResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("callId", item.CallId);
            if (item.Name is not null)
                writer.WriteString("name", item.Name);
            writer.WritePropertyName("output");
            WriteFunctionCallOutputPayload(writer, item.Output);
        });
    }

    private static JsonElement CreateWebSearchCallDetails(ResponseItem.WebSearchCallResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            if (item.Id is not null)
                writer.WriteString("id", item.Id);
            if (item.Status is not null)
                writer.WriteString("status", item.Status);
            if (item.Action is not null)
            {
                writer.WriteString("action", DescribeWebSearchAction(item.Action, fallbackQuery: null));
            }
        });
    }

    private static JsonElement CreateImageGenerationCallDetails(ResponseItem.ImageGenerationCallResponseItem item)
    {
        return CreateObjectElement(writer =>
        {
            writer.WriteString("status", item.Status);
            if (item.RevisedPrompt is not null)
                writer.WriteString("revisedPrompt", item.RevisedPrompt);
            if (!string.IsNullOrWhiteSpace(item.Result))
                writer.WriteString("result", item.Result);
        });
    }

    private static string DescribeLocalShellAction(LocalShellAction action)
    {
        if (TryResolveLocalShellCommandText(action, out var command))
        {
            return command!;
        }

        if (!string.IsNullOrWhiteSpace(action.Type))
        {
            return action.Type;
        }

        return JsonSerializer.Serialize(action);
    }

    private static bool TryResolveLocalShellCommandText(LocalShellAction action, out string? command)
    {
        command = null;

        if (action.Command.Count > 0)
        {
            command = string.Join(" ", action.Command);
            return true;
        }

        return false;
    }

    private static bool TryDeserializeCommandAction(JsonElement element, out CommandAction commandAction)
    {
        try
        {
            commandAction = JsonSerializer.Deserialize(element, CodexJsonSerializerContext.Default.CommandAction)!;
            return commandAction is not null;
        }
        catch (JsonException)
        {
            commandAction = null!;
            return false;
        }
        catch (NotSupportedException)
        {
            commandAction = null!;
            return false;
        }
    }

    private static string DescribeWebSearchAction(WebSearchAction? action, string? fallbackQuery)
    {
        return action switch
        {
            WebSearchAction.SearchWebSearchAction search => search.Query
                ?? search.Queries?.FirstOrDefault()
                ?? fallbackQuery
                ?? "web search",
            WebSearchAction.OpenPageWebSearchAction openPage => openPage.Url ?? "open page",
            WebSearchAction.FindInPageWebSearchAction findInPage => findInPage.Pattern ?? findInPage.Url ?? "find in page",
            WebSearchAction.OtherWebSearchAction => fallbackQuery ?? "web search",
            null => fallbackQuery ?? "web search",
            _ => fallbackQuery ?? "web search"
        };
    }

    private static string ExtractUserMessageText(ThreadItem.UserMessageThreadItem userMessage)
    {
        return string.Join(
            Environment.NewLine,
            userMessage.Content.Select(
                static content => content switch
                {
                    UserInput.TextUserInput text => text.Text,
                    UserInput.ImageUserInput => "Inline Image",
                    UserInput.LocalImageUserInput => "Inline Image",
                    UserInput.SkillUserInput skill => skill.Name,
                    UserInput.MentionUserInput mention => mention.Name,
                    _ => string.Empty
                }).Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ExtractReasoningThreadText(ThreadItem.ReasoningThreadItem reasoning)
    {
        return reasoning.Content is { Count: > 0 } content
            ? string.Join(Environment.NewLine, content)
            : reasoning.Summary is { Count: > 0 } summary
                ? string.Join(Environment.NewLine, summary)
                : string.Empty;
    }

    private static string ExtractMessageResponseText(ResponseItem.MessageResponseItem message)
    {
        return string.Join(
            Environment.NewLine,
            message.Content.Select(
                static content => content switch
                {
                    ContentItem.OutputTextContentItem text => text.Text,
                    ContentItem.InputTextContentItem input => input.Text,
                    ContentItem.InputImageContentItem => "Inline Image",
                    _ => string.Empty
                }).Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ExtractReasoningResponseText(ResponseItem.ReasoningResponseItem reasoning)
    {
        if (reasoning.Content is { Count: > 0 } content)
        {
            var text = string.Join(
                Environment.NewLine,
                content.Select(
                    static item => item switch
                    {
                        ReasoningItemContent.ReasoningTextReasoningItemContent reasoningText => reasoningText.Text,
                        ReasoningItemContent.TextReasoningItemContent textContent => textContent.Text,
                        _ => string.Empty
                    }).Where(static value => !string.IsNullOrWhiteSpace(value)));
            if (text.Length > 0)
            {
                return text;
            }
        }

        if (reasoning.Summary.Count > 0)
        {
            return string.Join(
                Environment.NewLine,
                reasoning.Summary
                    .Select(static item => item.Text)
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        return !string.IsNullOrWhiteSpace(reasoning.EncryptedContent)
            ? "_Reasoning content is encrypted and unavailable in this stream._"
            : string.Empty;
    }

    internal static AgentContentKind ToAgentContentKind(MessagePhase? phase)
    {
        return phase switch
        {
            MessagePhase.Commentary => AgentContentKind.Reasoning,
            MessagePhase.FinalAnswer => AgentContentKind.Assistant,
            _ => AgentContentKind.Assistant,
        };
    }

    private static string DescribeWebSearchAction(ResponsesApiWebSearchAction? action, string? fallbackQuery)
    {
        return action switch
        {
            ResponsesApiWebSearchAction.SearchResponsesApiWebSearchAction search => search.Query
                ?? search.Queries?.FirstOrDefault()
                ?? fallbackQuery
                ?? "web search",
            ResponsesApiWebSearchAction.OpenPageResponsesApiWebSearchAction openPage => openPage.Url ?? "open page",
            ResponsesApiWebSearchAction.FindInPageResponsesApiWebSearchAction findInPage => findInPage.Pattern ?? findInPage.Url ?? "find in page",
            ResponsesApiWebSearchAction.OtherResponsesApiWebSearchAction => fallbackQuery ?? "web search",
            null => fallbackQuery ?? "web search",
            _ => fallbackQuery ?? "web search"
        };
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static GrantedPermissionProfile ToGrantedPermissionProfile(RequestPermissionProfile requestedPermissions)
    {
        ArgumentNullException.ThrowIfNull(requestedPermissions);

        return new GrantedPermissionProfile
        {
            FileSystem = requestedPermissions.FileSystem is null
                ? null
                : new AdditionalFileSystemPermissions
                {
                    Read = requestedPermissions.FileSystem.Read is null ? null : [.. requestedPermissions.FileSystem.Read],
                    Write = requestedPermissions.FileSystem.Write is null ? null : [.. requestedPermissions.FileSystem.Write]
                },
            Network = requestedPermissions.Network is null
                ? null
                : new AdditionalNetworkPermissions
                {
                    Enabled = requestedPermissions.Network.Enabled
                }
        };
    }

    private static JsonElement CreateObjectElement(Action<Utf8JsonWriter> writeProperties)
    {
        ArgumentNullException.ThrowIfNull(writeProperties);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteStringifiedJsonValue(Utf8JsonWriter writer, string? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteStringValue(value);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteFunctionCallOutputPayload(Utf8JsonWriter writer, FunctionCallOutputBody payload)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(payload);

        writer.WriteStartObject();
        writer.WritePropertyName("body");
        switch (payload)
        {
            case FunctionCallOutputBody.StringValue stringValue:
                writer.WriteStringValue(stringValue.Value);
                break;
            case FunctionCallOutputBody.ArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Value)
                {
                    switch (item)
                    {
                        case FunctionCallOutputContentItem.InputTextFunctionCallOutputContentItem text:
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_text");
                            writer.WriteString("text", text.Text);
                            writer.WriteEndObject();
                            break;
                        case FunctionCallOutputContentItem.InputImageFunctionCallOutputContentItem image:
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_image");
                            writer.WriteString("image_url", image.ImageUrl);
                            if (image.Detail is not null)
                                writer.WriteString("detail", image.Detail.ToString());
                            writer.WriteEndObject();
                            break;
                    }
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteNullValue();
                break;
        }

        writer.WriteEndObject();
    }

    private static string? TryGetFunctionCallOutputText(FunctionCallOutputBody payload)
    {
        return payload switch
        {
            FunctionCallOutputBody.StringValue stringValue when !string.IsNullOrWhiteSpace(stringValue.Value) => stringValue.Value,
            FunctionCallOutputBody.ArrayValue arrayValue => string.Join(
                Environment.NewLine,
                arrayValue.Value.Select(
                    static item => item switch
                    {
                        FunctionCallOutputContentItem.InputTextFunctionCallOutputContentItem text => text.Text,
                        FunctionCallOutputContentItem.InputImageFunctionCallOutputContentItem => "Inline Image",
                        _ => string.Empty
                    }).Where(static value => !string.IsNullOrWhiteSpace(value))),
            _ => null
        };
    }

    private static AgentCommandPreviewAction ToAgentCommandPreviewAction(CommandAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action switch
        {
            CommandAction.ReadCommandAction read => new AgentCommandPreviewAction(
                AgentCommandPreviewKind.Read,
                read.Command,
                Path: read.Path,
                Name: read.Name),
            CommandAction.ListFilesCommandAction listFiles => new AgentCommandPreviewAction(
                AgentCommandPreviewKind.ListFiles,
                listFiles.Command,
                Path: listFiles.Path),
            CommandAction.SearchCommandAction search => new AgentCommandPreviewAction(
                AgentCommandPreviewKind.Search,
                search.Command,
                Path: search.Path,
                Query: search.Query),
            CommandAction.UnknownCommandAction unknown => new AgentCommandPreviewAction(
                AgentCommandPreviewKind.Unknown,
                unknown.Command),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported command action.")
        };
    }

    private static AgentNetworkPolicyAmendment ToAgentNetworkPolicyAmendment(NetworkPolicyAmendment amendment)
    {
        ArgumentNullException.ThrowIfNull(amendment);

        return new AgentNetworkPolicyAmendment(
            amendment.Action switch
            {
                NetworkPolicyRuleAction.Allow => AgentNetworkPolicyAction.Allow,
                NetworkPolicyRuleAction.Deny => AgentNetworkPolicyAction.Deny,
                _ => throw new ArgumentOutOfRangeException(nameof(amendment), amendment.Action, "Unsupported network policy action.")
            },
            amendment.Host);
    }

    private static NetworkPolicyAmendment ToNetworkPolicyAmendment(AgentNetworkPolicyAmendment amendment)
    {
        ArgumentNullException.ThrowIfNull(amendment);

        return new NetworkPolicyAmendment
        {
            Action = amendment.Action switch
            {
                AgentNetworkPolicyAction.Allow => NetworkPolicyRuleAction.Allow,
                AgentNetworkPolicyAction.Deny => NetworkPolicyRuleAction.Deny,
                _ => throw new ArgumentOutOfRangeException(nameof(amendment), amendment.Action, "Unsupported network policy action.")
            },
            Host = amendment.Host
        };
    }

    private static AgentReasoningEffort ToAgentReasoningEffort(V2ReasoningEffort reasoningEffort)
    {
        return reasoningEffort switch
        {
            V2ReasoningEffort.None => AgentReasoningEffort.None,
            V2ReasoningEffort.Minimal => AgentReasoningEffort.Minimal,
            V2ReasoningEffort.Low => AgentReasoningEffort.Low,
            V2ReasoningEffort.Medium => AgentReasoningEffort.Medium,
            V2ReasoningEffort.High => AgentReasoningEffort.High,
            V2ReasoningEffort.Xhigh => AgentReasoningEffort.XHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(reasoningEffort), reasoningEffort, "Unsupported reasoning effort.")
        };
    }

    private static Dictionary<string, JsonElement>? CreateThreadConfig(
        AgentReasoningEffort? reasoningEffort,
        IReadOnlyDictionary<string, AgentMcpServerConfig>? mcpServers)
    {
        Dictionary<string, JsonElement>? config = null;

        var mappedReasoningEffort = ToCodexReasoningEffort(reasoningEffort);
        if (mappedReasoningEffort is not null)
        {
            config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model_reasoning_effort"] = CreateStringElement(
                    mappedReasoningEffort.Value.ToString().ToLowerInvariant())
            };
        }

        if (mcpServers is not { Count: > 0 })
        {
            return config;
        }

        config ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in mcpServers)
        {
            AddCodexMcpServerConfig(config, pair.Key, pair.Value);
        }

        return config;
    }

    private static void AddCodexMcpServerConfig(
        IDictionary<string, JsonElement> config,
        string serverName,
        AgentMcpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(server);

        var prefix = $"mcp_servers.{serverName}.";

        config[prefix + "enabled"] = CreateBoolElement(server.Enabled);
        config[prefix + "required"] = CreateBoolElement(server.Required);
        if (server.ToolTimeout is { } toolTimeout)
        {
            config[prefix + "tool_timeout_sec"] = CreateNumberElement(toolTimeout.TotalSeconds);
        }

        if (server.EnabledTools is { Count: > 0 } enabledTools)
        {
            config[prefix + "enabled_tools"] = CreateStringArrayElement(enabledTools);
        }

        switch (server)
        {
            case AgentLocalMcpServerConfig local:
                config[prefix + "command"] = CreateStringElement(local.Command);
                if (local.Arguments is { Count: > 0 } arguments)
                {
                    config[prefix + "args"] = CreateStringArrayElement(arguments);
                }

                if (local.EnvironmentVariables is { Count: > 0 } environmentVariables)
                {
                    config[prefix + "env"] = CreateStringDictionaryElement(environmentVariables);
                }

                if (!string.IsNullOrWhiteSpace(local.WorkingDirectory))
                {
                    config[prefix + "cwd"] = CreateStringElement(local.WorkingDirectory);
                }

                break;

            case AgentRemoteMcpServerConfig remote:
                if (remote.Transport == AgentMcpRemoteTransport.Sse)
                {
                    throw new NotSupportedException("Codex MCP configuration currently supports streamable HTTP servers, not SSE.");
                }

                config[prefix + "url"] = CreateStringElement(remote.Url);
                if (remote.Headers is { Count: > 0 } headers)
                {
                    config[prefix + "http_headers"] = CreateStringDictionaryElement(headers);
                }

                if (!string.IsNullOrWhiteSpace(remote.BearerTokenEnvironmentVariable))
                {
                    config[prefix + "bearer_token_env_var"] = CreateStringElement(remote.BearerTokenEnvironmentVariable);
                }

                if (remote.EnvironmentHeaders is { Count: > 0 } environmentHeaders)
                {
                    config[prefix + "env_http_headers"] = CreateStringDictionaryElement(environmentHeaders);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(server), server, "Unsupported MCP server config.");
        }
    }

    private static V2ReasoningEffort? ToCodexReasoningEffort(AgentReasoningEffort? reasoningEffort)
    {
        return reasoningEffort switch
        {
            null => null,
            AgentReasoningEffort.None => V2ReasoningEffort.None,
            AgentReasoningEffort.Minimal => V2ReasoningEffort.Minimal,
            AgentReasoningEffort.Low => V2ReasoningEffort.Low,
            AgentReasoningEffort.Medium => V2ReasoningEffort.Medium,
            AgentReasoningEffort.High => V2ReasoningEffort.High,
            AgentReasoningEffort.XHigh => V2ReasoningEffort.Xhigh,
            _ => throw new ArgumentOutOfRangeException(nameof(reasoningEffort), reasoningEffort, "Unsupported reasoning effort."),
        };
    }

    private static SandboxPolicy? CreateSandboxPolicy(SandboxMode? sandboxMode, string? workingDirectory)
    {
        return sandboxMode switch
        {
            null => null,
            SandboxMode.DangerFullAccess => new SandboxPolicy.DangerFullAccessSandboxPolicy(),
            SandboxMode.ReadOnly => new SandboxPolicy.ReadOnlySandboxPolicy
            {
                Access = new ReadOnlyAccess.FullAccessReadOnlyAccess()
            },
            SandboxMode.WorkspaceWrite => new SandboxPolicy.WorkspaceWriteSandboxPolicy
            {
                ReadOnlyAccess = new ReadOnlyAccess.FullAccessReadOnlyAccess(),
                WritableRoots = string.IsNullOrWhiteSpace(workingDirectory)
                    ? null
                    : [workingDirectory]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(sandboxMode), sandboxMode, "Unsupported sandbox mode."),
        };
    }

    private static JsonElement CreateStringElement(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(value);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateNullElement()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteNullValue();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateBoolElement(bool value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteBooleanValue(value);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateInt64Element(long value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteNumberValue(value);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateNumberElement(double value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteNumberValue(value);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateStringArrayElement(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateStringDictionaryElement(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in values)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
