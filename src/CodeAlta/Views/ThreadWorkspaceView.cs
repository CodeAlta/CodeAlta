using System.Diagnostics.CodeAnalysis;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class ThreadWorkspaceView
{
    private Markup? _statusIconVisual;
    private readonly Dictionary<string, TabPage> _tabPages = new(StringComparer.OrdinalIgnoreCase);

    public ThreadWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Func<Visual> buildSessionUsageIndicatorVisual,
        Action sendPrompt,
        Action steerPrompt,
        Action clearQueuedPrompts,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Action delegateThread,
        Action abortThread,
        Action closeTab,
        Action<int> onThreadTabSelectionChanged,
        Action<int> onChatBackendSelectionChanged,
        Action<int> onChatModelSelectionChanged,
        Action<int> onChatReasoningSelectionChanged,
        Action onAutoScrollChanged)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(buildSessionUsageIndicatorVisual);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(steerPrompt);
        ArgumentNullException.ThrowIfNull(clearQueuedPrompts);
        ArgumentNullException.ThrowIfNull(convertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(deleteQueuedPrompt);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptCount);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(delegateThread);
        ArgumentNullException.ThrowIfNull(abortThread);
        ArgumentNullException.ThrowIfNull(closeTab);
        ArgumentNullException.ThrowIfNull(onThreadTabSelectionChanged);
        ArgumentNullException.ThrowIfNull(onChatBackendSelectionChanged);
        ArgumentNullException.ThrowIfNull(onChatModelSelectionChanged);
        ArgumentNullException.ThrowIfNull(onChatReasoningSelectionChanged);
        ArgumentNullException.ThrowIfNull(onAutoScrollChanged);

        ThreadCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
        };

        ThreadTabControl = new TabControl()
            .Style(TabControlStyle.NoBorder);
        ThreadTabControl.RegisterDynamicUpdate(_ => onThreadTabSelectionChanged(ThreadTabControl.SelectedIndex));

        ThreadInput = CreatePromptEditor(
            promptComposerViewModel,
            sendPrompt,
            steerPrompt,
            clearQueuedPrompts,
            delegateThread,
            abortThread,
            closeTab);
        ThreadInput.RegisterDynamicUpdate(_ => ThreadInput.IsEnabled = promptComposerViewModel.IsEnabled);
        ThreadInputView = ThreadInput.Scrollable();

        SendPromptButton = new Button(new TextBlock($"{NerdFont.MdSend} Send"))
            .Click(sendPrompt);
        SendPromptButton.RegisterDynamicUpdate(_ => SendPromptButton.IsEnabled = promptComposerViewModel.CanSend);
        ChatBackendSelect = new Select<ChatBackendOption>()
            .SelectionChanged((_, e) => onChatBackendSelectionChanged(e.NewIndex))
            .MinWidth(14)
            .MaxWidth(22);
        ChatBackendSelect.RegisterDynamicUpdate(_ => ChatBackendSelect.IsEnabled = workspaceViewModel.CanSelectBackend);
        ChatModelSelect = new Select<ChatModelOption>()
            .SelectionChanged((_, e) => onChatModelSelectionChanged(e.NewIndex))
            .MinWidth(18)
            .MaxWidth(36);
        ChatModelSelect.RegisterDynamicUpdate(_ => ChatModelSelect.IsEnabled = workspaceViewModel.CanSelectModel);
        ChatReasoningSelect = new Select<ChatReasoningOption>()
            .SelectionChanged((_, e) => onChatReasoningSelectionChanged(e.NewIndex))
            .MinWidth(12)
            .MaxWidth(22);
        ChatReasoningSelect.RegisterDynamicUpdate(_ => ChatReasoningSelect.IsEnabled = workspaceViewModel.CanSelectReasoning);
        ChatAutoScrollCheckBox = new CheckBox("AutoScroll", isChecked: true);
        ChatAutoScrollCheckBox.RegisterDynamicUpdate(_ => onAutoScrollChanged());
        ChatAutoScrollCheckBox.RegisterDynamicUpdate(_ => ChatAutoScrollCheckBox.IsEnabled = workspaceViewModel.CanToggleAutoScroll);

        var statusSpinner = new Spinner().Style(SpinnerStyles.Arc);
        statusSpinner.IsActive(() => shellViewModel.StatusBusy);
        statusSpinner.IsVisible(() => shellViewModel.StatusBusy);

        var usageIndicator = buildSessionUsageIndicatorVisual();
        var statusPrefix = new Center(
            new ComputedVisual(
                () => shellViewModel.StatusBusy
                    ? statusSpinner
                    : _statusIconVisual ??= new Markup(() => shellViewModel.StatusIconMarkup)
                    {
                        Wrap = false,
                    }))
        {
            MinWidth = 2,
            MaxWidth = 2,
        };

        var statusLine = new HStack(
        [
            statusPrefix,
            new TextBlock
                {
                    Wrap = true,
                    IsSelectable = false,
                }.Text(() => shellViewModel.StatusText)
                .Style(() => StatusVisualFormatter.BuildStatusTextStyle(shellViewModel.StatusText, shellViewModel.StatusBusy, shellViewModel.StatusTone)),
        ])
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var queuedPromptList = new ComputedVisual(
            () => QueuedPromptListView.Build(
                workspaceViewModel,
                convertQueuedPromptToSteer,
                deleteQueuedPrompt,
                updateQueuedPromptCount,
                updateQueuedPromptText,
                CreateStyledPromptEditor));

        var selectionControls = new HStack(
        [
            SendPromptButton,
            ChatBackendSelect,
            ChatModelSelect,
            ChatReasoningSelect,
            ChatAutoScrollCheckBox,
        ])
        {
            Spacing = 2,
        };

        var selectionRight = new HStack(
        [
            new Markup(() => workspaceViewModel.BackendStatusMarkup)
            {
                Wrap = false,
            },
            usageIndicator,
        ])
        {
            Spacing = 2,
        };

        var selectionLine = new StatusBar()
            .LeftText(selectionControls)
            .RightText(selectionRight);

        ThreadBottomPanel = new DockLayout(
            top: new VStack([queuedPromptList, statusLine]) { Spacing = 0 },
            content: ThreadInputView,
            bottom: selectionLine)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        ThreadBodySplitter = new VSplitter(new TextBlock("Open or create a thread to start working."), ThreadBottomPanel)
        {
            Ratio = 0.75,
            MinFirst = 6,
            MinSecond = 7,
        };

        var threadPaneLayout = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        threadPaneLayout.Cell(ThreadTabControl.Stretch(), 0, 0);
        threadPaneLayout.Cell(ThreadBodySplitter, 1, 0);

        ThreadPaneLayout = threadPaneLayout;
        Root = ThreadPaneLayout;
    }

    public Visual Root { get; }

    public Visual ThreadPaneLayout { get; }

    public Visual ThreadBottomPanel { get; }

    public VSplitter ThreadBodySplitter { get; }

    public ChatPromptEditor ThreadInput { get; }

    public Visual ThreadInputView { get; }

    public Button SendPromptButton { get; }

    public CommandBar ThreadCommandBar { get; }

    public Select<ChatBackendOption> ChatBackendSelect { get; }

    public Select<ChatModelOption> ChatModelSelect { get; }

    public Select<ChatReasoningOption> ChatReasoningSelect { get; }

    public CheckBox ChatAutoScrollCheckBox { get; }

    public TabControl ThreadTabControl { get; }

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _tabPages.TryGetValue(tabId, out page);
    }

    public void RememberTabPage(string tabId, TabPage page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(page);
        _tabPages[tabId] = page;
    }

    public bool RemoveTabPage(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _tabPages.Remove(tabId);
    }

    private static ChatPromptEditor CreatePromptEditor(
        PromptComposerViewModel promptComposerViewModel,
        Action sendPrompt,
        Action steerPrompt,
        Action clearQueuedPrompts,
        Action delegateThread,
        Action abortThread,
        Action closeTab)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(steerPrompt);
        ArgumentNullException.ThrowIfNull(clearQueuedPrompts);
        ArgumentNullException.ThrowIfNull(delegateThread);
        ArgumentNullException.ThrowIfNull(abortThread);
        ArgumentNullException.ThrowIfNull(closeTab);

        var editor = CreateStyledPromptEditor(_ => sendPrompt(), placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder);

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Steer",
            LabelMarkup = "Steer",
            DescriptionMarkup = "Send an immediate steering instruction to the selected thread.",
            Gesture = new KeyGesture(TerminalKey.F5),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => steerPrompt(),
            CanExecute = _visual => promptComposerViewModel.CanSteer,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Delegate",
            LabelMarkup = "Delegate",
            DescriptionMarkup = "Create a delegated internal thread from the current project thread.",
            Gesture = new KeyGesture(TerminalKey.F7),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => delegateThread(),
            CanExecute = _visual => promptComposerViewModel.CanDelegate,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Abort",
            LabelMarkup = "Abort",
            DescriptionMarkup = "Abort the selected thread run.",
            Gesture = new KeyGesture(TerminalKey.F8),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => abortThread(),
            CanExecute = _visual => promptComposerViewModel.CanAbort,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.ClearQueue",
            LabelMarkup = "Clear Queue",
            DescriptionMarkup = "Clear all queued prompts for the selected thread.",
            Gesture = new KeyGesture(TerminalKey.F10),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => clearQueuedPrompts(),
            CanExecute = _visual => promptComposerViewModel.CanClearQueue,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.CloseTab",
            LabelMarkup = "Close Tab",
            DescriptionMarkup = "Close the current thread tab.",
            Gesture = new KeyGesture(TerminalKey.F9),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => closeTab(),
            CanExecute = _visual => promptComposerViewModel.CanCloseTab,
        });

        return editor;
    }

    internal static ChatPromptEditor CreateStyledPromptEditor(Action<string> onAccepted, string? placeholder)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);

        var converter = new MarkdownMarkupConverter();
        return new ChatPromptEditor(onAccepted)
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .Placeholder(placeholder)
            .EnterMode(PromptEditorEnterMode.EnterInsertsNewLine)
            .EnableWordHints(true)
            .Highlighter(HighlightMarkdown)
            .MinHeight(3)
            .Style(PromptEditorStyle.Default with
            {
                Padding = new Thickness(0, 0, 1, 0),
                PlaceholderForeground = UiPalette.PromptPlaceholderColor,
            });

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            converter.Theme = request.Theme;
            converter.Highlight(SnapshotToString(request.Snapshot), runs);
        }

        static string SnapshotToString(ITextSnapshot snapshot)
        {
            if (snapshot.Length == 0)
            {
                return string.Empty;
            }

            return string.Create(snapshot.Length, snapshot, static (span, s) => s.CopyTo(0, span));
        }
    }
}
