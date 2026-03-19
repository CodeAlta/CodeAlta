using System.Globalization;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

internal static class ChatTimelineVisualFactory
{
    public static PendingChatMessage CreatePendingChatMessage(string userMarkdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMarkdown);

        var userItem = CreateUserChatItem(userMarkdown);
        var assistantItem = CreateAssistantStreamingChatItem(out var streamingMarkdown, out var timestampText);
        return new PendingChatMessage(userItem, assistantItem, streamingMarkdown, timestampText);
    }

    public static IReadOnlyList<DocumentFlowItem> BuildUserPromptTimelineItems(DocumentFlowItem userItem, bool hasSeenUserPrompt)
    {
        return hasSeenUserPrompt
            ? [CreateUserPromptSeparatorItem(), userItem]
            : [userItem];
    }

    public static string BuildTruncatedHistoryLoadButtonText(int omittedMessageCount)
        => omittedMessageCount == 1
            ? "Load 1 previous message"
            : $"Load {omittedMessageCount} previous messages";

    public static string BuildTruncatedHistorySummaryText(int omittedMessageCount)
        => omittedMessageCount == 1
            ? "1 previous message..."
            : $"{omittedMessageCount} previous messages...";

    public static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static void ApplyTimestamp(Markup timestampText, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestampText);

        UiDispatch.InvokeCurrent(
            static state =>
            {
                state.timestampText.Text = $"[dim]{FormatTimestamp(state.timestamp)}[/]";
                return 0;
            },
            (timestampText, timestamp));
    }

    public static void ApplyHeader(Markup headerText, ChatTimelineTone tone, string? headerOverride, string? headerSecondary)
    {
        ArgumentNullException.ThrowIfNull(headerText);

        UiDispatch.InvokeCurrent(
            static state =>
            {
                state.headerText.Text = CreateChatCardHeader(state.tone, state.headerOverride, state.headerSecondary).Text;
                return 0;
            },
            (headerText, tone, headerOverride, headerSecondary));
    }

    public static ChatMarkdownEntry CreateMarkdownItem(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14)
        => UiDispatch.InvokeCurrent(
            static state => CreateChatMarkdownItemCore(state.markdown, state.tone, state.headerOverride, state.headerSecondary, state.maxCodeBlockHeight),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight));

    public static string CreateContentKey(AgentContentKind kind, string contentId)
        => $"content:{kind}:{contentId}";

    public static ChatTimelineTone GetContentTone(AgentContentKind kind)
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

    public static string? GetContentHeader(AgentContentKind kind)
    {
        return kind switch
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
    }

    public static Action CreateDeferredUiAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return () => UiDispatch.PostCurrentDeferred(action);
    }

    private static DocumentFlowItem CreateUserChatItem(string markdown)
        => CreateMarkdownItem(
            markdown,
            ChatTimelineTone.User,
            headerOverride: "User Prompt",
            maxCodeBlockHeight: 10).Item;

    private static DocumentFlowItem CreateUserPromptSeparatorItem()
        => UiDispatch.InvokeCurrent(
            static () => new DocumentFlowItem
            {
                Content = new FlowDocument().Add(new Rule()),
                Alignment = DocumentFlowAlignment.Stretch,
                Padding = new Thickness(0, 1, 0, 0),
            });

    private static DocumentFlowItem CreateAssistantStreamingChatItem(out MarkdownControl markdownControl, out Markup timestampText)
    {
        var entry = CreateMarkdownItem(string.Empty, ChatTimelineTone.Assistant);
        markdownControl = entry.Markdown;
        timestampText = entry.TimestampText;
        return entry.Item;
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
    {
        return tone switch
        {
            ChatTimelineTone.User => ($"{NerdFont.MdAccount}", "User Prompt", "accent"),
            ChatTimelineTone.Assistant => ($"{NerdFont.MdRobot}", "Assistant", "success"),
            ChatTimelineTone.Reasoning => ($"{NerdFont.CodLightbulb}", "Reasoning", "primary"),
            ChatTimelineTone.Activity => ($"{NerdFont.CodTools}", "Activity", "muted"),
            ChatTimelineTone.Notice => ($"{NerdFont.CodInfo}", "Notice", "success"),
            ChatTimelineTone.Interaction => ($"{NerdFont.CodLock}", "Action Required", "warning"),
            _ => ($"{NerdFont.MdMessageText}", "Message", "primary"),
        };
    }

    private static ChatMarkdownEntry CreateChatMarkdownItemCore(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride,
        string? headerSecondary,
        int maxCodeBlockHeight)
    {
        var headerText = CreateChatCardHeader(tone, headerOverride, headerSecondary);
        markdown = markdown.Trim();
        var markdownControl = new MarkdownControl(markdown)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = maxCodeBlockHeight,
            },
        };

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy}"))
            .Click(() => markdownControl.App?.Terminal.Clipboard.TrySetText(markdownControl.Markdown ?? string.Empty));

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

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
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
}
