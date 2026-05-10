using CodeAlta.Threading;
using System.Globalization;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using CodeAlta.Views;
using ImageControl = XenoAtom.Terminal.UI.Graphics.Image;

namespace CodeAlta.Presentation.Timeline;

internal static class ChatTimelineVisualFactory
{
    public static PendingChatMessage CreatePendingChatMessage(string userMarkdown, string? localFileRootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMarkdown);

        var userItem = CreateUserChatItem(userMarkdown, localFileRootPath);
        var assistantItem = CreateAssistantStreamingChatItem(out var streamingMarkdown, out var timestampText, localFileRootPath);
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
        int maxCodeBlockHeight = 14,
        string? localFileRootPath = null,
        IReadOnlyList<PromptImageAttachmentReference>? imageAttachments = null,
        Func<Rectangle?>? getDialogBounds = null)
        => UiDispatch.InvokeCurrent(
            static state => CreateChatMarkdownItemCore(state.markdown, state.tone, state.headerOverride, state.headerSecondary, state.maxCodeBlockHeight, state.localFileRootPath, imageAttachments: state.imageAttachments, getDialogBounds: state.getDialogBounds),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight, localFileRootPath, imageAttachments, getDialogBounds));

    public static ChatMarkdownEntry CreateCollapsibleMarkdownItem(
        string markdown,
        string collapsibleHeader,
        string collapsibleMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14,
        string? localFileRootPath = null)
        => CreateCollapsibleMarkdownItem(
            markdown,
            [new ChatCollapsibleMarkdownSection(collapsibleHeader, collapsibleMarkdown)],
            tone,
            headerOverride,
            headerSecondary,
            maxCodeBlockHeight,
            localFileRootPath);

    public static ChatMarkdownEntry CreateCollapsibleMarkdownItem(
        string markdown,
        IReadOnlyList<ChatCollapsibleMarkdownSection> collapsibleSections,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14,
        string? localFileRootPath = null)
        => UiDispatch.InvokeCurrent(
            static state => CreateChatMarkdownItemCore(
                state.markdown,
                state.tone,
                state.headerOverride,
                state.headerSecondary,
                state.maxCodeBlockHeight,
                state.localFileRootPath,
                state.collapsibleSections),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight, localFileRootPath, collapsibleSections));

    public static ChatMarkdownEntry CreateVisualItem(
        string markdown,
        Func<Visual> contentVisualFactory,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14,
        string? localFileRootPath = null,
        IReadOnlyList<ChatCollapsibleMarkdownSection>? copyDetailSections = null)
    {
        ArgumentNullException.ThrowIfNull(contentVisualFactory);
        return UiDispatch.InvokeCurrent(
            static state => CreateChatMarkdownItemCore(
                state.markdown,
                state.tone,
                state.headerOverride,
                state.headerSecondary,
                state.maxCodeBlockHeight,
                state.localFileRootPath,
                state.copyDetailSections,
                contentVisualFactory: state.contentVisualFactory,
                useContentVisualOnly: true),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight, localFileRootPath, copyDetailSections, contentVisualFactory));
    }

    public static MarkdownRenderOptions CreateThreadMarkdownOptions(int maxCodeBlockHeight, string? localFileRootPath = null)
        => MarkdownRenderOptions.Default with
        {
            WrapCodeBlocks = true,
            MaxCodeBlockHeight = maxCodeBlockHeight,
            LocalFileRootPath = localFileRootPath,
        };

    public static void ApplyLocalFileRootPath(MarkdownControl markdownControl, string? localFileRootPath)
    {
        ArgumentNullException.ThrowIfNull(markdownControl);
        markdownControl.Options = markdownControl.Options with
        {
            LocalFileRootPath = localFileRootPath,
        };
    }

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

    private static DocumentFlowItem CreateUserChatItem(string markdown, string? localFileRootPath)
        => CreateMarkdownItem(
            markdown,
            ChatTimelineTone.User,
            headerOverride: "User Prompt",
            maxCodeBlockHeight: 10,
            localFileRootPath: localFileRootPath).Item;

    private static DocumentFlowItem CreateUserPromptSeparatorItem()
        => UiDispatch.InvokeCurrent(
            static () => new DocumentFlowItem
            {
                Content = new FlowDocument().Add(new Rule()),
                Alignment = DocumentFlowAlignment.Stretch,
                Padding = new Thickness(0, 1, 0, 0),
            });

    private static DocumentFlowItem CreateAssistantStreamingChatItem(out MarkdownControl markdownControl, out Markup timestampText, string? localFileRootPath)
    {
        var entry = CreateMarkdownItem(string.Empty, ChatTimelineTone.Assistant, localFileRootPath: localFileRootPath);
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
        int maxCodeBlockHeight,
        string? localFileRootPath,
        IReadOnlyList<ChatCollapsibleMarkdownSection>? collapsibleSections = null,
        Func<Visual>? contentVisualFactory = null,
        bool useContentVisualOnly = false,
        IReadOnlyList<PromptImageAttachmentReference>? imageAttachments = null,
        Func<Rectangle?>? getDialogBounds = null)
    {
        var headerText = CreateChatCardHeader(tone, headerOverride, headerSecondary);
        markdown = markdown.Trim();
        var markdownControl = new MarkdownControl(markdown)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Options = CreateThreadMarkdownOptions(maxCodeBlockHeight, localFileRootPath),
        };

        var timestampText = new Markup(string.Empty);

        var contentItems = new List<Visual>();
        if (useContentVisualOnly)
        {
            if (contentVisualFactory is null)
            {
                throw new ArgumentException("A content visual factory is required when visual-only rendering is requested.", nameof(contentVisualFactory));
            }

            contentItems.Add(contentVisualFactory());
        }
        else
        {
            contentItems.Add(markdownControl);
        }
        var detailMarkdownControls = new List<MarkdownControl>();
        var detailMarkdownSectionIndexes = new List<int>();
        var copyDetailMarkdownControls = new List<MarkdownControl?>();
        if (!useContentVisualOnly && imageAttachments is { Count: > 0 })
        {
            contentItems.Add(CreateImageAttachmentStrip(imageAttachments, getDialogBounds));
        }

        if (!useContentVisualOnly && collapsibleSections is { Count: > 0 })
        {
            for (var sectionIndex = 0; sectionIndex < collapsibleSections.Count; sectionIndex++)
            {
                var section = collapsibleSections[sectionIndex];
                if (string.IsNullOrWhiteSpace(section.Header) || string.IsNullOrWhiteSpace(section.Markdown))
                {
                    copyDetailMarkdownControls.Add(null);
                    continue;
                }

                var sectionMarkdown = section.Markdown.Trim();
                Visual sectionContent;
                if (section.VisualFactory is not null)
                {
                    sectionContent = section.VisualFactory();
                    copyDetailMarkdownControls.Add(null);
                }
                else
                {
                    var detailsMarkdown = new MarkdownControl(sectionMarkdown)
                    {
                        HorizontalAlignment = Align.Stretch,
                        VerticalAlignment = Align.Start,
                        Options = CreateThreadMarkdownOptions(maxCodeBlockHeight, localFileRootPath),
                    };
                    detailMarkdownControls.Add(detailsMarkdown);
                    detailMarkdownSectionIndexes.Add(sectionIndex);
                    copyDetailMarkdownControls.Add(detailsMarkdown);
                    sectionContent = detailsMarkdown;
                }

                contentItems.Add(new Collapsible()
                    .Header(CreateCollapsibleHeader(section))
                    .Content(sectionContent)
                    .IsExpanded(false));
            }
        }

        var copyState = new ChatMarkdownCopyState { DetailSections = collapsibleSections };
        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy}"));
        copyButton.Click(() => copyButton.App?.Terminal.Clipboard.TrySetText(BuildCopyMarkdown(markdownControl.Markdown ?? string.Empty, ResolveCurrentCopySections(copyState.DetailSections, copyDetailMarkdownControls))));

        Visual groupContent = contentItems.Count == 1
            ? contentItems[0]
            : new VStack(contentItems.ToArray()).Spacing(GetContentStackSpacing(contentItems));

        var group = new Group(headerText, groupContent)
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
            headerText)
        {
            DetailMarkdownControls = detailMarkdownControls,
            DetailMarkdownSectionIndexes = detailMarkdownSectionIndexes,
            CopyState = copyState,
        };
    }

    private static int GetContentStackSpacing(IReadOnlyList<Visual> contentItems)
    {
        if (contentItems.Count > 1 && contentItems.Skip(1).All(static item => item is Collapsible))
        {
            return 0;
        }

        return 1;
    }

    private static Visual CreateCollapsibleHeader(ChatCollapsibleMarkdownSection section)
        => section.HeaderVisualFactory?.Invoke() ?? new Markup(AnsiMarkup.Escape(section.Header.Trim()))
        {
            Wrap = false,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
        };

    internal static string BuildCopyMarkdown(string markdown, IReadOnlyList<ChatCollapsibleMarkdownSection>? collapsibleSections = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (collapsibleSections is not { Count: > 0 })
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.TrimEnd());
        foreach (var section in collapsibleSections)
        {
            if (string.IsNullOrWhiteSpace(section.Header) || string.IsNullOrWhiteSpace(section.Markdown))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append("## ")
                .AppendLine(section.Header.Trim())
                .AppendLine()
                .Append(section.Markdown.Trim());
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ChatCollapsibleMarkdownSection>? ResolveCurrentCopySections(
        IReadOnlyList<ChatCollapsibleMarkdownSection>? sections,
        IReadOnlyList<MarkdownControl?> detailMarkdownControls)
    {
        if (sections is not { Count: > 0 })
        {
            return sections;
        }

        if (detailMarkdownControls.Count == 0)
        {
            return sections;
        }

        var resolved = new ChatCollapsibleMarkdownSection[sections.Count];
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var currentMarkdown = index < detailMarkdownControls.Count
                ? detailMarkdownControls[index]?.Markdown ?? section.Markdown
                : section.Markdown;
            resolved[index] = section with { Markdown = currentMarkdown };
        }

        return resolved;
    }

    private static Visual CreateImageAttachmentStrip(
        IReadOnlyList<PromptImageAttachmentReference> imageAttachments,
        Func<Rectangle?>? getDialogBounds)
    {
        var children = new List<Visual>(imageAttachments.Count + 1)
        {
            new Markup($"[dim]Images ({imageAttachments.Count})[/]") { Wrap = false },
        };
        foreach (var attachment in imageAttachments)
        {
            var current = attachment;
            var button = new Button(new TextBlock($"▧ {current.Title}") { Wrap = false });
            button.Click(() => OpenImageAttachmentDialog(current, button, getDialogBounds));
            children.Add(button.Tooltip(new TextBlock($"Open attached image {current.Title}")));
        }

        return new HStack([.. children]) { Spacing = 1, HorizontalAlignment = Align.Stretch };
    }

    private static void OpenImageAttachmentDialog(
        PromptImageAttachmentReference attachment,
        Button? anchor,
        Func<Rectangle?>? getDialogBounds)
    {
        Dialog? dialog = null;
        var bounds = getDialogBounds?.Invoke() ?? anchor?.GetAbsoluteBounds();
        var size = ResponsiveDialogSize.Resolve(bounds, minWidth: 64, minHeight: 20, widthFactor: 0.8, heightFactor: 0.8);
        var preview = CreateImageAttachmentPreview(attachment, Math.Max(24, size.Width - 6), Math.Max(8, size.Height - 8));
        var closeButton = new Button(new TextBlock("Close")) { HorizontalAlignment = Align.End };
        closeButton.Click(() => dialog?.Close());
        var bottom = CreateImageAttachmentDialogBottom(attachment, closeButton);
        dialog = new Dialog()
            .Title(attachment.Title)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(new DockLayout(top: null, content: new Border(preview).Padding(1), bottom: bottom));
        dialog.Width(size.Width).Height(size.Height).MinWidth(64).MinHeight(20);
        dialog.AddCommand(new Command { Id = "CodeAlta.Timeline.Image.Close", LabelMarkup = "Close", DescriptionMarkup = "Close image preview.", Gesture = new KeyGesture(TerminalKey.Escape), Importance = CommandImportance.Primary, Execute = _ => dialog?.Close() });
        dialog.Show();
    }

    internal static Visual CreateImageAttachmentDialogBottom(PromptImageAttachmentReference attachment, Button closeButton)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(closeButton);

        return new VStack(
            new TextBlock($"Path: {attachment.Path}")
            {
                Wrap = true,
                HorizontalAlignment = Align.Stretch,
            },
            closeButton)
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };
    }

    private static Visual CreateImageAttachmentPreview(PromptImageAttachmentReference attachment, int cellWidth, int cellHeight)
    {
        var fallback = new Border(new VStack(
            new TextBlock(attachment.Title) { Wrap = false },
            new TextBlock($"{attachment.MediaType} · {attachment.Path}") { Wrap = true }))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        if (string.IsNullOrWhiteSpace(attachment.Path) || !File.Exists(attachment.Path))
        {
            return fallback;
        }

        return new ImageControl(TerminalImageSource.FromFile(attachment.Path))
        {
            CellWidth = cellWidth,
            CellHeight = cellHeight,
            ScaleMode = ImageScaleMode.Fit,
            PreserveAspectRatio = true,
            AccessibilityText = attachment.Title,
            FallbackContent = fallback,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
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
