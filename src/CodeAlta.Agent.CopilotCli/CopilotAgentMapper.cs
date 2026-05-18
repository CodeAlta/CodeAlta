using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.CopilotCli;

internal static class CopilotAgentMapper
{
    private static readonly Logger CallbackLogger = LogManager.GetLogger("CodeAlta.Agent.Copilot.Callbacks");

    public static AgentModelInfo ToAgentModelInfo(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var modelCapabilities = model.Capabilities;
        var capabilities = new Dictionary<string, object?>
        {
            ["capabilities"] = modelCapabilities,
            ["policy"] = model.Policy,
            ["billing"] = model.Billing,
            ["supportedReasoningEfforts"] = model.SupportedReasoningEfforts,
            ["defaultReasoningEffort"] = model.DefaultReasoningEffort,
            ["supportsImageInput"] = modelCapabilities?.Supports?.Vision,
            ["imageInputMaxImages"] = modelCapabilities?.Limits?.Vision?.MaxPromptImages,
            ["imageInputMaxBytes"] = modelCapabilities?.Limits?.Vision?.MaxPromptImageSize,
            ["imageInputMediaTypes"] = modelCapabilities?.Limits?.Vision?.SupportedMediaTypes
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
            WorkspacePath: null,
            Details: new CopilotSessionMetadataDetails(metadata.IsRemote));
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
            SessionId = string.IsNullOrWhiteSpace(options.ThreadId) ? null : options.ThreadId.Trim(),
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
            DisableResume = true,
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

        var attachments = new List<UserMessageAttachment>();
        var promptBuilder = new StringBuilder();

        foreach (var item in input.Items)
        {
            switch (item)
            {
                case AgentInputItem.Text text:
                    AppendPromptLine(promptBuilder, text.Value);
                    break;

                case AgentInputItem.File file:
                    attachments.Add(new UserMessageAttachmentFile
                    {
                        Path = file.Path,
                        DisplayName = file.DisplayName ?? file.Path,
                        LineRange = file.LineRange is null
                            ? null
                            : new UserMessageAttachmentFileLineRange
                            {
                                Start = file.LineRange.StartLine,
                                End = file.LineRange.EndLine
                            }
                    });
                    break;

                case AgentInputItem.Directory directory:
                    attachments.Add(new UserMessageAttachmentDirectory
                    {
                        Path = directory.Path,
                        DisplayName = directory.DisplayName ?? directory.Path,
                        // LineRange = directory.LineRange is null
                        //     ? null
                        //     : new UserMessageAttachmentDirectoryLineRange
                        //     {
                        //         Start = directory.LineRange.StartLine,
                        //         End = directory.LineRange.EndLine
                        //     }
                    });
                    break;

                case AgentInputItem.Selection selection:
                    attachments.Add(new UserMessageAttachmentSelection
                    {
                        FilePath = selection.FilePath,
                        DisplayName = selection.DisplayName,
                        Text = selection.SelectedText,
                        Selection = new UserMessageAttachmentSelectionDetails
                        {
                            Start = new UserMessageAttachmentSelectionDetailsStart
                            {
                                Line = selection.Range.Start.Line,
                                Character = selection.Range.Start.Character
                            },
                            End = new UserMessageAttachmentSelectionDetailsEnd
                            {
                                Line = selection.Range.End.Line,
                                Character = selection.Range.End.Character
                            }
                        }
                    });
                    break;

                case AgentInputItem.ImageUrl imageUrl:
                    if (TryParseBase64DataUri(imageUrl.Url, out var mediaType, out var base64Data))
                    {
                        attachments.Add(new UserMessageAttachmentBlob
                        {
                            Data = base64Data,
                            DisplayName = "image",
                            MimeType = mediaType
                        });
                    }
                    else
                    {
                        AppendPromptLine(promptBuilder, $"[image-url] {imageUrl.Url}");
                    }

                    break;

                case AgentInputItem.LocalImage localImage:
                    attachments.Add(new UserMessageAttachmentBlob
                    {
                        Data = Convert.ToBase64String(File.ReadAllBytes(localImage.Path)),
                        DisplayName = string.IsNullOrWhiteSpace(localImage.DisplayName) ? Path.GetFileName(localImage.Path) : localImage.DisplayName,
                        MimeType = ResolveImageMediaType(localImage.MediaType, localImage.Path)
                    });
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
                FormattableString.Invariant($"{usage.Data.CurrentTokens:0}/{usage.Data.TokenLimit:0} tokens"),
                usage: CreateCopilotSessionUsage(usage.Timestamp, usage.Data)),

            SessionCompactionStartEvent compactionStart => CreateSessionUpdate(
                sessionId,
                compactionStart.Timestamp,
                AgentSessionUpdateKind.CompactionStarted,
                FormatCompactionStartedMessage(compactionStart.Data)),

            SessionCompactionCompleteEvent compactionComplete => CreateSessionUpdate(
                sessionId,
                compactionComplete.Timestamp,
                AgentSessionUpdateKind.CompactionCompleted,
                FormatCompactionCompletedMessage(compactionComplete.Data),
                usage: CreateCopilotCompactionUsage(compactionComplete.Timestamp, compactionComplete.Data)),

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

            SessionWorkspaceFileChangedEvent workspaceFileChanged => CreateSessionUpdate(
                sessionId,
                workspaceFileChanged.Timestamp,
                AgentSessionUpdateKind.DiffUpdated,
                "Workspace file changed.",
                details: CreateWorkspaceFileChangedDetails(workspaceFileChanged.Data)),

            SessionHandoffEvent handoff => CreateSessionUpdate(
                sessionId,
                handoff.Timestamp,
                AgentSessionUpdateKind.Handoff,
                handoff.Data.Summary ?? handoff.Data.Context ?? "Session handoff."),

            SessionTruncationEvent truncation => CreateSessionUpdate(
                sessionId,
                truncation.Timestamp,
                AgentSessionUpdateKind.Truncated,
                $"{truncation.Data.MessagesRemovedDuringTruncation:0} messages removed.",
                usage: CreateCopilotTruncationUsage(truncation.Timestamp, truncation.Data)),

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
                null,
                delta.Data.DeltaContent),

            AssistantMessageEvent message => new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                sessionId,
                message.Timestamp,
                new AgentRunId(message.Data.MessageId),
                GetAssistantMessageContentKind(message.Data),
                message.Data.MessageId,
                null,
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
                FormattableString.Invariant($"{assistantUsage.Data.Model}: {assistantUsage.Data.InputTokens ?? 0:0}/{assistantUsage.Data.OutputTokens ?? 0:0} tokens"),
                usage: CreateCopilotAssistantUsage(assistantUsage.Timestamp, assistantUsage.Data)),

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
                abort.Data.Reason.ToString()),

            ToolUserRequestedEvent toolRequested => CreateActivityEvent(
                sessionId,
                toolRequested.Timestamp,
                null,
                GetCopilotToolActivityKind(toolRequested.Data.ToolName, mcpToolName: null),
                AgentActivityPhase.Requested,
                toolRequested.Data.ToolCallId,
                null,
                GetCopilotToolDisplayName(toolRequested.Data.ToolName, mcpToolName: null, toolRequested.Data.Arguments),
                "Tool requested.",
                CreateToolUserRequestedDetails(toolRequested.Data)),

            ToolExecutionStartEvent toolStart => CreateActivityEvent(
                sessionId,
                toolStart.Timestamp,
                null,
                GetCopilotToolActivityKind(toolStart.Data.ToolName, toolStart.Data.McpToolName),
                AgentActivityPhase.Started,
                toolStart.Data.ToolCallId,
                null,
                GetCopilotToolDisplayName(toolStart.Data.ToolName, toolStart.Data.McpToolName, toolStart.Data.Arguments),
                toolStart.Data.McpServerName,
                CreateToolExecutionStartDetails(toolStart.Data)),

            ToolExecutionProgressEvent toolProgress => CreateActivityEvent(
                sessionId,
                toolProgress.Timestamp,
                null,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Progressed,
                toolProgress.Data.ToolCallId,
                null,
                null,
                toolProgress.Data.ProgressMessage,
                CreateToolExecutionProgressDetails(toolProgress.Data)),

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
                GetCopilotToolActivityKind(toolComplete.Data),
                IsCopilotToolExecutionFailure(toolComplete.Data) ? AgentActivityPhase.Failed : AgentActivityPhase.Completed,
                toolComplete.Data.ToolCallId,
                null,
                GetCopilotToolDisplayName(toolComplete.Data),
                ResolveCopilotToolCompletionMessage(toolComplete.Data),
                CreateToolExecutionCompleteDetails(toolComplete.Data)),

            SkillInvokedEvent skillInvoked => CreateActivityEvent(
                sessionId,
                skillInvoked.Timestamp,
                null,
                AgentActivityKind.Skill,
                AgentActivityPhase.Completed,
                skillInvoked.Data.Path,
                null,
                skillInvoked.Data.Name,
                skillInvoked.Data.Path,
                CreateSkillInvokedDetails(skillInvoked.Data)),

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
                subagentStarted.Data.AgentDescription,
                CreateSubagentStartedDetails(subagentStarted.Data)),

            SubagentCompletedEvent subagentCompleted => CreateActivityEvent(
                sessionId,
                subagentCompleted.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Completed,
                subagentCompleted.Data.ToolCallId,
                null,
                subagentCompleted.Data.AgentDisplayName,
                null,
                CreateSubagentCompletedDetails(subagentCompleted.Data)),

            SubagentFailedEvent subagentFailed => CreateActivityEvent(
                sessionId,
                subagentFailed.Timestamp,
                null,
                AgentActivityKind.Subagent,
                AgentActivityPhase.Failed,
                subagentFailed.Data.ToolCallId,
                null,
                subagentFailed.Data.AgentDisplayName,
                subagentFailed.Data.Error,
                CreateSubagentFailedDetails(subagentFailed.Data)),

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
                null,
                CreateHookStartDetails(hookStart.Data)),

            HookEndEvent hookEnd => CreateActivityEvent(
                sessionId,
                hookEnd.Timestamp,
                null,
                AgentActivityKind.Hook,
                hookEnd.Data.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                hookEnd.Data.HookInvocationId,
                null,
                hookEnd.Data.HookType,
                hookEnd.Data.Error?.Message,
                CreateHookEndDetails(hookEnd.Data)),

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

    internal static IReadOnlyList<AgentEvent> ToAgentEvents(string sessionId, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return [ToAgentEvent(sessionId, sessionEvent)];
    }

    public static IReadOnlyList<AgentEvent> ToHistoryEvents(string sessionId, IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(events);

        var tracker = new CopilotAgentSession.CopilotInteractionTracker();
        var projectedEvents = new List<AgentEvent>(events.Count);

        foreach (var sessionEvent in events)
        {
            projectedEvents.AddRange(CopilotAgentSession.ProjectSessionEvents(sessionId, sessionEvent, tracker));
        }

        return projectedEvents;
    }

    internal static AgentContentKind GetAssistantMessageContentKind(AssistantMessageData message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.Equals(message.Phase, "final_answer", StringComparison.Ordinal))
        {
            return AgentContentKind.Assistant;
        }

        if (!string.IsNullOrWhiteSpace(message.Phase))
        {
            return AgentContentKind.Reasoning;
        }

        if (message.ToolRequests is { Length: > 0 } || string.IsNullOrWhiteSpace(message.Content))
        {
            return AgentContentKind.Reasoning;
        }

        return AgentContentKind.Assistant;
    }

    internal static AgentContentCompletedEvent? TryCreateEmbeddedReasoningEvent(string sessionId, AssistantMessageEvent message)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Data.ReasoningText))
        {
            return null;
        }

        return new AgentContentCompletedEvent(
            AgentBackendIds.Copilot,
            sessionId,
            message.Timestamp,
            new AgentRunId(message.Data.MessageId),
            AgentContentKind.Reasoning,
            $"{message.Data.MessageId}:reasoning",
            null,
            message.Data.ReasoningText);
    }

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
        CallbackLogger.Debug(message);
    }

    private static void LogCallbackError(string message, Exception exception)
    {
        CallbackLogger.Error(exception, message);
    }

    private static GitHub.Copilot.SDK.PermissionRequestResult ToPermissionResult(AgentPermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var kind = decision.Kind switch
        {
            AgentPermissionDecisionKind.AllowOnce => PermissionRequestResultKind.Approved,
            AgentPermissionDecisionKind.AllowForSession => PermissionRequestResultKind.Approved,
            AgentPermissionDecisionKind.Deny => PermissionRequestResultKind.Rejected,
            AgentPermissionDecisionKind.Cancel => PermissionRequestResultKind.Rejected,
            _ => PermissionRequestResultKind.Rejected
        };

        return new GitHub.Copilot.SDK.PermissionRequestResult
        {
            Kind = kind
        };
    }

    private static string? GetToolCallId(PermissionRequest request)
    {
        return request switch
        {
            PermissionRequestCustomTool permissionRequestCustomTool => permissionRequestCustomTool.ToolCallId,
            PermissionRequestHook permissionRequestHook => permissionRequestHook.ToolCallId,
            PermissionRequestMcp permissionRequestMcp => permissionRequestMcp.ToolCallId,
            PermissionRequestMemory permissionRequestMemory => permissionRequestMemory.ToolCallId,
            PermissionRequestRead permissionRequestRead => permissionRequestRead.ToolCallId,
            PermissionRequestShell permissionRequestShell => permissionRequestShell.ToolCallId,
            PermissionRequestUrl permissionRequestUrl => permissionRequestUrl.ToolCallId,
            PermissionRequestWrite permissionRequestWrite => permissionRequestWrite.ToolCallId,
            _ => null
        };
    }

    private static AgentPermissionRequest ToPermissionRequest(string sessionId, PermissionRequest request)
    {
        var requestToolCallId = GetToolCallId(request);
        var interactionId = requestToolCallId ?? Guid.CreateVersion7().ToString();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", request.Kind);
            if (requestToolCallId is not null)
                writer.WriteString("toolCallId", requestToolCallId);

#if CODEALTA_LOCAL_COPILOT_SDK
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
#endif

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
        AgentRunId? runId = null,
        JsonElement? details = null,
        AgentSessionUsage? usage = null)
    {
        return new AgentSessionUpdateEvent(
            AgentBackendIds.Copilot,
            sessionId,
            timestamp,
            runId,
            kind,
            message,
            details,
            Usage: usage);
    }

    private static JsonElement CreateWorkspaceFileChangedDetails(SessionWorkspaceFileChangedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("path", data.Path);
            var operation = data.Operation;
            var operationValue = operation == WorkspaceFileChangedOperation.Create
                ? "create"
                : "update";
            writer.WriteString("operation", operationValue);
        });
    }

    private static AgentSessionUsage CreateCopilotSessionUsage(DateTimeOffset timestamp, SessionUsageInfoData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                ToInt64(data.CurrentTokens),
                ToInt64(data.TokenLimit),
                ToInt32(data.MessagesLength),
                "Active context window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CopilotSessionUsageInfo,
            UpdatedAt: timestamp);
    }

    private static AgentSessionUsage CreateCopilotAssistantUsage(DateTimeOffset timestamp, AssistantUsageData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                Model: data.Model,
                InputTokens: ToNullableInt64(data.InputTokens),
                OutputTokens: ToNullableInt64(data.OutputTokens),
                CacheReadTokens: ToNullableInt64(data.CacheReadTokens),
                CacheWriteTokens: ToNullableInt64(data.CacheWriteTokens),
                Cost: data.Cost,
                DurationMs: data.Duration,
                Initiator: data.Initiator,
                ParentToolCallId: null,
                ReasoningEffort: null,
                Label: "Last API call"),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.CopilotAssistantUsage,
            UpdatedAt: timestamp,
            Details: new CopilotSessionUsageDetails(
                LastAssistantUsage: new CopilotAssistantUsage(
                    data.Model,
                    InputTokens: ToNullableInt64(data.InputTokens),
                    OutputTokens: ToNullableInt64(data.OutputTokens),
                    CacheReadTokens: ToNullableInt64(data.CacheReadTokens),
                    CacheWriteTokens: ToNullableInt64(data.CacheWriteTokens),
                    Cost: data.Cost,
                    DurationMs: data.Duration,
                    Initiator: data.Initiator,
                    ParentToolCallId: null,
                    ReasoningEffort: null,
                    TotalNanoAiu: data.CopilotUsage?.TotalNanoAiu,
                    TokenDetails: data.CopilotUsage?.TokenDetails
                        .Select(token => new CopilotTokenDetail(token.TokenType, ToInt64(token.TokenCount)))
                        .ToArray()),
                QuotaSnapshots: CreateQuotaSnapshots(data.QuotaSnapshots)));
    }

    internal static AgentSessionUsage? CreateCopilotQuotaUsage(DateTimeOffset timestamp, AccountGetQuotaResult? quota)
    {
        if (quota?.QuotaSnapshots is not { Count: > 0 } snapshots)
        {
            return null;
        }

        return new AgentSessionUsage(
            Scope: AgentUsageScope.RateLimitOnly,
            Source: AgentUsageSource.CopilotAccountQuota,
            UpdatedAt: timestamp,
            Details: new CopilotSessionUsageDetails(
                QuotaSnapshots: CreateQuotaSnapshots(snapshots)));
    }

    private static AgentSessionUsage CreateCopilotCompactionUsage(DateTimeOffset timestamp, SessionCompactionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var messageCount = default(int?);
        if (data.PreCompactionMessagesLength is { } preMessages && data.MessagesRemoved is { } removedMessages)
        {
            messageCount = Math.Max(0, ToInt32(preMessages) - ToInt32(removedMessages));
        }

        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                ToNullableInt64(data.PostCompactionTokens),
                null,
                messageCount,
                "Post-compaction window"),
            LastOperation: data.CompactionTokensUsed is null
                ? null
                : new AgentOperationUsageSnapshot(
                    InputTokens: ToInt64(data.CompactionTokensUsed.InputTokens ?? 0),
                    OutputTokens: ToInt64(data.CompactionTokensUsed.OutputTokens ?? 0),
                    CachedInputTokens: ToInt64(data.CompactionTokensUsed.CacheReadTokens ?? 0),
                    Label: "Compaction call"),
            Scope: AgentUsageScope.Compaction,
            Source: AgentUsageSource.CopilotCompactionComplete,
            UpdatedAt: timestamp,
            Details: new CopilotSessionUsageDetails(
                LastCompaction: new CopilotCompactionUsage(
                    data.Success,
                    PreCompactionTokens: ToNullableInt64(data.PreCompactionTokens),
                    PostCompactionTokens: ToNullableInt64(data.PostCompactionTokens),
                    PreCompactionMessages: ToNullableInt32(data.PreCompactionMessagesLength),
                    MessagesRemoved: ToNullableInt32(data.MessagesRemoved),
                    TokensRemoved: ToNullableInt64(data.TokensRemoved),
                    TokensUsed: data.CompactionTokensUsed is null
                        ? null
                        : new CopilotCompactionTokenUsage(
                            ToInt64(data.CompactionTokensUsed.InputTokens ?? 0),
                            ToInt64(data.CompactionTokensUsed.OutputTokens ?? 0),
                            ToInt64(data.CompactionTokensUsed.CacheReadTokens ?? 0)),
                    SummaryContent: data.SummaryContent)));
    }

    private static string FormatCompactionStartedMessage(SessionCompactionStartData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var totalTokens = SumNullableDoubles(data.SystemTokens, data.ConversationTokens, data.ToolDefinitionsTokens);
        return totalTokens is { } value
            ? FormattableString.Invariant($"Compaction started at {value:0} tokens.")
            : "Compaction started.";
    }

    private static string FormatCompactionCompletedMessage(SessionCompactionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!data.Success)
        {
            return data.Error ?? "Compaction failed.";
        }

        if (data.PreCompactionTokens is { } preTokens && data.PostCompactionTokens is { } postTokens)
        {
            if (data.MessagesRemoved is { } removedMessages)
            {
                return FormattableString.Invariant(
                    $"Compaction completed ({preTokens:0} -> {postTokens:0} tokens, {removedMessages:0} messages removed).");
            }

            return FormattableString.Invariant($"Compaction completed ({preTokens:0} -> {postTokens:0} tokens).");
        }

        if (data.TokensRemoved is { } removedTokens && data.MessagesRemoved is { } removedMessagesOnly)
        {
            return FormattableString.Invariant(
                $"Compaction completed ({removedTokens:0} tokens and {removedMessagesOnly:0} messages removed).");
        }

        if (data.TokensRemoved is { } tokensOnly)
        {
            return FormattableString.Invariant($"Compaction completed ({tokensOnly:0} tokens removed).");
        }

        if (data.MessagesRemoved is { } messagesOnly)
        {
            return FormattableString.Invariant($"Compaction completed ({messagesOnly:0} messages removed).");
        }

        return "Compaction completed.";
    }

    private static double? SumNullableDoubles(params double?[] values)
    {
        double sum = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (value is not { } number)
            {
                continue;
            }

            sum += number;
            hasValue = true;
        }

        return hasValue ? sum : null;
    }

    private static AgentSessionUsage CreateCopilotTruncationUsage(DateTimeOffset timestamp, SessionTruncationData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                ToInt64(data.PostTruncationTokensInMessages),
                ToInt64(data.TokenLimit),
                ToInt32(data.PostTruncationMessagesLength),
                "Post-truncation window"),
            Scope: AgentUsageScope.Truncation,
            Source: AgentUsageSource.CopilotTruncation,
            UpdatedAt: timestamp);
    }

    private static CopilotQuotaSnapshot[]? CreateQuotaSnapshots(IDictionary<string, AssistantUsageQuotaSnapshot>? snapshots)
    {
        if (snapshots is not { Count: > 0 })
        {
            return null;
        }

        return snapshots
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => new CopilotQuotaSnapshot(entry.Key, ToCopilotQuotaDetails(entry.Value)))
            .ToArray();
    }

    private static CopilotQuotaSnapshot[]? CreateQuotaSnapshots(IDictionary<string, AccountQuotaSnapshot>? snapshots)
    {
        if (snapshots is not { Count: > 0 })
        {
            return null;
        }

        return snapshots
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => new CopilotQuotaSnapshot(entry.Key, ToCopilotQuotaDetails(entry.Value)))
            .ToArray();
    }

    private static CopilotQuotaDetails ToCopilotQuotaDetails(AssistantUsageQuotaSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);

        bool? isUnlimitedEntitlement = value.IsUnlimitedEntitlement || value.EntitlementRequests < 0 ? true : null;
        return new CopilotRequestQuotaDetails(
            EntitlementRequests: value.EntitlementRequests >= 0 ? ToInt64(value.EntitlementRequests) : null,
            UsedRequests: ToInt64(value.UsedRequests),
            RemainingPercentage: value.RemainingPercentage,
            Overage: ToInt64(value.Overage),
            UsageAllowedWithExhaustion: value.OverageAllowedWithExhaustedQuota,
            IsUnlimitedEntitlement: isUnlimitedEntitlement,
            ResetDate: value.ResetDate);
    }

    private static CopilotQuotaDetails ToCopilotQuotaDetails(AccountQuotaSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);

        bool? isUnlimitedEntitlement = value.IsUnlimitedEntitlement || value.EntitlementRequests < 0 ? true : null;
        return new CopilotRequestQuotaDetails(
            EntitlementRequests: value.EntitlementRequests >= 0 ? value.EntitlementRequests : null,
            UsedRequests: value.UsedRequests,
            RemainingPercentage: value.RemainingPercentage,
            Overage: ToInt64(value.Overage),
            UsageAllowedWithExhaustion: value.OverageAllowedWithExhaustedQuota,
            IsUnlimitedEntitlement: isUnlimitedEntitlement,
            ResetDate: TryParseQuotaResetDate(value.ResetDate));
    }

    private static CopilotQuotaDetails ToCopilotQuotaDetails(object? value)
    {
        var payload = ToJsonElement(value);
        return TryCreateRequestQuotaDetails(payload, out var details)
            ? details
            : new CopilotOpaqueQuotaDetails(SummarizeQuotaPayload(payload));
    }

    private static bool TryCreateRequestQuotaDetails(JsonElement payload, out CopilotRequestQuotaDetails details)
    {
        details = default!;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasKnownField = false;

        long? entitlementRequests = null;
        if (TryGetInt64(payload, "entitlementRequests", out var parsedEntitlementRequests))
        {
            entitlementRequests = parsedEntitlementRequests;
            hasKnownField = true;
        }

        long? usedRequests = null;
        if (TryGetInt64(payload, "usedRequests", out var parsedUsedRequests))
        {
            usedRequests = parsedUsedRequests;
            hasKnownField = true;
        }

        double? remainingPercentage = null;
        if (TryGetDouble(payload, "remainingPercentage", out var parsedRemainingPercentage))
        {
            remainingPercentage = parsedRemainingPercentage;
            hasKnownField = true;
        }

        long? overage = null;
        if (TryGetInt64(payload, "overage", out var parsedOverage))
        {
            overage = parsedOverage;
            hasKnownField = true;
        }

        bool? usageAllowedWithExhaustion = null;
        if (TryGetBoolean(payload, "usageAllowedWithExhaustion", out var parsedUsageAllowed) ||
            TryGetBoolean(payload, "overageAllowedWithExhaustedQuota", out parsedUsageAllowed))
        {
            usageAllowedWithExhaustion = parsedUsageAllowed;
            hasKnownField = true;
        }

        bool? isUnlimitedEntitlement = null;
        if (TryGetBoolean(payload, "isUnlimitedEntitlement", out var parsedUnlimited))
        {
            isUnlimitedEntitlement = parsedUnlimited;
            hasKnownField = true;
        }

        DateTimeOffset? resetDate = null;
        if (TryGetDateTimeOffset(payload, "resetDate", out var parsedResetDate))
        {
            resetDate = parsedResetDate;
            hasKnownField = true;
        }

        if (!hasKnownField)
        {
            return false;
        }

        details = new CopilotRequestQuotaDetails(
            EntitlementRequests: entitlementRequests,
            UsedRequests: usedRequests,
            RemainingPercentage: remainingPercentage,
            Overage: overage,
            UsageAllowedWithExhaustion: usageAllowedWithExhaustion,
            IsUnlimitedEntitlement: isUnlimitedEntitlement,
            ResetDate: resetDate);
        return true;
    }

    private static JsonElement ToJsonElement(object? value)
        => value switch
        {
            JsonElement json => json.Clone(),
            null => JsonDocument.Parse("null").RootElement.Clone(),
            string text => CreateJsonStringElement(text),
            bool boolean => JsonDocument.Parse(boolean ? "true" : "false").RootElement.Clone(),
            int number => JsonDocument.Parse(number.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
            long number => JsonDocument.Parse(number.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
            double number => JsonDocument.Parse(number.ToString("R", CultureInfo.InvariantCulture)).RootElement.Clone(),
            decimal number => JsonDocument.Parse(number.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
            float number => JsonDocument.Parse(number.ToString("R", CultureInfo.InvariantCulture)).RootElement.Clone(),
            IDictionary<string, object?> dictionary => CreateJsonObjectElement(dictionary),
            IDictionary dictionary => CreateJsonObjectElement(dictionary),
            IEnumerable enumerable => CreateJsonArrayElement(enumerable),
            _ => CreateJsonStringElement(value.ToString() ?? string.Empty),
        };

    private static JsonElement CreateJsonObjectElement(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var entry in entries)
        {
            writer.WritePropertyName(entry.Key);
            WriteJsonValue(writer, entry.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).RootElement.Clone();
    }

    private static JsonElement CreateJsonObjectElement(IDictionary dictionary)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            writer.WritePropertyName(entry.Key?.ToString() ?? string.Empty);
            WriteJsonValue(writer, entry.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).RootElement.Clone();
    }

    private static JsonElement CreateJsonArrayElement(IEnumerable values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var item in values)
        {
            WriteJsonValue(writer, item);
        }

        writer.WriteEndArray();
        writer.Flush();
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).RootElement.Clone();
    }

    private static JsonElement CreateJsonStringElement(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStringValue(value);
        writer.Flush();
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).RootElement.Clone();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                return;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                return;
            case bool booleanValue:
                writer.WriteBooleanValue(booleanValue);
                return;
            case int numberValue:
                writer.WriteNumberValue(numberValue);
                return;
            case long numberValue:
                writer.WriteNumberValue(numberValue);
                return;
            case double numberValue:
                writer.WriteNumberValue(numberValue);
                return;
            case decimal numberValue:
                writer.WriteNumberValue(numberValue);
                return;
            case float numberValue:
                writer.WriteNumberValue(numberValue);
                return;
            case IDictionary<string, object?> dictionaryValue:
                writer.WriteStartObject();
                foreach (var entry in dictionaryValue)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IDictionary dictionaryValue:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionaryValue)
                {
                    writer.WritePropertyName(entry.Key?.ToString() ?? string.Empty);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable enumerableValue:
                writer.WriteStartArray();
                foreach (var item in enumerableValue)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(value.ToString());
                return;
        }
    }

    private static string SummarizeQuotaPayload(JsonElement payload)
    {
        var raw = payload.GetRawText();
        return raw.Length <= 160
            ? raw
            : raw[..157] + "...";
    }

    private static bool TryGetBoolean(JsonElement payload, string propertyName, out bool value)
    {
        value = default;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetInt64(JsonElement payload, string propertyName, out long value)
    {
        value = default;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var doubleValue))
        {
            value = checked((long)doubleValue);
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetDouble(JsonElement payload, string propertyName, out double value)
    {
        value = default;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetDateTimeOffset(JsonElement payload, string propertyName, out DateTimeOffset value)
    {
        value = default;
        if (!payload.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    private static DateTimeOffset? TryParseQuotaResetDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static long ToInt64(double value) => checked((long)value);

    private static long? ToNullableInt64(double? value) => value is { } concrete ? ToInt64(concrete) : null;

    private static int ToInt32(double value) => checked((int)value);

    private static int? ToNullableInt32(double? value) => value is { } concrete ? ToInt32(concrete) : null;

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
        string? message,
        JsonElement? details = null)
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
            message,
            details);
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

    private static AgentPlanChangeKind ToPlanChangeKind(PlanChangedOperation operation)
    {
        if (operation == PlanChangedOperation.Create)
        {
            return AgentPlanChangeKind.Created;
        }

        if (operation == PlanChangedOperation.Update)
        {
            return AgentPlanChangeKind.Updated;
        }

        if (operation == PlanChangedOperation.Delete)
        {
            return AgentPlanChangeKind.Deleted;
        }

        throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported plan operation.");
    }

    private static AgentActivityKind GetCopilotToolActivityKind(string toolName, string? mcpToolName)
    {
        if (!string.IsNullOrWhiteSpace(mcpToolName))
        {
            return AgentActivityKind.McpToolCall;
        }

        return (toolName ?? string.Empty).Trim() switch
        {
            var name when string.Equals(name, "task", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.Subagent,
            var name when string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.CommandExecution,
            var name when string.Equals(name, "shell_command", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.CommandExecution,
            var name when string.Equals(name, "bash", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.CommandExecution,
            var name when string.Equals(name, "apply_patch", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.FileChange,
            var name when string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.WebSearch,
            var name when string.Equals(name, "image_generation", StringComparison.OrdinalIgnoreCase) => AgentActivityKind.ImageGeneration,
            _ => AgentActivityKind.ToolCall,
        };
    }

    private static AgentActivityKind GetCopilotToolActivityKind(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (HasCopilotTerminalResult(data.Result))
        {
            return AgentActivityKind.CommandExecution;
        }

        return GetCopilotToolActivityKind(TryInferCopilotToolName(data) ?? string.Empty, mcpToolName: null);
    }

    private static string? GetCopilotToolDisplayName(string toolName, string? mcpToolName, object? arguments)
    {
        var activityKind = GetCopilotToolActivityKind(toolName, mcpToolName);
        if (activityKind == AgentActivityKind.CommandExecution &&
            TryGetNormalizedToolArgument(arguments, out var command, "command"))
        {
            return command;
        }

        return mcpToolName ?? toolName;
    }

    private static string? GetCopilotToolDisplayName(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var toolName = TryInferCopilotToolName(data);
        return string.IsNullOrWhiteSpace(toolName) ? null : toolName;
    }

    private static JsonElement CreateToolUserRequestedDetails(ToolUserRequestedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var arguments = SerializeRuntimeObject(data.Arguments);
        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteString("toolName", data.ToolName);
            WriteSerializedProperty(writer, "arguments", arguments);
            WriteKnownToolFields(writer, arguments);
        });
    }

    private static JsonElement CreateToolExecutionStartDetails(ToolExecutionStartData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var arguments = SerializeRuntimeObject(data.Arguments);
        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteString("toolName", data.ToolName);
            if (!string.IsNullOrWhiteSpace(data.McpToolName))
                writer.WriteString("mcpToolName", data.McpToolName);
            if (!string.IsNullOrWhiteSpace(data.McpServerName))
                writer.WriteString("mcpServerName", data.McpServerName);
            WriteSerializedProperty(writer, "arguments", arguments);
            WriteKnownToolFields(writer, arguments);
        });
    }

    private static JsonElement CreateToolExecutionProgressDetails(ToolExecutionProgressData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            if (!string.IsNullOrWhiteSpace(data.ProgressMessage))
                writer.WriteString("progressMessage", data.ProgressMessage);
        });
    }

    private static JsonElement CreateToolExecutionCompleteDetails(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var telemetry = SerializeRuntimeObject(data.ToolTelemetry);
        var telemetryProperties = TryGetNestedObject(telemetry, "properties");
        var telemetryRestrictedProperties = TryGetNestedObject(telemetry, "restrictedProperties");
        var inferredToolName = TryInferCopilotToolName(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteBoolean("success", data.Success);
            if (TryResolveCopilotTerminalExitCode(data.Result, out var exitCode))
                writer.WriteNumber("exitCode", exitCode);
            if (!string.IsNullOrWhiteSpace(data.Model))
                writer.WriteString("model", data.Model);
            if (!string.IsNullOrWhiteSpace(data.InteractionId))
                writer.WriteString("interactionId", data.InteractionId);
            if (!string.IsNullOrWhiteSpace(inferredToolName))
                writer.WriteString("toolName", inferredToolName);
            WriteToolExecutionResult(writer, data.Result);
            WriteToolExecutionError(writer, data.Error);
            WriteSerializedProperty(writer, "toolTelemetry", telemetry);
            WriteKnownToolFields(writer, telemetryProperties, telemetryRestrictedProperties);
        });
    }

    private static JsonElement CreateSkillInvokedDetails(SkillInvokedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("name", data.Name);
            writer.WriteString("path", data.Path);
        });
    }

    private static JsonElement CreateSubagentStartedDetails(SubagentStartedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteString("agentName", data.AgentName);
            writer.WriteString("agentDisplayName", data.AgentDisplayName);
            if (!string.IsNullOrWhiteSpace(data.AgentDescription))
                writer.WriteString("agentDescription", data.AgentDescription);
        });
    }

    private static JsonElement CreateSubagentCompletedDetails(SubagentCompletedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteString("agentDisplayName", data.AgentDisplayName);
        });
    }

    private static JsonElement CreateSubagentFailedDetails(SubagentFailedData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("toolCallId", data.ToolCallId);
            writer.WriteString("agentDisplayName", data.AgentDisplayName);
            if (!string.IsNullOrWhiteSpace(data.Error))
                writer.WriteString("error", data.Error);
        });
    }

    private static JsonElement CreateHookStartDetails(HookStartData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return CreateObjectElement(writer =>
        {
            writer.WriteString("hookInvocationId", data.HookInvocationId);
            writer.WriteString("hookType", data.HookType);
        });
    }

    private static JsonElement CreateHookEndDetails(HookEndData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var error = SerializeRuntimeObject(data.Error);
        return CreateObjectElement(writer =>
        {
            writer.WriteString("hookInvocationId", data.HookInvocationId);
            writer.WriteString("hookType", data.HookType);
            writer.WriteBoolean("success", data.Success);
            WriteSerializedProperty(writer, "error", error);
        });
    }

    private static void WriteToolExecutionResult(Utf8JsonWriter writer, ToolExecutionCompleteResult? result)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (result is null)
        {
            return;
        }

        writer.WritePropertyName("result");
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(result.Content))
            writer.WriteString("content", result.Content);
        if (!string.IsNullOrWhiteSpace(result.DetailedContent))
            writer.WriteString("detailedContent", result.DetailedContent);
        if (result.Contents is { Length: > 0 })
        {
            writer.WritePropertyName("contents");
            WriteUntypedJsonValue(writer, result.Contents);
        }

        writer.WriteEndObject();
    }

    private static void WriteToolExecutionError(Utf8JsonWriter writer, ToolExecutionCompleteError? error)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (error is null)
        {
            return;
        }

        writer.WritePropertyName("error");
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(error.Message))
            writer.WriteString("message", error.Message);
        if (!string.IsNullOrWhiteSpace(error.Code))
            writer.WriteString("code", error.Code);
        writer.WriteEndObject();
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

    private static Dictionary<string, McpServerConfig>? ToCopilotMcpServers(IReadOnlyDictionary<string, AgentMcpServerConfig>? servers)
    {
        if (servers is not { Count: > 0 })
        {
            return null;
        }

        var mapped = new Dictionary<string, McpServerConfig>(servers.Count, StringComparer.Ordinal);
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

    private static McpStdioServerConfig ToCopilotLocalMcpServerConfig(AgentLocalMcpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Command);

        return new McpStdioServerConfig
        {
            Command = config.Command,
            Args = config.Arguments?.ToList() ?? [],
            Cwd = config.WorkingDirectory,
            Env = config.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(config.EnvironmentVariables, StringComparer.Ordinal),
            Timeout = ToCopilotTimeoutMilliseconds(config.ToolTimeout),
            Tools = ToCopilotToolFilter(config.EnabledTools),
        };
    }

    private static McpHttpServerConfig ToCopilotRemoteMcpServerConfig(AgentRemoteMcpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Url);

        if (config.BearerTokenEnvironmentVariable is not null || config.EnvironmentHeaders is not null)
        {
            throw new NotSupportedException(
                "Copilot MCP server configuration does not support environment-derived HTTP credentials through this adapter.");
        }

        return new McpHttpServerConfig
        {
            Url = config.Url,
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

    private static IReadOnlyList<AgentReasoningEffort>? ToAgentReasoningEfforts(IList<string>? efforts)
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

    private static JsonElement CreateObjectElement(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement? SerializeRuntimeObject(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return CreateValueElement(writer => WriteUntypedJsonValue(writer, value));
    }

    private static void WriteSerializedProperty(Utf8JsonWriter writer, string propertyName, JsonElement? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        if (value is not { } element)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        element.WriteTo(writer);
    }

    private static JsonElement CreateValueElement(Action<Utf8JsonWriter> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeValue(writer);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteUntypedJsonValue(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case byte number:
                writer.WriteNumberValue(number);
                return;
            case short number:
                writer.WriteNumberValue(number);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case float number:
                writer.WriteNumberValue(number);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        continue;
                    }

                    writer.WritePropertyName(key);
                    WriteUntypedJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var pair in pairs)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteUntypedJsonValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable enumerable when value is not string:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteUntypedJsonValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(value.ToString());
                return;
        }
    }

    private static void WriteKnownToolFields(Utf8JsonWriter writer, params JsonElement?[] sources)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WriteKnownToolField(writer, "command", sources);
        WriteKnownToolField(writer, "description", sources);
        WriteKnownToolField(writer, "path", sources);
        WriteKnownToolField(writer, "pattern", sources);
        WriteKnownToolField(writer, "query", sources);
        WriteKnownToolField(writer, "intent", sources);
        WriteKnownToolField(writer, "database", sources);
        WriteKnownToolField(writer, "viewType", sources);
        WriteKnownToolField(writer, "queryType", sources);
    }

    private static void WriteKnownToolField(Utf8JsonWriter writer, string propertyName, params JsonElement?[] sources)
    {
        if (TryGetNormalizedToolValue(propertyName, sources) is not { } value)
        {
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static bool TryGetNormalizedToolArgument(object? value, out string? normalized, string propertyName)
    {
        normalized = TryGetNormalizedToolValue(propertyName, SerializeRuntimeObject(value));
        return !string.IsNullOrWhiteSpace(normalized);
    }

    private static bool IsCopilotToolExecutionFailure(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!data.Success)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(data.Error?.Message) || !string.IsNullOrWhiteSpace(data.Error?.Code))
        {
            return true;
        }

        return TryResolveCopilotTerminalExitCode(data.Result, out var exitCode) && exitCode != 0;
    }

    private static string? ResolveCopilotToolCompletionMessage(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!IsCopilotToolExecutionFailure(data))
        {
            return data.Result?.Content;
        }

        return !string.IsNullOrWhiteSpace(data.Error?.Message)
            ? data.Error.Message
            : data.Result?.Content;
    }

    private static bool HasCopilotTerminalResult(ToolExecutionCompleteResult? result)
    {
        if (result is null)
        {
            return false;
        }

        if (result.Contents is { Length: > 0 } contents &&
            contents.Any(static item => item is ToolExecutionCompleteContentTerminal))
        {
            return true;
        }

        return ContainsCopilotTerminalExitMarker(result.Content) ||
               ContainsCopilotTerminalExitMarker(result.DetailedContent);
    }

    private static bool TryResolveCopilotTerminalExitCode(ToolExecutionCompleteResult? result, out int exitCode)
    {
        exitCode = 0;
        if (result is null)
        {
            return false;
        }

        if (result.Contents is { Length: > 0 } contents)
        {
            foreach (var content in contents)
            {
                if (content is ToolExecutionCompleteContentTerminal { ExitCode: { } terminalExitCode })
                {
                    exitCode = (int)terminalExitCode;
                    return true;
                }
            }
        }

        return TryExtractCopilotTerminalExitCode(result.Content, out exitCode) ||
               TryExtractCopilotTerminalExitCode(result.DetailedContent, out exitCode);
    }

    private static bool ContainsCopilotTerminalExitMarker(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Contains("<exited with exit code ", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractCopilotTerminalExitCode(string? text, out int exitCode)
    {
        exitCode = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        const string marker = "<exited with exit code ";
        var markerIndex = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = text.IndexOf('>', valueStart);
        if (valueEnd < 0)
        {
            return false;
        }

        var exitCodeText = text[valueStart..valueEnd].Trim();
        return int.TryParse(exitCodeText, out exitCode);
    }

    private static string? TryInferCopilotToolName(ToolExecutionCompleteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var telemetry = SerializeRuntimeObject(data.ToolTelemetry);
        if (TryGetNestedString(telemetry, out var toolName, "properties", "command"))
        {
            return toolName;
        }

        if (TryResolveResultString(data.Result, out var content, result => result.Content))
        {
            if (string.Equals(content, "Intent logged", StringComparison.Ordinal))
            {
                return "report_intent";
            }

            if (!string.IsNullOrWhiteSpace(content) && LooksLikePathList(content))
            {
                return "glob";
            }
        }

        if (TryResolveResultString(data.Result, out var detailedContent, result => result.DetailedContent) &&
            !string.IsNullOrWhiteSpace(detailedContent) &&
            (detailedContent.Contains("diff --git", StringComparison.Ordinal) ||
             detailedContent.Contains("--- a/", StringComparison.Ordinal)))
        {
            return "view";
        }

        if (TryGetNormalizedToolValue("query", TryGetNestedObject(telemetry, "restrictedProperties")) is not null)
        {
            return "sql";
        }

        return null;
    }

    private static bool TryResolveResultString(
        ToolExecutionCompleteResult? result,
        out string? value,
        Func<ToolExecutionCompleteResult, string?> selector)
    {
        value = result is null ? null : selector(result);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static JsonElement? TryGetNestedObject(JsonElement? root, string propertyName)
    {
        if (root is not { } element ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.Clone();
    }

    private static bool TryGetNestedString(JsonElement? root, out string? value, params string[] path)
    {
        value = null;
        if (root is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? TryGetNormalizedToolValue(string propertyName, params JsonElement?[] sources)
    {
        foreach (var source in sources)
        {
            if (source is not { } element || element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.ToString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool LooksLikePathList(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Take(12)
            .ToArray();
        if (lines.Length < 2)
        {
            return false;
        }

        var matchingPathLines = lines.Count(static line =>
        {
            var trimmed = line.Trim().Trim('"', '\'', '`');
            if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
            {
                return true;
            }

            return !trimmed.Contains(" ", StringComparison.Ordinal) &&
                   (!string.IsNullOrWhiteSpace(Path.GetExtension(trimmed)) || trimmed.StartsWith(".", StringComparison.Ordinal));
        });

        return matchingPathLines >= Math.Max(2, lines.Length - 1);
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

    private static string? GuessImageMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => null,
        };

    private static string ResolveImageMediaType(string? mediaType, string path)
        => string.IsNullOrWhiteSpace(mediaType)
            ? GuessImageMediaType(path) ?? "application/octet-stream"
            : mediaType.Trim();

    private static bool TryParseBase64DataUri(string value, out string mediaType, out string base64Data)
    {
        mediaType = string.Empty;
        base64Data = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return false;
        }

        var metadata = value.AsSpan(5, commaIndex - 5);
        var isBase64 = false;
        foreach (var segmentRange in metadata.Split(';'))
        {
            if (metadata[segmentRange].Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
                break;
            }
        }

        if (!isBase64)
        {
            return false;
        }

        var payload = value[(commaIndex + 1)..].Trim();
        if (payload.Length == 0)
        {
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = metadata.IndexOf(';');
        var parsedMediaType = separatorIndex < 0 ? metadata : metadata[..separatorIndex];
        mediaType = parsedMediaType.IsWhiteSpace() ? "application/octet-stream" : parsedMediaType.Trim().ToString();
        base64Data = payload;
        return true;
    }

    private static void AppendPromptLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
            builder.AppendLine();

        builder.Append(line);
    }
}
