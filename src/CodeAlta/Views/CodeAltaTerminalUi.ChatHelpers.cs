using System.Text;
using System.Text.Json;
using System.Globalization;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Threading;

internal sealed partial class CodeAltaTerminalUi
{
    private static void ReplaceSelectItems<T>(Select<T> select, IReadOnlyList<T> items)
    {
        select.Items.Clear();
        foreach (var item in items)
        {
            select.Items.Add(item);
        }
    }

    private static List<ChatBackendOption> BuildChatBackendOptions()
    {
        return
        [
            new ChatBackendOption(AgentBackendIds.Codex, "Codex"),
            new ChatBackendOption(AgentBackendIds.Copilot, "Copilot"),
        ];
    }

    private static List<ChatModelOption> BuildChatModelOptions(ChatBackendState backendState)
    {
        if (backendState.Models.Count == 0)
        {
            return [new ChatModelOption(null, "(default)")];
        }

        return backendState.Models
            .Select(model => new ChatModelOption(model.Id, model.DisplayName ?? model.Id))
            .ToList();
    }

    internal static List<ChatReasoningOption> BuildChatReasoningOptions(AgentModelInfo? model)
    {
        var options = new List<ChatReasoningOption>
        {
            new(null, "Default"),
        };

        var efforts = model?.SupportedReasoningEfforts is { Count: > 0 } supported
            ? supported
            : Enum.GetValues<AgentReasoningEffort>();

        foreach (var effort in efforts.Distinct())
        {
            options.Add(new ChatReasoningOption(effort, SplitPascalCase(effort.ToString())));
        }

        return options;
    }

    internal static AgentBackendId ResolveChatBackendSelection(
        AgentBackendId currentSelection,
        AgentBackendId requestedBackend,
        bool adoptRequestedBackend)
        => adoptRequestedBackend ? requestedBackend : currentSelection;

    internal static string BuildChatBackendStatusMarkup(
        IEnumerable<ChatBackendState> backendStates,
        AgentBackendId selectedBackendId,
        bool isInitializing)
    {
        var items = backendStates
            .OrderBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(state =>
            {
                var tone = state.Availability switch
                {
                    ChatBackendAvailability.Ready => "success",
                    ChatBackendAvailability.Unsupported or ChatBackendAvailability.Failed => "warning",
                    ChatBackendAvailability.Connecting => "primary",
                    _ => "muted",
                };
                var icon = state.Availability switch
                {
                    ChatBackendAvailability.Ready => $"{NerdFont.MdCheck}",
                    ChatBackendAvailability.Unsupported => $"{NerdFont.CodWarning}",
                    ChatBackendAvailability.Failed => $"{NerdFont.MdClose}",
                    ChatBackendAvailability.Connecting => $"{NerdFont.MdTimerOutline}",
                    _ => $"{NerdFont.MdHelpBox}",
                };
                var selected = string.Equals(state.BackendId.Value, selectedBackendId.Value, StringComparison.OrdinalIgnoreCase)
                    ? "[bold]"
                    : string.Empty;
                var reset = selected.Length > 0 ? "[/]" : string.Empty;
                return $"{selected}[{tone}]{icon} {AnsiMarkup.Escape(state.DisplayName)}[/]{reset}";
            });

        var prefix = isInitializing
            ? $"[primary]{NerdFont.MdTimerOutline} Detecting[/] "
            : string.Empty;
        return prefix + string.Join("   ", items);
    }

    private static AgentModelInfo? GetSelectedModel(ChatBackendState backendState)
    {
        return string.IsNullOrWhiteSpace(backendState.SelectedModelId)
            ? null
            : backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal));
    }

    private static AgentReasoningEffort? NormalizeReasoningEffort(
        AgentReasoningEffort? selectedReasoningEffort,
        AgentModelInfo? model)
    {
        if (selectedReasoningEffort is null)
        {
            return null;
        }

        if (model?.SupportedReasoningEfforts is not { Count: > 0 } supportedReasoningEfforts)
        {
            return selectedReasoningEffort;
        }

        return supportedReasoningEfforts.Contains(selectedReasoningEffort.Value)
            ? selectedReasoningEffort
            : null;
    }

    private static string BuildReadyStatusMessage(ChatBackendState backendState)
    {
        var selectedModel = GetSelectedModel(backendState);
        if (selectedModel is not null)
        {
            return $"Connected · {selectedModel.DisplayName ?? selectedModel.Id}";
        }

        return backendState.Models.Count switch
        {
            0 => "Connected.",
            1 => $"Connected · {backendState.Models[0].DisplayName ?? backendState.Models[0].Id}",
            _ => $"Connected · {backendState.Models.Count} models",
        };
    }

    private static string BuildUnsupportedBackendMessage(ChatBackendState backendState, string message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "CLI not found." : message.Trim();
        return $"{backendState.DisplayName} is unavailable: {trimmed}";
    }

    private static string BuildFailedBackendMessage(ChatBackendState backendState, string message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "Failed to initialize backend." : message.Trim();
        return $"{backendState.DisplayName} failed: {trimmed}";
    }

    private static DocumentFlowItem CreateUserChatItem(string markdown)
        => CreateChatMarkdownItem(
            markdown,
            ChatTimelineTone.User,
            headerOverride: "User Prompt",
            maxCodeBlockHeight: 10).Item;

    private static DocumentFlowItem CreateUserPromptSeparatorItem()
        => RunOnUiThread(
            static () => new DocumentFlowItem
            {
                Content = new FlowDocument().Add(new Rule()),
                Alignment = DocumentFlowAlignment.Stretch,
                Padding = new Thickness(0, 1, 0, 0),
            });

    private static DocumentFlowItem CreateAssistantStreamingChatItem(out MarkdownControl markdownControl, out Markup timestampText)
    {
        var entry = CreateChatMarkdownItem(string.Empty, ChatTimelineTone.Assistant);
        markdownControl = entry.Markdown;
        timestampText = entry.TimestampText;
        return entry.Item;
    }

    internal static PendingChatMessage CreatePendingChatMessage(string userMarkdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMarkdown);

        var userItem = CreateUserChatItem(userMarkdown);
        var assistantItem = CreateAssistantStreamingChatItem(out var streamingMarkdown, out var timestampText);
        return new PendingChatMessage(userItem, assistantItem, streamingMarkdown, timestampText);
    }

    internal static IReadOnlyList<DocumentFlowItem> BuildUserPromptTimelineItems(DocumentFlowItem userItem, bool hasSeenUserPrompt)
        => hasSeenUserPrompt
            ? [CreateUserPromptSeparatorItem(), userItem]
            : [userItem];

    internal static string FormatChatCardTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static void ApplyChatCardTimestamp(Markup timestampText, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestampText);

        RunOnUiThread(
            static state =>
            {
                state.timestampText.Text = $"[dim]{FormatChatCardTimestamp(state.timestamp)}[/]";
                return 0;
            },
            (timestampText, timestamp));
    }

    private static Dictionary<string, ChatBackendState> CreateChatBackendStates()
    {
        return new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
        {
            [AgentBackendIds.Codex.Value] = new(AgentBackendIds.Codex, "Codex"),
            [AgentBackendIds.Copilot.Value] = new(AgentBackendIds.Copilot, "Copilot"),
        };
    }

    private static Markup CreateChatCardHeader(ChatTimelineTone tone, string? headerOverride, string? headerSecondary)
    {
        var (icon, title, toneName) = GetChatCardHeaderParts(tone, headerOverride);
        return string.IsNullOrWhiteSpace(headerSecondary)
            ? new Markup($"[{toneName}]{icon}[/] [bold]{AnsiMarkup.Escape(title)}[/]")
            : new Markup($"[{toneName}]{icon}[/] [bold]{AnsiMarkup.Escape(title)}[/] [dim]- {AnsiMarkup.Escape(headerSecondary)}[/]");
    }

    private static GroupStyle CreateChatGroupStyle(ChatTimelineTone tone)
        => UiPalette.GetChatGroupStyle(tone);

    private static (string Icon, string Title, string ToneName) GetChatCardHeaderParts(ChatTimelineTone tone, string? headerOverride)
    {
        var (icon, defaultTitle, toneName) = GetChatCardDefaults(tone);
        if (!string.IsNullOrWhiteSpace(headerOverride))
        {
            return (icon, headerOverride, toneName);
        }

        return (icon, defaultTitle, toneName);
    }

    private static (string Icon, string Title, string ToneName) GetChatCardDefaults(ChatTimelineTone tone)
        => tone switch
        {
            ChatTimelineTone.User => ($"{NerdFont.MdAccount}", "User Prompt", "accent"),
            ChatTimelineTone.Assistant => ($"{NerdFont.MdRobot}", "Assistant", "success"),
            ChatTimelineTone.Reasoning => ($"{NerdFont.CodLightbulb}", "Reasoning", "primary"),
            ChatTimelineTone.Activity => ($"{NerdFont.CodTools}", "Activity", "muted"),
            ChatTimelineTone.Notice => ($"{NerdFont.CodInfo}", "Notice", "success"),
            ChatTimelineTone.Interaction => ($"{NerdFont.CodLock}", "Action Required", "warning"),
            _ => ($"{NerdFont.MdMessageText}", "Message", "primary"),
        };

    private static ChatMarkdownEntry CreateChatMarkdownItem(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14)
        => RunOnUiThread(
            static state => CreateChatMarkdownItemCore(state.markdown, state.tone, state.headerOverride, state.headerSecondary, state.maxCodeBlockHeight),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight));

    private static ChatMarkdownEntry CreateChatMarkdownItemCore(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride,
        string? headerSecondary,
        int maxCodeBlockHeight)
    {
        var headerText = CreateChatCardHeader(tone, headerOverride, headerSecondary);
        var markdownControl = new MarkdownControl(markdown.Trim())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = maxCodeBlockHeight,
            },
        };

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy} Copy"))
            .Click(() => markdownControl.App?.Terminal.Clipboard.TrySetText(markdown));

        var timestampText = new Markup(string.Empty);

        var group = new Group(headerText, markdownControl)
            .TopRightText(copyButton)
            .BottomRightText(timestampText)
            .Style(CreateChatGroupStyle(tone))
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Start);

        return new ChatMarkdownEntry(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(group),
                Alignment = DocumentFlowAlignment.Stretch,
            },
            markdownControl,
            timestampText,
            headerText);
    }

    internal static string FormatChatContentMarkdown(AgentContentKind kind, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        content = SanitizeInlineImageContent(content);

        return kind switch
        {
            AgentContentKind.User => content,
            AgentContentKind.Assistant => content,
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => TrimReasoningContent(content),
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput => FormatChatOutputMarkdown(content),
            _ => content,
        };
    }

    private static string SanitizeInlineImageContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var normalizedLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<string>(normalizedLines.Length);
        var changed = false;

        foreach (var line in normalizedLines)
        {
            var trimmed = line.Trim();
            if (string.Equals(trimmed, "<image>", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                if (lines.Count == 0 || !string.Equals(lines[^1], "Inline Image", StringComparison.Ordinal))
                {
                    lines.Add("Inline Image");
                }

                changed = true;
                continue;
            }

            lines.Add(line);
        }

        return changed
            ? string.Join(Environment.NewLine, lines)
            : content;
    }

    internal static string? GetChatContentHeaderSecondary(AgentContentKind kind, string content)
    {
        return kind switch
        {
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => BuildReasoningSummary(content),
            _ => null,
        };
    }

    internal static void ApplyChatCardHeader(Markup headerText, ChatTimelineTone tone, string? headerOverride, string? headerSecondary)
    {
        ArgumentNullException.ThrowIfNull(headerText);

        RunOnUiThread(
            static state =>
            {
                state.headerText.Text = CreateChatCardHeader(state.tone, state.headerOverride, state.headerSecondary).Text;
                return 0;
            },
            (headerText, tone, headerOverride, headerSecondary));
    }

    internal static string FormatChatPlanMarkdown(AgentPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var builder = new StringBuilder();
        if (snapshot.ChangeKind is { } changeKind)
        {
            builder.Append("_").Append(SplitPascalCase(changeKind.ToString())).Append("._");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Explanation))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(snapshot.Explanation);
        }

        if (snapshot.Steps is { Count: > 0 } steps)
        {
            foreach (var step in steps)
            {
                builder.AppendLine()
                    .Append("- ")
                    .Append(FormatPlanStepStatus(step.Status))
                    .Append(step.Text);
            }
        }

        return builder.ToString();
    }

    internal static string FormatChatActivityMarkdown(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var builder = new StringBuilder();
        var displayName = ResolveActivityDisplayName(activity);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            builder.AppendLine()
                .Append("- Name: `")
                .Append(displayName)
                .Append('`');
        }

        if (!string.IsNullOrWhiteSpace(activity.Message))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder
                .Append("- Detail: ")
                .Append(SummarizeActivityMessage(activity));
        }

        return builder.ToString();
    }

    private static string? ResolveActivityDisplayName(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Details is { } details &&
            TryGetStringProperty(details, "command", out var command) &&
            !string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        return activity.Name;
    }

    internal static string FormatChatSessionUpdateMarkdown(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Message ?? string.Empty;
    }

    internal static string GetSessionUpdateHeader(AgentSessionUpdateKind kind)
        => kind switch
        {
            AgentSessionUpdateKind.Info => $"{NerdFont.CodInfo} Info",
            AgentSessionUpdateKind.Warning => $"{NerdFont.CodWarning} Warning",
            AgentSessionUpdateKind.ModelChanged => $"{NerdFont.MdChat} Model Changed",
            AgentSessionUpdateKind.ModeChanged => $"{NerdFont.MdCubeOutline} Mode Changed",
            AgentSessionUpdateKind.TitleChanged => $"{NerdFont.MdRenameBox} Title Changed",
            AgentSessionUpdateKind.ContextChanged => $"{NerdFont.MdFolder} Context Changed",
            AgentSessionUpdateKind.PlanUpdated => $"{NerdFont.MdProgressWrench} Plan Updated",
            AgentSessionUpdateKind.UsageUpdated => $"{NerdFont.MdPacMan} Usage Updated",
            AgentSessionUpdateKind.CompactionStarted => $"{NerdFont.MdSelectCompare} Compaction Started",
            AgentSessionUpdateKind.CompactionCompleted => $"{NerdFont.MdShieldPlusOutline} Compaction Completed",
            AgentSessionUpdateKind.Handoff => $"{NerdFont.MdServerNetwork} Handoff",
            AgentSessionUpdateKind.Truncated => $"{NerdFont.MdDelete} Session Truncated",
            AgentSessionUpdateKind.Shutdown => $"{NerdFont.MdClose} Session Shutdown",
            AgentSessionUpdateKind.TaskCompleted => $"{NerdFont.MdCheck} Task Completed",
            AgentSessionUpdateKind.DiffUpdated => $"{NerdFont.CodEdit} Diff Updated",
            AgentSessionUpdateKind.Started => $"{NerdFont.MdTimerOutline} Session Started",
            AgentSessionUpdateKind.Resumed => $"{NerdFont.MdAccountArrowRight} Session Resumed",
            AgentSessionUpdateKind.Idle => $"{NerdFont.MdCat} Agent Idle",
            _ => SplitPascalCase(kind.ToString()),
        };

    internal static string FormatChatPermissionRequestMarkdown(AgentPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder("_The agent is blocked until this permission request is resolved._");

        switch (request)
        {
            case AgentCommandPermissionRequest command:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: command execution");

                if (!string.IsNullOrWhiteSpace(command.Command))
                {
                    builder.AppendLine()
                        .AppendLine()
                        .Append(FormatChatCodeFence(command.Command, "shell"));
                }

                AppendBullet(builder, "Working directory", command.WorkingDirectory, code: true);
                AppendBullet(builder, "Reason", command.Reason);

                if (command.Actions is { Count: > 0 } actions)
                {
                    builder.AppendLine().AppendLine().AppendLine("**Actions**");
                    foreach (var action in actions)
                    {
                        builder.Append("- ")
                            .Append(ToDisplayLabel(action.Kind));

                        if (!string.IsNullOrWhiteSpace(action.Path))
                        {
                            builder.Append(": `").Append(action.Path).Append('`');
                        }
                        else if (!string.IsNullOrWhiteSpace(action.Query))
                        {
                            builder.Append(": `").Append(action.Query).Append('`');
                        }

                        builder.AppendLine();
                    }
                }

                if (command.Network is { } network)
                {
                    AppendBullet(builder, "Network", $"{network.Protocol}://{network.Host}");
                }

                break;

            case AgentFileChangePermissionRequest fileChange:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: file change");
                AppendBullet(builder, "Grant root", fileChange.GrantRoot, code: true);
                AppendBullet(builder, "Reason", fileChange.Reason);
                break;

            case AgentGenericPermissionRequest generic:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(generic.Kind);
                if (TryGetStringProperty(generic.Raw, "toolName", out var toolName))
                {
                    builder.AppendLine().Append("- Tool: `").Append(toolName).Append('`');
                }

                builder.AppendLine()
                    .AppendLine()
                    .Append(FormatChatCodeFence(generic.Raw.GetRawText(), "json"));
                break;

            default:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(request.Kind);
                break;
        }

        return builder.ToString();
    }

    internal static string FormatChatRawEventMarkdown(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var builder = new StringBuilder()
            .AppendLine($"- Event: `{raw.BackendEventType}`");

        var payload = raw.Raw.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : raw.Raw.GetRawText();

        builder
            .AppendLine()
            .AppendLine("```json")
            .AppendLine(payload)
            .Append("```");

        return builder.ToString();
    }

    internal static bool ShouldDisplayActivity(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Kind == AgentActivityKind.Turn)
        {
            return false;
        }

        if (activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Subagent
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration)
        {
            return false;
        }

        return activity.Phase switch
        {
            AgentActivityPhase.Requested => false,
            _ => true,
        };
    }

    internal static bool ShouldDisplayRawEvent(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return false;
    }

    internal static bool ShouldDisplayCompletedContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        return completed.Kind switch
        {
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => !string.IsNullOrWhiteSpace(completed.Content),
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput or AgentContentKind.Notice => false,
            _ => true,
        };
    }

    internal static bool ShouldDisplayContentDelta(AgentContentDeltaEvent delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return delta.Kind switch
        {
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput or AgentContentKind.Notice => false,
            _ => true,
        };
    }

    internal static bool ShouldDisplaySessionUpdate(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Kind == AgentSessionUpdateKind.Warning;
    }

    internal static bool ShouldDisplayPermissionRequest(bool autoApproveEnabled)
        => !autoApproveEnabled;

    internal static bool ShouldDisplayInteraction(AgentInteractionEvent interaction, bool autoApproveEnabled)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (interaction.Kind == AgentInteractionKind.PermissionResolved && autoApproveEnabled)
        {
            return false;
        }

        return true;
    }

    internal static string FormatChatUserInputRequestMarkdown(AgentUserInputRequest request)
        => FormatChatUserInputRequestMarkdown(request, autoApprove: false);

    internal static string FormatChatUserInputRequestMarkdown(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder(
            autoApprove
                ? "_The agent asked a question. Auto-Approve will prefer continue/inspect-style choices or use a neutral fallback answer so the run can continue._"
                : "_The agent asked a question. Terminal question prompts are not implemented yet, so CodeAlta returns empty answers for now._");

        for (var index = 0; index < request.Form.Prompts.Count; index++)
        {
            var prompt = request.Form.Prompts[index];
            builder.AppendLine()
                .AppendLine()
                .Append("**Question ")
                .Append(index + 1)
                .Append("**");

            AppendBullet(builder, "Id", prompt.Id, code: true);
            if (!string.IsNullOrWhiteSpace(prompt.Header))
            {
                builder.AppendLine().Append("- Header: ").Append(prompt.Header);
            }

            builder.AppendLine().Append("- Question: ").Append(prompt.Question);

            if (prompt.Options is { Count: > 0 } options)
            {
                builder.AppendLine().AppendLine().Append("**Choices**");
                foreach (var option in options)
                {
                    builder.AppendLine().Append("- ").Append(option.Label);
                    if (!string.IsNullOrWhiteSpace(option.Description))
                    {
                        builder.Append(": ").Append(option.Description);
                    }
                }
            }

            builder.AppendLine()
                .Append("- Freeform: ")
                .Append(prompt.AllowFreeform ? "allowed" : "disabled");

            if (prompt.IsSecret)
            {
                builder.AppendLine().Append("- Input: secret");
            }
        }

        return builder.ToString();
    }

    internal static string FormatChatInteractionResolutionMarkdown(AgentInteractionEvent interaction, bool includeHeading)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var label = interaction.Kind switch
        {
            AgentInteractionKind.PermissionResolved => "Permission Resolved",
            AgentInteractionKind.UserInputResolved => "User Input Resolved",
            _ => interaction.Kind.ToString(),
        };
        var detailsMarkdown = BuildChatInteractionResolutionDetailsMarkdown(interaction);

        if (!includeHeading)
        {
            if (string.IsNullOrWhiteSpace(detailsMarkdown))
            {
                return string.IsNullOrWhiteSpace(interaction.Message)
                    ? "_Status:_ resolved"
                    : $"_Status:_ {interaction.Message}";
            }

            return string.IsNullOrWhiteSpace(interaction.Message)
                ? $"_Status:_ resolved\n\n{detailsMarkdown}"
                : $"_Status:_ {interaction.Message}\n\n{detailsMarkdown}";
        }

        if (string.IsNullOrWhiteSpace(interaction.Message))
        {
            return string.IsNullOrWhiteSpace(detailsMarkdown)
                ? $"**{NerdFont.CodArrowRight} {label}**"
                : $"**{NerdFont.CodArrowRight} {label}**\n\n{detailsMarkdown}";
        }

        return string.IsNullOrWhiteSpace(detailsMarkdown)
            ? $"**{NerdFont.CodArrowRight} {label}**\n\n{interaction.Message}"
            : $"**{NerdFont.CodArrowRight} {label}**\n\n{interaction.Message}\n\n{detailsMarkdown}";
    }

    internal static string FormatChatImmediatePermissionDecisionMarkdown(AgentPermissionDecision decision, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var reason = autoApprove
            ? "CodeAlta response: auto-approved this request."
            : "CodeAlta response: denied this request because interactive approval UI is not implemented yet.";
        return $"_Status:_ {reason}\n\n- Decision: {SplitPascalCase(decision.Kind.ToString())}";
    }

    internal static string FormatChatImmediateUserInputResponseMarkdown(AgentUserInputResponse response, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(response);

        var builder = new StringBuilder();
        builder.Append(
            autoApprove
                ? "_Status:_ CodeAlta auto-answered the question."
                : "_Status:_ CodeAlta returned an empty answer because terminal question prompts are not implemented yet.");

        foreach (var answer in response.Answers)
        {
            builder.AppendLine()
                .AppendLine()
                .Append("- `")
                .Append(answer.Key)
                .Append("`: ");
            if (string.IsNullOrWhiteSpace(answer.Value))
            {
                builder.Append("_empty_");
            }
            else
            {
                builder.Append('`').Append(answer.Value).Append('`');
            }
        }

        return builder.ToString();
    }

    private static string CreateChatContentKey(AgentContentKind kind, string contentId)
        => $"content:{kind}:{contentId}";

    private static ChatTimelineTone GetContentTone(AgentContentKind kind)
    {
        return kind switch
        {
            AgentContentKind.User => ChatTimelineTone.User,
            AgentContentKind.Assistant => ChatTimelineTone.Assistant,
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => ChatTimelineTone.Reasoning,
            AgentContentKind.Plan or AgentContentKind.Notice => ChatTimelineTone.Notice,
            _ => ChatTimelineTone.Activity,
        };
    }

    private static string? GetContentHeader(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.User => "User Prompt",
            AgentContentKind.Assistant => null,
            AgentContentKind.Reasoning => "Reasoning",
            AgentContentKind.ReasoningSummary => "Reasoning Summary",
            AgentContentKind.Plan => "Plan",
            AgentContentKind.CommandOutput => "Command Output",
            AgentContentKind.FileChangeOutput => "File Change Output",
            AgentContentKind.ToolOutput => "Tool Output",
            AgentContentKind.Notice => "Notice",
            _ => SplitPascalCase(kind.ToString()),
        };

    private static string TrimReasoningContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (TryExtractReasoningHeading(normalized, out _, out var remainder) && remainder is not null)
        {
            return remainder;
        }

        return normalized;
    }

    private static string? BuildReasoningSummary(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (TryExtractReasoningHeading(normalized, out var heading, out _))
        {
            return TrimSummaryText(heading);
        }

        var firstSentenceEnd = normalized.IndexOf('.');
        var firstLineEnd = normalized.IndexOf('\n');
        var end = firstSentenceEnd >= 0 && firstLineEnd >= 0
            ? Math.Min(firstSentenceEnd, firstLineEnd)
            : Math.Max(firstSentenceEnd, firstLineEnd);
        var summary = end >= 0 ? normalized[..end] : normalized;
        return TrimSummaryText(summary);
    }

    private static bool TryExtractReasoningHeading(string content, out string? heading, out string? remainder)
    {
        heading = null;
        remainder = null;
        var normalized = content.TrimStart();
        if (normalized.StartsWith("**", StringComparison.Ordinal))
        {
            var closingIndex = normalized.IndexOf("**", 2, StringComparison.Ordinal);
            if (closingIndex > 2)
            {
                heading = normalized[2..closingIndex].Trim();
                remainder = normalized[(closingIndex + 2)..].TrimStart('\n', '\r', ' ');
                return !string.IsNullOrWhiteSpace(heading);
            }
        }

        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            var lineEnd = normalized.IndexOf('\n');
            var line = (lineEnd >= 0 ? normalized[..lineEnd] : normalized).Trim();
            heading = line.TrimStart('#', ' ').Trim();
            remainder = lineEnd >= 0 ? normalized[(lineEnd + 1)..].TrimStart() : string.Empty;
            return !string.IsNullOrWhiteSpace(heading);
        }

        return false;
    }

    private static string? TrimSummaryText(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var normalized = summary.Trim();
        const int maxLength = 44;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }

    private static string FormatPlanStepStatus(AgentPlanStepStatus? status)
    {
        return status switch
        {
            AgentPlanStepStatus.Pending => "[ ] ",
            AgentPlanStepStatus.InProgress => "[~] ",
            AgentPlanStepStatus.Completed => "[x] ",
            _ => string.Empty,
        };
    }

    private static string GetActivityPhaseLabel(AgentActivityPhase phase)
    {
        return phase switch
        {
            AgentActivityPhase.Requested => "Requested",
            AgentActivityPhase.Started => "Started",
            AgentActivityPhase.Progressed => "In Progress",
            AgentActivityPhase.Completed => "Completed",
            AgentActivityPhase.Failed => "Failed",
            AgentActivityPhase.Canceled => "Canceled",
            AgentActivityPhase.Selected => "Selected",
            AgentActivityPhase.Deselected => "Deselected",
            _ => phase.ToString(),
        };
    }

    private static string GetActivityKindLabel(AgentActivityKind kind)
    {
        return kind switch
        {
            AgentActivityKind.Turn => "Turn",
            AgentActivityKind.ToolCall => "Tool Call",
            AgentActivityKind.CommandExecution => "Command Execution",
            AgentActivityKind.FileChange => "File Change",
            AgentActivityKind.McpToolCall => "MCP Tool Call",
            AgentActivityKind.DynamicToolCall => "Dynamic Tool Call",
            AgentActivityKind.CollabAgentToolCall => "Collab Agent Tool Call",
            AgentActivityKind.Subagent => "Subagent",
            AgentActivityKind.Hook => "Hook",
            AgentActivityKind.Skill => "Skill",
            AgentActivityKind.Compaction => "Compaction",
            AgentActivityKind.WebSearch => "Web Search",
            AgentActivityKind.ImageGeneration => "Image Generation",
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    private static string GetActivityHeadline(AgentActivityKind kind, AgentActivityPhase phase)
    {
        var label = GetActivityKindLabel(kind);
        return phase switch
        {
            AgentActivityPhase.Requested or AgentActivityPhase.Started => $"Calling {label}",
            AgentActivityPhase.Completed => $"{label} Result",
            AgentActivityPhase.Failed => $"{label} Failed",
            AgentActivityPhase.Canceled => $"{label} Canceled",
            AgentActivityPhase.Progressed => $"{label} Update",
            AgentActivityPhase.Selected => $"{label} Selected",
            AgentActivityPhase.Deselected => $"{label} Deselected",
            _ => $"{label} · {GetActivityPhaseLabel(phase)}",
        };
    }

    private static string SummarizeActivityMessage(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.Message))
        {
            return string.Empty;
        }

        var normalized = activity.Message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        var lineCount = normalized.Count(static ch => ch == '\n') + 1;
        var shouldCompact = activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.Subagent;

        if (shouldCompact && (normalized.Length > 220 || lineCount > 6))
        {
            var firstLine = normalized.Split('\n')[0].Trim();
            if (firstLine.Length > 120)
            {
                firstLine = firstLine[..117].TrimEnd() + "...";
            }

            return string.IsNullOrWhiteSpace(firstLine)
                ? $"Output omitted ({lineCount} lines, {normalized.Length} chars)."
                : $"{firstLine} _(output omitted: {lineCount} lines, {normalized.Length} chars)_";
        }

        return normalized;
    }

    private static string ToDisplayLabel(AgentCommandPreviewKind kind)
        => kind switch
        {
            AgentCommandPreviewKind.ListFiles => "List Files",
            _ => SplitPascalCase(kind.ToString()),
        };

    private static string FormatChatOutputMarkdown(string content)
        => string.IsNullOrWhiteSpace(content) ? string.Empty : FormatChatCodeFence(content, "text");

    private static string FormatChatCodeFence(string content, string language)
    {
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{language}\n{content}\n{fence}";
    }

    private static void AppendBullet(StringBuilder builder, string label, string? value, bool code = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine().Append("- ").Append(label).Append(": ");
        if (code)
        {
            builder.Append('`').Append(value).Append('`');
        }
        else
        {
            builder.Append(value);
        }
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (index > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    internal static AgentUserInputResponse CreateChatUserInputResponse(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = request.Form.Prompts.ToDictionary(
            static x => x.Id,
            prompt => ResolveChatPromptAnswer(prompt, autoApprove),
            StringComparer.Ordinal);

        return new AgentUserInputResponse(answers);
    }

    private static string ResolveChatPromptAnswer(AgentUserInputPrompt prompt, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!autoApprove)
        {
            return string.Empty;
        }

        if (prompt.Options is { Count: > 0 } options)
        {
            return SelectPreferredPromptOption(options, prompt.Question);
        }

        if (prompt.IsSecret)
        {
            return string.Empty;
        }

        return prompt.AllowFreeform
            ? "No preference. Use your best judgment and continue."
            : string.Empty;
    }

    private static string SelectPreferredPromptOption(IReadOnlyList<AgentUserInputOption> options, string? question)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Count == 0)
        {
            return string.Empty;
        }

        var bestIndex = 0;
        var bestScore = int.MinValue;
        for (var index = 0; index < options.Count; index++)
        {
            var score = ScorePromptOption(options[index].Label, question);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return options[bestIndex].Label;
    }

    private static int ScorePromptOption(string label, string? question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var normalizedLabel = label.Trim().ToLowerInvariant();
        var normalizedQuestion = question?.Trim().ToLowerInvariant() ?? string.Empty;
        var score = 0;

        score += ScoreOptionKeywords(
            normalizedLabel,
            "yes",
            "allow",
            "approve",
            "continue",
            "proceed",
            "go ahead",
            "run",
            "use",
            "look",
            "inspect",
            "search",
            "list",
            "read",
            "open",
            "explore",
            "summarize");

        score -= ScoreOptionKeywords(
            normalizedLabel,
            "no",
            "deny",
            "reject",
            "cancel",
            "abort",
            "stop",
            "don't",
            "do not",
            "never",
            "skip",
            "later",
            "different path",
            "specify a different path",
            "provide instructions",
            "inspect locally");

        if (normalizedQuestion.Contains("which option", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("how should i proceed", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("do you want me to", StringComparison.Ordinal))
        {
            score += ScoreOptionKeywords(normalizedLabel, "continue", "proceed", "look", "inspect", "search", "list", "use", "run");
            score -= ScoreOptionKeywords(normalizedLabel, "provide instructions", "different path", "stop", "cancel");
        }

        return score;
    }

    private static int ScoreOptionKeywords(string value, params string[] keywords)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (value.Contains(keyword, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }

    private static string BuildChatInteractionResolutionDetailsMarkdown(AgentInteractionEvent interaction)
    {
        if (interaction.Details is not { } details)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        switch (interaction.Kind)
        {
            case AgentInteractionKind.PermissionResolved:
                if (TryGetStringProperty(details, "decisionKind", out var decisionKind))
                {
                    builder.Append("- Decision: ").Append(SplitPascalCase(decisionKind!));
                }
                break;

            case AgentInteractionKind.UserInputResolved:
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("answers", out var answers) &&
                    answers.ValueKind == JsonValueKind.Object)
                {
                    var answerLines = new List<string>();
                    foreach (var answer in answers.EnumerateObject())
                    {
                        answerLines.Add(
                            string.IsNullOrWhiteSpace(answer.Value.GetString())
                                ? $"- `{answer.Name}`: _empty_"
                                : $"- `{answer.Name}`: `{answer.Value.GetString()}`");
                    }

                    if (answerLines.Count == 0)
                    {
                        builder.Append("- Answers: _empty_");
                    }
                    else
                    {
                        builder.Append(string.Join(Environment.NewLine, answerLines));
                    }

                    if (answerLines.Count > 0 && answerLines.All(static line => line.EndsWith("_empty_", StringComparison.Ordinal)))
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine();
                        }

                        builder.Append("- Note: Terminal question prompts are not implemented yet.");
                    }
                }
                break;
        }

        return builder.ToString();
    }

    private static T RunOnUiThread<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    private static T RunOnUiThread<TState, T>(Func<TState, T> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action(state)
            : dispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
    }

}
