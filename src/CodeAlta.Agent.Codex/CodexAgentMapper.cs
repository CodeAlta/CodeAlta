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
            WorkspacePath: thread.Path);
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

    public static ThreadStartParams ToThreadStartParams(AgentSessionCreateOptions options, V2AskForApproval approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ThreadStartParams
        {
            ApprovalPolicy = approvalPolicy,
            BaseInstructions = options.SystemMessage,
            Config = CreateThreadConfig(options.ReasoningEffort),
            DeveloperInstructions = options.DeveloperInstructions,
            Cwd = options.WorkingDirectory,
            Model = options.Model
        };
    }

    public static ThreadResumeParams ToThreadResumeParams(
        string threadId,
        AgentSessionResumeOptions options,
        V2AskForApproval approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(options);

        return new ThreadResumeParams
        {
            ThreadId = threadId,
            ApprovalPolicy = approvalPolicy,
            BaseInstructions = options.SystemMessage,
            Config = CreateThreadConfig(options.ReasoningEffort),
            DeveloperInstructions = options.DeveloperInstructions,
            Cwd = options.WorkingDirectory,
            Model = options.Model
        };
    }

    public static TurnStartParams ToTurnStartParams(
        string threadId,
        AgentInput input,
        string? workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort)
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
                $"Token usage updated for turn {tokenUsage.Data.TurnId}.",
                new AgentRunId(tokenUsage.Data.TurnId)),

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
        AgentRunId? runId = null)
    {
        return new AgentSessionUpdateEvent(
            AgentBackendIds.Codex,
            sessionId,
            timestamp,
            runId,
            kind,
            message);
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
        string? message)
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
            message);
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
                commandExecution.Command,
                commandExecution.Cwd),

            ThreadItem.FileChangeThreadItem fileChange => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.FileChange,
                AgentActivityPhase.Started,
                fileChange.Id,
                notification.TurnId,
                "file change",
                null),

            ThreadItem.McpToolCallThreadItem mcpToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.McpToolCall,
                AgentActivityPhase.Started,
                mcpToolCall.Id,
                notification.TurnId,
                mcpToolCall.Tool,
                mcpToolCall.Server),

            ThreadItem.DynamicToolCallThreadItem dynamicToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.DynamicToolCall,
                AgentActivityPhase.Started,
                dynamicToolCall.Id,
                notification.TurnId,
                dynamicToolCall.Tool,
                null),

            ThreadItem.CollabAgentToolCallThreadItem collabAgentToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CollabAgentToolCall,
                AgentActivityPhase.Started,
                collabAgentToolCall.Id,
                notification.TurnId,
                collabAgentToolCall.Tool.ToString(),
                collabAgentToolCall.Prompt),

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
            ThreadItem.AgentMessageThreadItem message => new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                runId,
                AgentContentKind.Assistant,
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
                reasoning.Content is { Count: > 0 } content
                    ? string.Join(Environment.NewLine, content)
                    : string.Join(Environment.NewLine, reasoning.Summary ?? [])),

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
                commandExecution.Command,
                commandExecution.AggregatedOutput),

            ThreadItem.FileChangeThreadItem fileChange => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.FileChange,
                ToActivityPhase(fileChange.Status),
                fileChange.Id,
                notification.TurnId,
                "file change",
                fileChange.Changes.Count == 0 ? null : $"{fileChange.Changes.Count} change(s)"),

            ThreadItem.McpToolCallThreadItem mcpToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.McpToolCall,
                ToActivityPhase(mcpToolCall.Status),
                mcpToolCall.Id,
                notification.TurnId,
                mcpToolCall.Tool,
                mcpToolCall.Error?.Message),

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
                    : null),

            ThreadItem.CollabAgentToolCallThreadItem collabAgentToolCall => CreateActivity(
                sessionId,
                timestamp,
                runId,
                AgentActivityKind.CollabAgentToolCall,
                ToActivityPhase(collabAgentToolCall.Status),
                collabAgentToolCall.Id,
                notification.TurnId,
                collabAgentToolCall.Tool.ToString(),
                collabAgentToolCall.Prompt),

            _ => new AgentRawEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                nameof(CodexNotification.ItemCompleted),
                SerializeNotification(new CodexNotification.ItemCompleted(notification)),
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
            ServerRequest.ItemToolCallRequest value => value.Params.ThreadId,
            _ => null
        };

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

    private static Dictionary<string, JsonElement>? CreateThreadConfig(AgentReasoningEffort? reasoningEffort)
    {
        var mappedReasoningEffort = ToCodexReasoningEffort(reasoningEffort);
        if (mappedReasoningEffort is null)
        {
            return null;
        }

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["model_reasoning_effort"] = CreateStringElement(
                mappedReasoningEffort.Value.ToString().ToLowerInvariant())
        };
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
}
