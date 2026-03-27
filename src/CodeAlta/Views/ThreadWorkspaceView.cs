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
    private Dialog? _expandedPromptDialog;

    internal const TerminalKey ExpandPromptShortcutKey = TerminalKey.F6;

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
        Action compactThread,
        Action closeTab,
        Action<int> onChatBackendSelectionChanged,
        Action<int> onChatModelSelectionChanged,
        Action<int> onChatReasoningSelectionChanged,
        Action<int> onSelectedTabChanged,
        Binding<string?> promptText,
        State<float> thinkingAnimationPhase01,
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
        ArgumentNullException.ThrowIfNull(compactThread);
        ArgumentNullException.ThrowIfNull(closeTab);
        ArgumentNullException.ThrowIfNull(onChatBackendSelectionChanged);
        ArgumentNullException.ThrowIfNull(onChatModelSelectionChanged);
        ArgumentNullException.ThrowIfNull(onChatReasoningSelectionChanged);
        ArgumentNullException.ThrowIfNull(onSelectedTabChanged);
        ArgumentNullException.ThrowIfNull(thinkingAnimationPhase01);
        ArgumentNullException.ThrowIfNull(onAutoScrollChanged);

        ThreadCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
        };

        ThreadTabControl = new TabControl()
            .Style(TabControlStyle.NoBorder)
            .SelectedIndex(workspaceViewModel.Bind.SelectedTabIndex);

        ThreadInput = CreatePromptEditor(
            promptComposerViewModel,
            sendPrompt,
            () => OpenExpandedPromptDialog(promptComposerViewModel, promptText),
            steerPrompt,
            clearQueuedPrompts,
            delegateThread,
            abortThread,
            compactThread,
            closeTab,
            promptText)
            .IsEnabled(promptComposerViewModel.Bind.IsEnabled);
        ThreadInputView = ThreadInput.Scrollable();

        SendPromptButton = CreateIconButton(
                $"{NerdFont.MdSend}",
                "Send the current prompt.",
                sendPrompt,
                button => button.IsEnabled(promptComposerViewModel.Bind.CanSend));
        ExpandPromptButton = CreateIconButton(
                $"{NerdFont.MdSquareEditOutline}",
                "Open the current prompt in a large editor window (F6).",
                () => OpenExpandedPromptDialog(promptComposerViewModel, promptText),
                button => button.IsEnabled(promptComposerViewModel.Bind.IsEnabled));
        ChatBackendSelect = new Select<ChatBackendOption>()
            .SelectionChanged((_, e) => onChatBackendSelectionChanged(e.NewIndex))
            .MinWidth(14)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectBackend);
        ChatModelSelect = new Select<ChatModelOption>()
            .SelectionChanged((_, e) => onChatModelSelectionChanged(e.NewIndex))
            .MinWidth(18)
            .MaxWidth(36)
            .IsEnabled(workspaceViewModel.Bind.CanSelectModel);
        ChatReasoningSelect = new Select<ChatReasoningOption>()
            .SelectionChanged((_, e) => onChatReasoningSelectionChanged(e.NewIndex))
            .MinWidth(12)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectReasoning);
        ChatAutoScrollSwitch = new Switch(new TextBlock("AutoScroll") { Wrap = false, IsSelectable = false })
            .IsOn(workspaceViewModel.Bind.AutoScroll)
            .IsEnabled(workspaceViewModel.Bind.CanToggleAutoScroll)
            .Toggled(onAutoScrollChanged);
        AlwaysEnqueueSwitch = new Switch(new TextBlock("AlwaysQueue") { Wrap = false, IsSelectable = false })
            .IsOn(promptComposerViewModel.Bind.AlwaysEnqueue)
            .IsEnabled(promptComposerViewModel.Bind.CanAlwaysEnqueue);
        var compactThreadButton = CreateIconButton(
                $"{NerdFont.MdSelectCompare}",
                "Compact the selected thread session when it is idle (F11).",
                compactThread,
                button => button.IsEnabled(promptComposerViewModel.Bind.CanCompact));

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
                .Style(() => StatusVisualFormatter.BuildStatusTextStyle(shellViewModel.StatusText, shellViewModel.StatusBusy, shellViewModel.StatusTone, thinkingAnimationPhase01.Value)),
        ])
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var queuedPromptList = new ComputedVisual(
            () =>
                QueuedPromptListView.Build(
                    workspaceViewModel.QueuedPrompts,
                    markdown => (ThreadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
                    convertQueuedPromptToSteer,
                    deleteQueuedPrompt,
                    updateQueuedPromptCount,
                    updateQueuedPromptText,
                    CreateStyledPromptEditor));

        var selectionControls = new HStack(
        [
            SendPromptButton,
            ExpandPromptButton,
            ChatBackendSelect,
            ChatModelSelect,
            ChatReasoningSelect,
            compactThreadButton,
            ChatAutoScrollSwitch,
            AlwaysEnqueueSwitch,
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
        Root = new ZStack(
            ThreadPaneLayout,
            new BindableObserver<int>(
                () => workspaceViewModel.SelectedTabIndex,
                onSelectedTabChanged));
    }

    public Visual Root { get; }

    public Visual ThreadPaneLayout { get; }

    public Visual ThreadBottomPanel { get; }

    public VSplitter ThreadBodySplitter { get; }

    public ChatPromptEditor ThreadInput { get; }

    public Visual ThreadInputView { get; }

    public Visual SendPromptButton { get; }

    public Visual ExpandPromptButton { get; }

    public CommandBar ThreadCommandBar { get; }

    public Select<ChatBackendOption> ChatBackendSelect { get; }

    public Select<ChatModelOption> ChatModelSelect { get; }

    public Select<ChatReasoningOption> ChatReasoningSelect { get; }

    public Switch ChatAutoScrollSwitch { get; }

    public Switch AlwaysEnqueueSwitch { get; }

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
        Action openExpandedPromptEditor,
        Action steerPrompt,
        Action clearQueuedPrompts,
        Action delegateThread,
        Action abortThread,
        Action compactThread,
        Action closeTab,
        Binding<string?> promptText)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);
        ArgumentNullException.ThrowIfNull(steerPrompt);
        ArgumentNullException.ThrowIfNull(clearQueuedPrompts);
        ArgumentNullException.ThrowIfNull(delegateThread);
        ArgumentNullException.ThrowIfNull(abortThread);
        ArgumentNullException.ThrowIfNull(compactThread);
        ArgumentNullException.ThrowIfNull(closeTab);
        var editor = CreateStyledPromptEditor(_ => sendPrompt(), placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder)
            .Text(promptText);

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.ExpandPrompt",
            LabelMarkup = "Full Prompt",
            DescriptionMarkup = "Open the current prompt in a large editor window. Escape closes the window and keeps the draft.",
            Gesture = new KeyGesture(ExpandPromptShortcutKey),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => openExpandedPromptEditor(),
            CanExecute = _visual => promptComposerViewModel.IsEnabled,
        });

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
            Id = "CodeAlta.Thread.Compact",
            LabelMarkup = "Compact",
            DescriptionMarkup = "Compact the selected thread session when it is idle.",
            Gesture = new KeyGesture(TerminalKey.F11),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => compactThread(),
            CanExecute = _visual => promptComposerViewModel.CanCompact,
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

    private void OpenExpandedPromptDialog(
        PromptComposerViewModel promptComposerViewModel,
        Binding<string?> promptText)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        if (_expandedPromptDialog is { App: not null })
        {
            return;
        }

        var editor = CreateStyledPromptEditor(_ => { }, placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder)
            .Text(promptText)
            .MinHeight(12)
            .IsEnabled(promptComposerViewModel.Bind.IsEnabled);
        editor.AddCommand(CreateExpandedPromptDialogCloseCommand());

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Error,
        };
        closeButton.Click(CloseExpandedPromptDialog);

        var dialog = new Dialog()
            .Title("Edit Prompt")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close · draft preserved[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(editor.Scrollable());
        ResponsiveDialogSize.Apply(dialog, ThreadPaneLayout.GetAbsoluteBounds(), minWidth: 60, minHeight: 18);
        dialog.AddCommand(CreateExpandedPromptDialogCloseCommand());

        _expandedPromptDialog = dialog;
        dialog.Show();
        dialog.App?.Focus(editor);

        Command CreateExpandedPromptDialogCloseCommand()
            => new()
            {
                Id = "CodeAlta.Thread.ExpandPrompt.Close",
                LabelMarkup = "Close",
                DescriptionMarkup = "Close the large prompt editor and keep the current draft.",
                Gesture = new KeyGesture(TerminalKey.Escape),
                Importance = CommandImportance.Primary,
                Execute = _ => CloseExpandedPromptDialog(),
            };
    }

    private void CloseExpandedPromptDialog()
    {
        var dialog = _expandedPromptDialog;
        _expandedPromptDialog = null;
        var app = dialog?.App ?? ThreadPaneLayout.App;
        dialog?.Close();
        app?.Focus(ThreadInput);
    }

    private static Visual CreateIconButton(string icon, string tooltipText, Action onClick, Action<Button>? configureButton = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltipText);
        ArgumentNullException.ThrowIfNull(onClick);

        var button = new Button(new TextBlock(icon) { Wrap = false, IsSelectable = false })
            .Click(onClick);
        configureButton?.Invoke(button);
        return button.Tooltip(new TextBlock(tooltipText));
    }

    internal static ChatPromptEditor CreateStyledPromptEditor(Action<string> onAccepted, string? placeholder)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);

        var converter = new MarkdownMarkupConverter();
        ITextSnapshot? cachedSnapshot = null;
        Theme? cachedTheme = null;
        string? cachedText = null;
        List<StyledRun>? cachedRuns = null;
        return new ChatPromptEditor(onAccepted)
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .Placeholder(placeholder)
            .EscapeBehavior(PromptEditorEscapeBehavior.CancelCompletionOnly)
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
            if (cachedRuns is not null &&
                ReferenceEquals(cachedSnapshot, request.Snapshot) &&
                Equals(cachedTheme, request.Theme))
            {
                runs.AddRange(cachedRuns);
                return;
            }

            var text = SnapshotToString(request.Snapshot);
            if (cachedRuns is not null &&
                string.Equals(cachedText, text, StringComparison.Ordinal) &&
                Equals(cachedTheme, request.Theme))
            {
                cachedSnapshot = request.Snapshot;
                runs.AddRange(cachedRuns);
                return;
            }

            converter.Theme = request.Theme;
            converter.Highlight(text, runs);
            cachedSnapshot = request.Snapshot;
            cachedTheme = request.Theme;
            cachedText = text;
            cachedRuns = [.. runs];
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
