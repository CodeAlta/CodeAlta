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
        var supportedReasoningEfforts = model.SupportedReasoningEfforts
            .Select(x => ToAgentReasoningEffort(x.ReasoningEffort))
            .ToArray();

        return new AgentModelInfo(
            model.Id,
            DisplayName: model.DisplayName,
            Description: model.Description,
            Provider: null,
            DefaultReasoningEffort: ToAgentReasoningEffort(model.DefaultReasoningEffort),
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
            DeveloperInstructions = options.DeveloperInstructions,
            Cwd = options.WorkingDirectory,
            Model = options.Model
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

        var questions = parameters.Questions.Select(question =>
        {
            var choices = question.Options?.Select(option => option.Label).ToArray();
            var allowFreeform = question.IsOther.GetValueOrDefault(true);
            return new AgentUserInputQuestion(question.Id, question.Question, choices, allowFreeform);
        }).ToArray();

        return new AgentUserInputRequest(
            AgentBackendIds.Codex,
            parameters.ThreadId,
            questions);
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
            CodexNotification.AgentMessageDelta delta => new AgentAssistantMessageDeltaEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(delta.Data.TurnId),
                delta.Data.Delta),

            CodexNotification.ItemCompleted itemCompleted when itemCompleted.Data.Item is ThreadItem.AgentMessageThreadItem message => new AgentAssistantMessageEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp,
                new AgentRunId(itemCompleted.Data.TurnId),
                message.Text),

            CodexNotification.TurnCompleted => new AgentSessionIdleEvent(
                AgentBackendIds.Codex,
                sessionId,
                timestamp),

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
                if (item is ThreadItem.AgentMessageThreadItem message)
                {
                    events.Add(new AgentAssistantMessageEvent(
                        AgentBackendIds.Codex,
                        sessionId,
                        timestamp,
                        runId,
                        message.Text));
                }
            }

            if (turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Interrupted)
            {
                events.Add(new AgentSessionIdleEvent(
                    AgentBackendIds.Codex,
                    sessionId,
                    timestamp));
            }
        }

        return events;
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

    public static bool TryGetThreadId(CodexServerRequest request, out string? threadId)
    {
        ArgumentNullException.ThrowIfNull(request);

        threadId = request switch
        {
            CodexServerRequest.CommandExecutionApproval value => value.Data.ThreadId,
            CodexServerRequest.FileChangeApproval value => value.Data.ThreadId,
            CodexServerRequest.ToolRequestUserInput value => value.Data.ThreadId,
            CodexServerRequest.ToolCall value => value.Data.ThreadId,
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

        return new AgentPermissionRequest(
            AgentBackendIds.Codex,
            sessionId,
            Kind: "commandExecution",
            Raw: CreateRawElementFromCommand(data));
    }

    public static AgentPermissionRequest ToPermissionRequest(
        string sessionId,
        FileChangeRequestApprovalParams data)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(data);

        return new AgentPermissionRequest(
            AgentBackendIds.Codex,
            sessionId,
            Kind: "fileChange",
            Raw: CreateRawElementFromFileChange(data));
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
}
