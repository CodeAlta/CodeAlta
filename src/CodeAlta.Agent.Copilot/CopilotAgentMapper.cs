using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Copilot;

internal static class CopilotAgentMapper
{
    private static readonly Logger CallbackLogger = LogManager.GetLogger("CodeAlta.Agent.Copilot.Callbacks");

    public static AgentModelInfo ToAgentModelInfo(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var capabilities = new Dictionary<string, object?>
        {
            ["capabilities"] = model.Capabilities,
            ["policy"] = model.Policy,
            ["billing"] = model.Billing,
            ["supportedReasoningEfforts"] = model.SupportedReasoningEfforts,
            ["defaultReasoningEffort"] = model.DefaultReasoningEffort
        };
        var supportedReasoningEfforts = ToAgentReasoningEfforts(model.SupportedReasoningEfforts);

        return new AgentModelInfo(
            model.Id,
            DisplayName: model.Name,
            Description: null,
            Provider: null,
            DefaultReasoningEffort: ToAgentReasoningEffort(model.DefaultReasoningEffort),
            SupportedReasoningEfforts: supportedReasoningEfforts,
            Capabilities: capabilities);
    }

    public static AgentSessionMetadata ToAgentSessionMetadata(SessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var context = metadata.Context is null
            ? null
            : new AgentSessionContext(
                metadata.Context.Cwd,
                metadata.Context.GitRoot,
                metadata.Context.Repository,
                metadata.Context.Branch);

        return new AgentSessionMetadata(
            SessionId: metadata.SessionId,
            CreatedAt: new DateTimeOffset(metadata.StartTime),
            UpdatedAt: new DateTimeOffset(metadata.ModifiedTime),
            Summary: metadata.Summary,
            Context: context,
            WorkspacePath: null);
    }

    public static SessionConfig ToSessionConfig(
        AgentSessionCreateOptions options,
        CopilotSessionCallbackBridge? callbackBridge = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        callbackBridge ??= new CopilotSessionCallbackBridge();
        var systemMessage = BuildSystemMessage(options.SystemMessage, options.DeveloperInstructions);
        var config = new SessionConfig
        {
            Model = options.Model,
            WorkingDirectory = options.WorkingDirectory,
            Streaming = options.Streaming,
            ReasoningEffort = ToCopilotReasoningEffort(options.ReasoningEffort),
            McpServers = ToCopilotMcpServers(options.McpServers),
            OnPermissionRequest = CreatePermissionHandler(options.OnPermissionRequest, callbackBridge),
            OnUserInputRequest = CreateUserInputHandler(options.OnUserInputRequest, callbackBridge),
            SystemMessage = systemMessage,
            Tools = ToCopilotTools(options.Tools)
        };

        return config;
    }

    public static ResumeSessionConfig ToResumeSessionConfig(
        AgentSessionResumeOptions options,
        CopilotSessionCallbackBridge? callbackBridge = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        callbackBridge ??= new CopilotSessionCallbackBridge();
        var systemMessage = BuildSystemMessage(options.SystemMessage, options.DeveloperInstructions);
        var config = new ResumeSessionConfig
        {
            Model = options.Model,
            WorkingDirectory = options.WorkingDirectory,
            Streaming = options.Streaming,
            ReasoningEffort = ToCopilotReasoningEffort(options.ReasoningEffort),
            McpServers = ToCopilotMcpServers(options.McpServers),
            OnPermissionRequest = CreatePermissionHandler(options.OnPermissionRequest, callbackBridge),
            OnUserInputRequest = CreateUserInputHandler(options.OnUserInputRequest, callbackBridge),
            SystemMessage = systemMessage,
            Tools = ToCopilotTools(options.Tools)
        };

        return config;
    }

    public static MessageOptions ToSendMessageOptions(AgentSendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ToMessageOptions(options.Input, "enqueue");
    }

    public static MessageOptions ToSteerMessageOptions(AgentSteerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ToMessageOptions(options.Input, "immediate");
    }

    private static MessageOptions ToMessageOptions(AgentInput input, string mode)
    {
        ArgumentNullException.ThrowIfNull(input);

        var attachments = new List<UserMessageDataAttachmentsItem>();
        var promptBuilder = new StringBuilder();

        foreach (var item in input.Items)
        {
            switch (item)
            {
                case AgentInputItem.Text text:
                    AppendPromptLine(promptBuilder, text.Value);
                    break;

                case AgentInputItem.File file:
                    attachments.Add(new UserMessageDataAttachmentsItemFile
                    {
                        Path = file.Path,
                        DisplayName = file.DisplayName ?? file.Path,
                        LineRange = file.LineRange is null
                            ? null
                            : new UserMessageDataAttachmentsItemFileLineRange
                            {
                                Start = file.LineRange.StartLine,
                                End = file.LineRange.EndLine
                            }
                    });
                    break;

                case AgentInputItem.Directory directory:
                    attachments.Add(new UserMessageDataAttachmentsItemDirectory
                    {
                        Path = directory.Path,
                        DisplayName = directory.DisplayName ?? directory.Path,
                        LineRange = directory.LineRange is null
                            ? null
                            : new UserMessageDataAttachmentsItemDirectoryLineRange
                            {
                                Start = directory.LineRange.StartLine,
                                End = directory.LineRange.EndLine
                            }
                    });
                    break;

                case AgentInputItem.Selection selection:
                    attachments.Add(new UserMessageDataAttachmentsItemSelection
                    {
                        FilePath = selection.FilePath,
                        DisplayName = selection.DisplayName,
                        Text = selection.SelectedText,
                        Selection = new UserMessageDataAttachmentsItemSelectionSelection
                        {
                            Start = new UserMessageDataAttachmentsItemSelectionSelectionStart
                            {
                                Line = selection.Range.Start.Line,
                                Character = selection.Range.Start.Character
                            },
                            End = new UserMessageDataAttachmentsItemSelectionSelectionEnd
                            {
                                Line = selection.Range.End.Line,
                                Character = selection.Range.End.Character
                            }
                        }
                    });
                    break;

                case AgentInputItem.ImageUrl imageUrl:
                    AppendPromptLine(promptBuilder, $"[image-url] {imageUrl.Url}");
                    break;

                case AgentInputItem.LocalImage localImage:
                    AppendPromptLine(promptBuilder, $"[local-image] {localImage.Path}");
                    break;

                case AgentInputItem.Skill skill:
                    AppendPromptLine(promptBuilder, $"[skill] name={skill.Name} path={skill.Path}");
                    break;

                case AgentInputItem.Mention mention:
                    AppendPromptLine(promptBuilder, $"[mention] name={mention.Name} path={mention.Path}");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item, "Unsupported input item.");
            }
        }

        return new MessageOptions
        {
            Prompt = promptBuilder.ToString(),
            Attachments = attachments.Count == 0 ? null : attachments,
            Mode = mode
        };
    }

    public static AgentEvent ToAgentEvent(string sessionId, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return sessionEvent switch
        {
            SessionStartEvent started => CreateSessionUpdate(
                sessionId,
                started.Timestamp,
                AgentSessionUpdateKind.Started,
                started.Data.SelectedModel is { Length: > 0 } selectedModel
                    ? $"Session started ({selectedModel})."
                    : "Session started."),

            SessionResumeEvent resumed => CreateSessionUpdate(
                sessionId,
                resumed.Timestamp,
                AgentSessionUpdateKind.Resumed,
                $"Session resumed ({resumed.Data.EventCount:0} events)."),

            SessionInfoEvent info => CreateSessionUpdate(
                sessionId,
                info.Timestamp,
                AgentSessionUpdateKind.Info,
                info.Data.Message),

            SessionWarningEvent warning => CreateSessionUpdate(
                sessionId,
                warning.Timestamp,
                AgentSessionUpdateKind.Warning,
                warning.Data.Message),

            SessionModelChangeEvent modelChange => CreateSessionUpdate(
                sessionId,
                modelChange.Timestamp,
                AgentSessionUpdateKind.ModelChanged,
                modelChange.Data.PreviousModel is { Length: > 0 } previousModel
                    ? $"{previousModel} → {modelChange.Data.NewModel}"
                    : modelChange.Data.NewModel),

            SessionModeChangedEvent modeChanged => CreateSessionUpdate(
                sessionId,
                modeChanged.Timestamp,
                AgentSessionUpdateKind.ModeChanged,
                $"{modeChanged.Data.PreviousMode} → {modeChanged.Data.NewMode}"),

            SessionTitleChangedEvent titleChanged => CreateSessionUpdate(
                sessionId,
                titleChanged.Timestamp,
                AgentSessionUpdateKind.TitleChanged,
                titleChanged.Data.Title),

            SessionContextChangedEvent contextChanged => CreateSessionUpdate(
                sessionId,
                contextChanged.Timestamp,
                AgentSessionUpdateKind.ContextChanged,
                contextChanged.Data.Cwd),

            SessionUsageInfoEvent usage => CreateSessionUpdate(
                sessionId,
                usage.Timestamp,
                AgentSessionUpdateKind.UsageUpdated,
                $"{usage.Data.CurrentTokens:0}/{usage.Data.TokenLimit:0} tokens"),

            SessionCompactionStartEvent compactionStart => CreateSessionUpdate(
                sessionId,
                compactionStart.Timestamp,
                AgentSessionUpdateKind.CompactionStarted,
                "Compaction started."),

            SessionCompactionCompleteEvent compactionComplete => CreateSessionUpdate(
                sessionId,
                compactionComplete.Timestamp,
                AgentSessionUpdateKind.CompactionCompleted,
                compactionComplete.Data.Success
                    ? "Compaction completed."
                    : compactionComplete.Data.Error ?? "Compaction failed."),

            SessionTaskCompleteEvent taskComplete => CreateSessionUpdate(
                sessionId,
                taskComplete.Timestamp,
                AgentSessionUpdateKind.TaskCompleted,
                taskComplete.Data.Summary),

            SessionPlanChangedEvent planChanged => new AgentPlanSnapshotEvent(
                AgentBackendIds.Copilot,
                sessionId,
                planChanged.Timestamp,
                null,
                new AgentPlanSnapshot(
                    ToPlanChangeKind(planChanged.Data.Operation),
                    Explanation: null,
                    Steps: null)),

            SessionHandoffEvent handoff => CreateSessionUpdate(
                sessionId,
                handoff.Timestamp,
                AgentSessionUpdateKind.Handoff,
                handoff.Data.Summary ?? handoff.Data.Context ?? "Session handoff."),

            SessionTruncationEvent truncation => CreateSessionUpdate(
                sessionId,
                truncation.Timestamp,
                AgentSessionUpdateKind.Truncated,
                $"{truncation.Data.MessagesRemovedDuringTruncation:0} messages removed."),

            SessionSnapshotRewindEvent rewind => CreateSessionUpdate(
                sessionId,
                rewind.Timestamp,
                AgentSessionUpdateKind.Truncated,
                $"{rewind.Data.EventsRemoved:0} events removed."),

            SessionShutdownEvent shutdown => CreateSessionUpdate(
                sessionId,
                shutdown.Timestamp,
                AgentSessionUpdateKind.Shutdown,
                shutdown.Data.ErrorReason ?? shutdown.Data.ShutdownType.ToString()),

            AssistantTurnStartEvent turnStart => CreateActivityEvent(
                sessionId,
                turnStart.Timestamp,
                null,
                AgentActivityKind.Turn,
                AgentActivityPhase.Started,
                turnStart.Data.TurnId,
                turnStart.Data.InteractionId,
                "assistant turn",
                null),

            AssistantTurnEndEvent turnEnd => CreateActivityEvent(
                sessionId,
                turnEnd.Timestamp,
                null,
                AgentActivityKind.Turn,
                AgentActivityPhase.Completed,
                turnEnd.Data.TurnId,
                null,
                "assistant turn",
                null),

            AssistantIntentEvent intent => CreateSessionUpdate(
                sessionId,
                intent.Timestamp,
                AgentSessionUpdateKind.Info,
                $"Intent: {intent.Data.Intent}"),

            AssistantReasoningDeltaEvent reasoningDelta => new AgentContentDeltaEvent(
                AgentBackendIds.Copilot,
                sessionId,
                reasoningDelta.Timestamp,
                null,
                AgentContentKind.Reasoning,
                reasoningDelta.Data.ReasoningId,
                null,
                reasoningDelta.Data.DeltaContent),

            AssistantReasoningEvent reasoning => new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                sessionId,
                reasoning.Timestamp,
                null,
                AgentContentKind.Reasoning,
                reasoning.Data.ReasoningId,
                null,
                reasoning.Data.Content),

            AssistantMessageDeltaEvent delta => new AgentContentDeltaEvent(
                AgentBackendIds.Copilot,
                sessionId,
                delta.Timestamp,
                new AgentRunId(delta.Data.MessageId),
                AgentContentKind.Assistant,
                delta.Data.MessageId,
                delta.Data.ParentToolCallId,
                delta.Data.DeltaContent),

            AssistantMessageEvent message => new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                sessionId,
                message.Timestamp,
                new AgentRunId(message.Data.MessageId),
                GetAssistantMessageContentKind(message.Data.Phase),
                message.Data.MessageId,
                message.Data.ParentToolCallId,
                message.Data.Content),

            UserMessageEvent userMessage => new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                sessionId,
                userMessage.Timestamp,
                null,
                AgentContentKind.User,
                userMessage.Data.InteractionId ?? $"user:{userMessage.Timestamp.ToUnixTimeMilliseconds()}",
                null,
                userMessage.Data.Content),

            AssistantUsageEvent assistantUsage => CreateSessionUpdate(
                sessionId,
                assistantUsage.Timestamp,
                AgentSessionUpdateKind.UsageUpdated,
                $"{assistantUsage.Data.Model}: {assistantUsage.Data.InputTokens ?? 0:0}/{assistantUsage.Data.OutputTokens ?? 0:0} tokens"),

            SessionIdleEvent idle => new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                sessionId,
                idle.Timestamp,
                null,
                AgentSessionUpdateKind.Idle,
                null),

            AbortEvent abort => CreateActivityEvent(
                sessionId,
                abort.Timestamp,
                null,
                AgentActivityKind.Turn,
                AgentActivityPhase.Canceled,
                "session-abort",
                null,
                "abort",
                abort.Data.Reason),

            ToolUserRequestedEvent toolRequested => CreateActivityEvent(
                sessionId,
                toolRequested.Timestamp,
                null,
                GetCopilotToolActivityKind(toolRequested.Data.ToolName, mcpToolName: null),
                AgentActivityPhase.Requested,
                toolRequested.Data.ToolCallId,
                null,
                toolRequested.Data.ToolName,
                "Tool requested."),

            ToolExecutionStartEvent toolStart => CreateActivityEvent(
                sessionId,
                toolStart.Timestamp,
                null,
                GetCopilotToolActivityKind(toolStart.Data.ToolName, toolStart.Data.McpToolName),
                AgentActivityPhase.Started,
                toolStart.Data.ToolCallId,
                toolStart.Data.ParentToolCallId,
                toolStart.Data.McpToolName ?? toolStart.Data.ToolName,
                toolStart.Data.McpServerName),

            ToolExecutionProgressEvent toolProgress => CreateActivityEvent(
                sessionId,
                toolProgress.Timestamp,
                null,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Progressed,
                toolProgress.Data.ToolCallId,
                null,
                null,
                toolProgress.Data.ProgressMessage),

            ToolExecutionPartialResultEvent toolPartialResult => new AgentContentDeltaEvent(
                AgentBackendIds.Copilot,
                sessionId,
                toolPartialResult.Timestamp,
                null,
                AgentContentKind.ToolOutput,
                toolPartialResult.Data.ToolCallId,
                toolPartialResult.Data.ToolCallId,
                toolPartialResult.Data.PartialOutput),

            ToolExecutionCompleteEvent toolComplete => CreateActivityEvent(
                sessionId,
                toolComplete.Timestamp,
                null,
                AgentActivityKind.ToolCall,
                toolComplete.Data.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                toolComplete.Data.ToolCallId,
                toolComplete.Data.ParentToolCallId,
                null,
                toolComplete.Data.Success
                    ? toolComplete.Data.Result?.Content
                    : toolComplete.Data.Error?.Message),

            SkillInvokedEvent skillInvoked => CreateActivityEvent(
                sessionId,
                skillInvoked.Timestamp,
                null,
                AgentActivityKind.Skill,
                AgentActivityPhase.Completed,
                skillInvoked.Data.Path,
                null,
                skillInvoked.Data.Name,
                skillInvoked.Data.Path),

            SubagentSelectedEvent subagentSelected => CreateActivityEvent(
                sessionId,
                subagentSelected.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Selected,
                subagentSelected.Data.AgentName,
                null,
                subagentSelected.Data.AgentDisplayName,
                null),

            SubagentStartedEvent subagentStarted => CreateActivityEvent(
                sessionId,
                subagentStarted.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Started,
                subagentStarted.Data.ToolCallId,
                null,
                subagentStarted.Data.AgentDisplayName,
                subagentStarted.Data.AgentDescription),

            SubagentCompletedEvent subagentCompleted => CreateActivityEvent(
                sessionId,
                subagentCompleted.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Completed,
                subagentCompleted.Data.ToolCallId,
                null,
                subagentCompleted.Data.AgentDisplayName,
                null),

            SubagentFailedEvent subagentFailed => CreateActivityEvent(
                sessionId,
                subagentFailed.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Failed,
                subagentFailed.Data.ToolCallId,
                null,
                subagentFailed.Data.AgentDisplayName,
                subagentFailed.Data.Error),

            SubagentDeselectedEvent => CreateActivityEvent(
                sessionId,
                sessionEvent.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Deselected,
                "subagent-selection",
                null,
                null,
                null),

            HookStartEvent hookStart => CreateActivityEvent(
                sessionId,
                hookStart.Timestamp,
                null,
                AgentActivityKind.Hook,
                AgentActivityPhase.Started,
                hookStart.Data.HookInvocationId,
                null,
                hookStart.Data.HookType,
                null),

            HookEndEvent hookEnd => CreateActivityEvent(
                sessionId,
                hookEnd.Timestamp,
                null,
                AgentActivityKind.Hook,
                hookEnd.Data.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                hookEnd.Data.HookInvocationId,
                null,
                hookEnd.Data.HookType,
                hookEnd.Data.Error?.Message),

            SystemMessageEvent systemMessage => new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                sessionId,
                systemMessage.Timestamp,
                null,
                AgentContentKind.Notice,
                $"system-message:{systemMessage.Timestamp.ToUnixTimeMilliseconds()}",
                null,
                systemMessage.Data.Content),

            SessionErrorEvent error => new AgentErrorEvent(
                AgentBackendIds.Copilot,
                sessionId,
                error.Timestamp,
                error.Data.Message),

            _ => new AgentRawEvent(
                AgentBackendIds.Copilot,
                sessionId,
                sessionEvent.Timestamp,
                sessionEvent.Type,
                ToRawElement(sessionEvent),
                TryGetRunId(sessionEvent))
        };
    }

    public static IReadOnlyList<AgentEvent> ToHistoryEvents(string sessionId, IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(events);

        return events.Select(sessionEvent => ToAgentEvent(sessionId, sessionEvent)).ToArray();
    }

    private static AgentContentKind GetAssistantMessageContentKind(string? phase)
        => phase switch
        {
            "commentary" => AgentContentKind.Reasoning,
            "final_answer" => AgentContentKind.Assistant,
            _ => AgentContentKind.Assistant,
        };

    private static PermissionRequestHandler CreatePermissionHandler(
        AgentPermissionRequestHandler handler,
        CopilotSessionCallbackBridge callbackBridge)
    {
        return async (request, invocation) =>
        {
            var mappedRequest = ToPermissionRequest(invocation.SessionId, request);
            LogCallbackDebug($"Permission request session={invocation.SessionId} payload={mappedRequest.ToJson()}");
            callbackBridge.Publish(mappedRequest);

            AgentPermissionDecision decision;
            try
            {
                decision = await handler(mappedRequest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCallbackError($"Permission request handler failed session={invocation.SessionId}", ex);
                callbackBridge.Publish(CreateHandlerErrorEvent(invocation.SessionId, $"permission request: {ex.Message}", ex));
                decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
            }

            LogCallbackDebug($"Permission request result session={invocation.SessionId} decision={decision.ToJson()}");
            callbackBridge.Publish(CreatePermissionResolvedEvent(mappedRequest, decision));
            return ToPermissionResult(decision);
        };
    }

    private static UserInputHandler? CreateUserInputHandler(
        AgentUserInputRequestHandler? handler,
        CopilotSessionCallbackBridge callbackBridge)
    {
        if (handler is null)
            return null;

        return async (request, invocation) =>
        {
            var mappedRequest = ToUserInputRequest(invocation.SessionId, request);
            LogCallbackDebug($"User input request session={invocation.SessionId} payload={mappedRequest.ToJson()}");
            callbackBridge.Publish(mappedRequest);

            AgentUserInputResponse response;
            try
            {
                response = await handler(mappedRequest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCallbackError($"User input request handler failed session={invocation.SessionId}", ex);
                callbackBridge.Publish(CreateHandlerErrorEvent(invocation.SessionId, $"user input request: {ex.Message}", ex));
                throw;
            }

            LogCallbackDebug($"User input request result session={invocation.SessionId} response={response.ToJson()}");
            callbackBridge.Publish(CreateUserInputResolvedEvent(mappedRequest, response));
            var answer = response.Answers.TryGetValue("answer", out var value)
                ? value
                : response.Answers.Values.FirstOrDefault() ?? string.Empty;

            var wasFreeform = request.Choices is { Count: > 0 } choices && !choices.Contains(answer, StringComparer.Ordinal);
            return new UserInputResponse
            {
                Answer = answer,
                WasFreeform = wasFreeform
            };
        };
    }

    private static void LogCallbackDebug(string message)
    {
        if (LogManager.IsInitialized && CallbackLogger.IsEnabled(LogLevel.Debug))
        {
            CallbackLogger.Debug(message);
        }
    }

    private static void LogCallbackError(string message, Exception exception)
    {
        if (LogManager.IsInitialized && CallbackLogger.IsEnabled(LogLevel.Error))
        {
            CallbackLogger.Error(exception, message);
        }
    }

    private static PermissionRequestResult ToPermissionResult(AgentPermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var kind = decision.Kind switch
        {
            AgentPermissionDecisionKind.AllowOnce => PermissionRequestResultKind.Approved,
            AgentPermissionDecisionKind.AllowForSession => PermissionRequestResultKind.Approved,
            AgentPermissionDecisionKind.Deny => PermissionRequestResultKind.DeniedInteractivelyByUser,
            AgentPermissionDecisionKind.Cancel => PermissionRequestResultKind.DeniedInteractivelyByUser,
            _ => PermissionRequestResultKind.DeniedInteractivelyByUser
        };

        return new PermissionRequestResult
        {
            Kind = kind
        };
    }

    private static AgentPermissionRequest ToPermissionRequest(string sessionId, PermissionRequest request)
    {
        var interactionId = request.ToolCallId ?? Guid.CreateVersion7().ToString();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", request.Kind);
            if (request.ToolCallId is not null)
                writer.WriteString("toolCallId", request.ToolCallId);

            if (request.ExtensionData is not null)
            {
                foreach (var pair in request.ExtensionData)
                {
                    switch (pair.Value)
                    {
                        case JsonElement element:
                            writer.WritePropertyName(pair.Key);
                            element.WriteTo(writer);
                            break;
                        case null:
                            writer.WriteNull(pair.Key);
                            break;
                        default:
                            writer.WriteString(pair.Key, pair.Value.ToString());
                            break;
                    }
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return new AgentGenericPermissionRequest(
            AgentBackendIds.Copilot,
            sessionId,
            DateTimeOffset.UtcNow,
            null,
            interactionId,
            request.Kind,
            document.RootElement.Clone());
    }

    private static AgentUserInputRequest ToUserInputRequest(string sessionId, UserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        var interactionId = Guid.CreateVersion7().ToString();
        return new AgentUserInputRequest(
            AgentBackendIds.Copilot,
            sessionId,
            DateTimeOffset.UtcNow,
            null,
            interactionId,
            new AgentUserInputForm(
                [
                    new AgentUserInputPrompt(
                        Id: "answer",
                        Question: request.Question,
                        Header: null,
                        Options: request.Choices?.Select(static choice => new AgentUserInputOption(choice)).ToArray(),
                        AllowFreeform: request.AllowFreeform ?? true)
                ]));
    }

    private static SystemMessageConfig? BuildSystemMessage(string? systemMessage, string? developerInstructions)
    {
        if (string.IsNullOrWhiteSpace(systemMessage) && string.IsNullOrWhiteSpace(developerInstructions))
            return null;

        var contentBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemMessage))
        {
            contentBuilder.AppendLine(systemMessage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(developerInstructions))
        {
            if (contentBuilder.Length > 0)
                contentBuilder.AppendLine();

            contentBuilder.Append(developerInstructions.Trim());
        }

        return new SystemMessageConfig
        {
            Mode = SystemMessageMode.Append,
            Content = contentBuilder.ToString()
        };
    }

    private static AgentSessionUpdateEvent CreateSessionUpdate(
        string sessionId,
        DateTimeOffset timestamp,
        AgentSessionUpdateKind kind,
        string? message,
        AgentRunId? runId = null)
    {
        return new AgentSessionUpdateEvent(
            AgentBackendIds.Copilot,
            sessionId,
            timestamp,
            runId,
            kind,
            message);
    }

    private static AgentInteractionEvent CreatePermissionResolvedEvent(
        AgentPermissionRequest request,
        AgentPermissionDecision decision)
    {
        return new AgentInteractionEvent(
            request.BackendId,
            request.SessionId,
            DateTimeOffset.UtcNow,
            request.RunId,
            AgentInteractionKind.PermissionResolved,
            request.InteractionId,
            $"Permission resolved: {decision.Kind}.",
            CreatePermissionResolutionDetails(decision));
    }

    private static AgentInteractionEvent CreateUserInputResolvedEvent(
        AgentUserInputRequest request,
        AgentUserInputResponse response)
    {
        return new AgentInteractionEvent(
            request.BackendId,
            request.SessionId,
            DateTimeOffset.UtcNow,
            request.RunId,
            AgentInteractionKind.UserInputResolved,
            request.InteractionId,
            $"User input resolved ({response.Answers.Count} answer(s)).",
            CreateUserInputResolutionDetails(response));
    }

    private static AgentErrorEvent CreateHandlerErrorEvent(string sessionId, string message, Exception exception)
    {
        return new AgentErrorEvent(
            AgentBackendIds.Copilot,
            sessionId,
            DateTimeOffset.UtcNow,
            $"Failed while handling {message}",
            exception);
    }

    private static AgentActivityEvent CreateActivityEvent(
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
            AgentBackendIds.Copilot,
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

    private static JsonElement CreatePermissionResolutionDetails(AgentPermissionDecision decision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("decisionKind", decision.Kind.ToString());

            if (decision.ExecPolicyAmendment is { Count: > 0 } execPolicyAmendment)
            {
                writer.WritePropertyName("execPolicyAmendment");
                writer.WriteStartArray();
                foreach (var rule in execPolicyAmendment)
                {
                    writer.WriteStringValue(rule);
                }

                writer.WriteEndArray();
            }

            if (decision.NetworkPolicyAmendment is { } networkPolicyAmendment)
            {
                writer.WritePropertyName("networkPolicyAmendment");
                writer.WriteStartObject();
                writer.WriteString("action", networkPolicyAmendment.Action.ToString());
                writer.WriteString("host", networkPolicyAmendment.Host);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateUserInputResolutionDetails(AgentUserInputResponse response)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("answerCount", response.Answers.Count);
            writer.WritePropertyName("answers");
            writer.WriteStartObject();
            foreach (var pair in response.Answers)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static AgentPlanChangeKind ToPlanChangeKind(SessionPlanChangedDataOperation operation)
    {
        return operation switch
        {
            SessionPlanChangedDataOperation.Create => AgentPlanChangeKind.Created,
            SessionPlanChangedDataOperation.Update => AgentPlanChangeKind.Updated,
            SessionPlanChangedDataOperation.Delete => AgentPlanChangeKind.Deleted,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported plan operation."),
        };
    }

    private static AgentActivityKind GetCopilotToolActivityKind(string toolName, string? mcpToolName)
    {
        if (!string.IsNullOrWhiteSpace(mcpToolName))
        {
            return AgentActivityKind.McpToolCall;
        }

        return string.Equals(toolName, "task", StringComparison.OrdinalIgnoreCase)
            ? AgentActivityKind.Subagent
            : AgentActivityKind.ToolCall;
    }

    private static ICollection<AIFunction>? ToCopilotTools(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
            return null;

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        return tools
            .Select(tool => ToCopilotTool(tool, GetCopilotToolName(tool.Spec.Name, usedNames)))
            .ToArray();
    }

    private static Dictionary<string, object>? ToCopilotMcpServers(IReadOnlyDictionary<string, AgentMcpServerConfig>? servers)
    {
        if (servers is not { Count: > 0 })
        {
            return null;
        }

        var mapped = new Dictionary<string, object>(servers.Count, StringComparer.Ordinal);
        foreach (var pair in servers)
        {
            mapped[pair.Key] = pair.Value switch
            {
                AgentLocalMcpServerConfig local => ToCopilotLocalMcpServerConfig(local),
                AgentRemoteMcpServerConfig remote => ToCopilotRemoteMcpServerConfig(remote),
                _ => throw new ArgumentOutOfRangeException(nameof(servers), pair.Value, "Unsupported MCP server config."),
            };
        }

        return mapped;
    }

    private static McpLocalServerConfig ToCopilotLocalMcpServerConfig(AgentLocalMcpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Command);

        return new McpLocalServerConfig
        {
            Command = config.Command,
            Args = config.Arguments?.ToList() ?? [],
            Cwd = config.WorkingDirectory,
            Env = config.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(config.EnvironmentVariables, StringComparer.Ordinal),
            Timeout = ToCopilotTimeoutMilliseconds(config.ToolTimeout),
            Tools = ToCopilotToolFilter(config.EnabledTools),
            Type = "local"
        };
    }

    private static McpRemoteServerConfig ToCopilotRemoteMcpServerConfig(AgentRemoteMcpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Url);

        if (config.BearerTokenEnvironmentVariable is not null || config.EnvironmentHeaders is not null)
        {
            throw new NotSupportedException(
                "Copilot MCP server configuration does not support environment-derived HTTP credentials through this adapter.");
        }

        return new McpRemoteServerConfig
        {
            Url = config.Url,
            Type = config.Transport == AgentMcpRemoteTransport.Sse ? "sse" : "http",
            Headers = config.Headers is null
                ? null
                : new Dictionary<string, string>(config.Headers, StringComparer.Ordinal),
            Timeout = ToCopilotTimeoutMilliseconds(config.ToolTimeout),
            Tools = ToCopilotToolFilter(config.EnabledTools)
        };
    }

    private static List<string> ToCopilotToolFilter(IReadOnlyList<string>? tools)
        => tools is { Count: > 0 } ? [.. tools] : ["*"];

    private static int? ToCopilotTimeoutMilliseconds(TimeSpan? timeout)
    {
        if (timeout is null)
        {
            return null;
        }

        var milliseconds = checked((long)Math.Ceiling(timeout.Value.TotalMilliseconds));
        return milliseconds switch
        {
            <= 0 => 1,
            > int.MaxValue => int.MaxValue,
            _ => (int)milliseconds,
        };
    }

    private static AIFunction ToCopilotTool(AgentToolDefinition tool, string registeredToolName)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredToolName);

        return AIFunctionFactory.Create(
            async (JsonElement arguments, AIFunctionArguments rawArgs) =>
            {
                var invocation = GetInvocation(rawArgs, tool.Spec.Name, arguments);
                var result = await tool.Handler(invocation, CancellationToken.None).ConfigureAwait(false);

                var text = string.Join(
                    Environment.NewLine,
                    result.Items.Select(static item => item switch
                    {
                        AgentToolResultItem.Text value => value.Value,
                        AgentToolResultItem.ImageUrl value => $"[image-url] {value.Url}",
                        _ => string.Empty
                    }).Where(static line => !string.IsNullOrEmpty(line)));

                var payload = new ToolResultObject
                {
                    ResultType = result.Success ? "success" : "failure",
                    TextResultForLlm = text,
                    Error = result.Error
                };

                return new ToolResultAIContent(payload);
            },
            registeredToolName,
            tool.Spec.Description);
    }

    internal static string GetCopilotToolName(string toolName, ISet<string>? usedNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        Span<char> buffer = stackalloc char[toolName.Length];
        var length = 0;
        var lastWasSeparator = false;

        foreach (var ch in toolName)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                buffer[length++] = ch;
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                buffer[length++] = '_';
                lastWasSeparator = true;
            }
        }

        var candidate = length == 0
            ? "tool"
            : new string(buffer[..length]).Trim('_');
        if (candidate.Length == 0)
        {
            candidate = "tool";
        }

        const int maxToolNameLength = 64;
        if (candidate.Length > maxToolNameLength)
        {
            candidate = candidate[..maxToolNameLength];
        }

        if (usedNames is null)
        {
            return candidate;
        }

        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $"_{suffix}";
            var baseLength = Math.Min(candidate.Length, maxToolNameLength - suffixText.Length);
            var uniqueCandidate = string.Concat(candidate.AsSpan(0, baseLength), suffixText);
            if (usedNames.Add(uniqueCandidate))
            {
                return uniqueCandidate;
            }
        }
    }

    private static AgentToolInvocation GetInvocation(
        AIFunctionArguments rawArgs,
        string toolName,
        JsonElement arguments)
    {
        if (rawArgs.Context is not null &&
            rawArgs.Context.TryGetValue(typeof(ToolInvocation), out var value) &&
            value is ToolInvocation invocation)
        {
            return new AgentToolInvocation(
                AgentBackendIds.Copilot,
                invocation.SessionId,
                invocation.ToolCallId,
                toolName,
                arguments);
        }

        return new AgentToolInvocation(
            AgentBackendIds.Copilot,
            string.Empty,
            string.Empty,
            toolName,
            arguments);
    }

    private static string? ToCopilotReasoningEffort(AgentReasoningEffort? effort)
    {
        return effort switch
        {
            null => null,
            AgentReasoningEffort.None => "none",
            AgentReasoningEffort.Minimal => "minimal",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            _ => null
        };
    }

    private static AgentReasoningEffort? ToAgentReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;

        return effort.Trim().ToLowerInvariant() switch
        {
            "none" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            _ => null
        };
    }

    private static IReadOnlyList<AgentReasoningEffort>? ToAgentReasoningEfforts(IReadOnlyList<string>? efforts)
    {
        if (efforts is not { Count: > 0 })
            return null;

        var values = new List<AgentReasoningEffort>(efforts.Count);
        foreach (var effort in efforts)
        {
            var mapped = ToAgentReasoningEffort(effort);
            if (mapped is not { } value)
                continue;

            if (!values.Contains(value))
                values.Add(value);
        }

        return values;
    }

    private static JsonElement ToRawElement(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        try
        {
            var json = sessionEvent.ToJson();
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return CreateRawFallback(sessionEvent, ex.Message);
        }
    }

    private static JsonElement CreateRawFallback(SessionEvent sessionEvent, string serializationError)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", sessionEvent.Type);
            writer.WriteString("eventClass", sessionEvent.GetType().FullName);
            writer.WriteString("serializationError", serializationError);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static AgentRunId? TryGetRunId(SessionEvent sessionEvent)
    {
        return sessionEvent switch
        {
            AssistantMessageEvent message => new AgentRunId(message.Data.MessageId),
            AssistantMessageDeltaEvent delta => new AgentRunId(delta.Data.MessageId),
            _ => null
        };
    }

    private static void AppendPromptLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
            builder.AppendLine();

        builder.Append(line);
    }
}
