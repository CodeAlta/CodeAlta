using System.Diagnostics.CodeAnalysis;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Styling;
using CodeAlta.Search;
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
    private readonly State<int> _activeTabContentVersion = new(0);
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Binding<string?> _promptTextBinding;
    private readonly Action _openHelp;
    private readonly Action _openCommandPalette;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Func<string?> _getPromptReferenceProjectRoot;
    private Dialog? _expandedPromptDialog;
    private Visual? _activeTabContent;

    internal const TerminalKey ExpandPromptShortcutKey = TerminalKey.F6;
    internal static readonly KeySequence SessionUsageShortcutSequence = ShellCommandCatalog.SessionUsageShortcutSequence;
    internal static readonly KeySequence ThreadInfoShortcutSequence = ShellCommandCatalog.ThreadInfoShortcutSequence;

    public ThreadWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        IReadOnlyList<ThreadWorkspaceCommandBinding> commandBindings,
        Func<Visual> buildSessionUsageIndicatorVisual,
        Action openSessionUsagePopup,
        Action<Visual> toggleThreadInfoPopup,
        Action openHelp,
        Action openCommandPalette,
        Action<string> acceptPrompt,
        Action sendPrompt,
        Action steerPrompt,
        Action clearQueuedPrompts,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deletePendingSteer,
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
        : this(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            commandBindings,
            buildSessionUsageIndicatorVisual,
            openSessionUsagePopup,
            toggleThreadInfoPopup,
            openHelp,
            openCommandPalette,
            NullProjectFileSearchService.Instance,
            static () => null,
            acceptPrompt,
            sendPrompt,
            steerPrompt,
            clearQueuedPrompts,
            convertQueuedPromptToSteer,
            deletePendingSteer,
            deleteQueuedPrompt,
            updateQueuedPromptCount,
            updateQueuedPromptText,
            delegateThread,
            abortThread,
            compactThread,
            closeTab,
            onChatBackendSelectionChanged,
            onChatModelSelectionChanged,
            onChatReasoningSelectionChanged,
            onSelectedTabChanged,
            promptText,
            thinkingAnimationPhase01,
            onAutoScrollChanged)
    {
    }

    public ThreadWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        IReadOnlyList<ThreadWorkspaceCommandBinding> commandBindings,
        Func<Visual> buildSessionUsageIndicatorVisual,
        Action openSessionUsagePopup,
        Action<Visual> toggleThreadInfoPopup,
        Action openHelp,
        Action openCommandPalette,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        Action<string> acceptPrompt,
        Action sendPrompt,
        Action steerPrompt,
        Action clearQueuedPrompts,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deletePendingSteer,
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
        ArgumentNullException.ThrowIfNull(commandBindings);
        ArgumentNullException.ThrowIfNull(buildSessionUsageIndicatorVisual);
        ArgumentNullException.ThrowIfNull(openSessionUsagePopup);
        ArgumentNullException.ThrowIfNull(toggleThreadInfoPopup);
        ArgumentNullException.ThrowIfNull(openHelp);
        ArgumentNullException.ThrowIfNull(openCommandPalette);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(acceptPrompt);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(steerPrompt);
        ArgumentNullException.ThrowIfNull(clearQueuedPrompts);
        ArgumentNullException.ThrowIfNull(convertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(deletePendingSteer);
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

        _promptComposerViewModel = promptComposerViewModel;
        _promptTextBinding = promptText;
        _openHelp = openHelp;
        _openCommandPalette = openCommandPalette;
        _projectFileSearchService = projectFileSearchService;
        _getPromptReferenceProjectRoot = getPromptReferenceProjectRoot;

        ThreadCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
            MultiLine = true,
        };

        ThreadTabControl = new TabControl();
        ThreadTabControl.KeyDown((_, _) => ThreadTabControl.Dispatcher.Post(() => onSelectedTabChanged(ThreadTabControl.SelectedIndex)));
        ThreadTabControl.PointerReleased((_, _) => ThreadTabControl.Dispatcher.Post(() => onSelectedTabChanged(ThreadTabControl.SelectedIndex)));

        Visual? threadInfoButton = null;
        ThreadInput = CreatePromptEditor(
            promptComposerViewModel,
            openHelp,
            openCommandPalette,
            projectFileSearchService,
            getPromptReferenceProjectRoot,
            acceptPrompt,
            commandBindings,
            promptText)
            .IsEnabled(promptComposerViewModel.Bind.IsEnabled);
        ThreadInputView = ThreadInput.Scrollable();

        SendPromptButton = CreatePromptActionButton(promptComposerViewModel, sendPrompt, abortThread);
        ExpandPromptButton = CreateIconButton(
                $"{NerdFont.MdSquareEditOutline}",
                "Open the current prompt in a large editor window (F6).",
                () => OpenExpandedPromptDialog(promptComposerViewModel, promptText),
                button => button.IsEnabled(promptComposerViewModel.Bind.IsEnabled));
        threadInfoButton = CreateIconButton(
                $"{NerdFont.MdInformationOutline}",
                $"Show information about the selected thread ({ThreadInfoShortcutSequence}).",
                () => toggleThreadInfoPopup(threadInfoButton!),
                button => button.IsEnabled(workspaceViewModel.Bind.CanShowThreadInfo));
        ChatBackendSelect = new Select<ChatBackendOption>()
            .SelectionChanged((_, e) => onChatBackendSelectionChanged(e.NewIndex))
            .SelectedIndex(workspaceViewModel.Bind.SelectedBackendIndex)
            .MinWidth(14)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectBackend);
        ChatModelSelect = new Select<ChatModelOption>()
            .SelectionChanged((_, e) => onChatModelSelectionChanged(e.NewIndex))
            .SelectedIndex(workspaceViewModel.Bind.SelectedModelIndex)
            .MinWidth(18)
            .MaxWidth(36)
            .IsEnabled(workspaceViewModel.Bind.CanSelectModel);
        ChatReasoningSelect = new Select<ChatReasoningOption>()
            .SelectionChanged((_, e) => onChatReasoningSelectionChanged(e.NewIndex))
            .SelectedIndex(workspaceViewModel.Bind.SelectedReasoningIndex)
            .MinWidth(12)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectReasoning);
        ChatAutoScrollCheckBox = new CheckBox("AutoScroll")
            .IsChecked(workspaceViewModel.Bind.AutoScroll)
            .IsEnabled(workspaceViewModel.Bind.CanToggleAutoScroll);
        ChatAutoScrollCheckBox.KeyDown((_, e) => OnAutoScrollCheckBoxToggled(e, onAutoScrollChanged));
        ChatAutoScrollCheckBox.PointerPressed((_, e) => OnAutoScrollCheckBoxToggled(e, onAutoScrollChanged));
        AlwaysEnqueueCheckBox = new CheckBox("AlwaysQueue")
            .IsChecked(promptComposerViewModel.Bind.AlwaysEnqueue)
            .IsEnabled(promptComposerViewModel.Bind.CanAlwaysEnqueue);
        var compactThreadButton = CreateIconButton(
                $"{NerdFont.MdSelectCompare}",
                "Compact the selected thread session when it is idle (F11).",
                compactThread,
                button => button.IsEnabled(promptComposerViewModel.Bind.CanCompact));

        var statusSpinner = new Spinner().Style(SpinnerStyles.Dots);
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
                    workspaceViewModel.PromptStripItems,
                    markdown => (ThreadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
                    convertQueuedPromptToSteer,
                    deletePendingSteer,
                    deleteQueuedPrompt,
                    updateQueuedPromptCount,
                    updateQueuedPromptText,
                    (onAccepted, placeholder) => CreateStyledPromptEditor(onAccepted, openHelp, openCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, placeholder)));

        var selectionControls = new HStack(
        [
            ChatBackendSelect,
            ChatModelSelect,
            ChatReasoningSelect,
            compactThreadButton,
            ChatAutoScrollCheckBox,
            AlwaysEnqueueCheckBox,
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
            threadInfoButton,
            ExpandPromptButton,
            SendPromptButton,
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
        _activeTabContent = ThreadBodySplitter;

        var activeTabContentHost = new ComputedVisual(
            () =>
            {
                _ = _activeTabContentVersion.Value;
                return _activeTabContent ?? ThreadBodySplitter;
            });

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
        threadPaneLayout.Cell(activeTabContentHost.Stretch(), 1, 0);

        ThreadPaneLayout = threadPaneLayout;
        Root = ThreadPaneLayout;
        foreach (var binding in commandBindings)
        {
            if (IsSharedEditorCommand(binding.Metadata.Id))
            {
                Root.AddCommand(BuildCommand(binding));
            }
        }
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

    private Select<ChatBackendOption> ChatBackendSelect { get; }

    private Select<ChatModelOption> ChatModelSelect { get; }

    private Select<ChatReasoningOption> ChatReasoningSelect { get; }

    public CheckBox ChatAutoScrollCheckBox { get; }

    public CheckBox AlwaysEnqueueCheckBox { get; }

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

    public void SetActiveTabContent(Visual content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (ReferenceEquals(_activeTabContent, content))
        {
            return;
        }

        _activeTabContent = content;
        _activeTabContentVersion.Value++;
    }

    public void OpenExpandedPromptDialog()
        => OpenExpandedPromptDialog(_promptComposerViewModel, _promptTextBinding);

    public void SyncChatSelectorItems(ThreadWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        ChatBackendPresentation.ReplaceSelectItems(ChatBackendSelect, workspaceViewModel.BackendOptions);
        ChatBackendPresentation.ReplaceSelectItems(ChatModelSelect, workspaceViewModel.ModelOptions);
        ChatBackendPresentation.ReplaceSelectItems(ChatReasoningSelect, workspaceViewModel.ReasoningOptions);
    }

    private static ChatPromptEditor CreatePromptEditor(
        PromptComposerViewModel promptComposerViewModel,
        Action openHelp,
        Action openCommandPalette,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        Action<string> acceptPrompt,
        IReadOnlyList<ThreadWorkspaceCommandBinding> commandBindings,
        Binding<string?> promptText)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(openHelp);
        ArgumentNullException.ThrowIfNull(openCommandPalette);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(acceptPrompt);
        ArgumentNullException.ThrowIfNull(commandBindings);
        var editor = CreateStyledPromptEditor(acceptPrompt, openHelp, openCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder)
            .Text(promptText);

        foreach (var binding in commandBindings)
        {
            editor.AddCommand(BuildCommand(binding));
        }

        return editor;
    }

    private static bool IsSharedEditorCommand(string commandId)
        => commandId is
            "CodeAlta.Shell.Help" or
            "CodeAlta.Thread.CloseTab" or
            "CodeAlta.Thread.TabLeft" or
            "CodeAlta.Thread.TabRight";

    private static Command BuildCommand(ThreadWorkspaceCommandBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var metadata = binding.Metadata;
        return new Command
        {
            Id = metadata.Id,
            LabelMarkup = metadata.DisplayLabelMarkup,
            Name = metadata.CommandName,
            DescriptionMarkup = metadata.DescriptionMarkup,
            SearchText = metadata.CommandSearchText,
            Execute = _ => binding.Execute(),
            CanExecute = _ => binding.CanExecute(),
            Gesture = metadata.Gesture,
            Sequence = metadata.Sequence,
            Presentation = ResolvePresentation(metadata),
        };
    }

    private static CommandPresentation ResolvePresentation(ShellCommandMetadata metadata)
    {
        var presentation = CommandPresentation.None;
        if (metadata.ShowInCommandBar)
        {
            presentation |= CommandPresentation.CommandBar;
        }

        if (metadata.ShowInCommandPalette)
        {
            presentation |= CommandPresentation.CommandPalette;
        }

        return presentation;
    }

    private static void OnAutoScrollCheckBoxToggled(KeyEventArgs e, Action onAutoScrollChanged)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(onAutoScrollChanged);

        if (e.Handled && e.Key is TerminalKey.Space or TerminalKey.Enter)
        {
            onAutoScrollChanged();
        }
    }

    private static void OnAutoScrollCheckBoxToggled(PointerEventArgs e, Action onAutoScrollChanged)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(onAutoScrollChanged);

        if (e.Handled && e.Button == TerminalMouseButton.Left)
        {
            onAutoScrollChanged();
        }
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

        var editor = CreateStyledPromptEditor(_ => { }, _openHelp, _openCommandPalette, _projectFileSearchService, _getPromptReferenceProjectRoot, placeholder: null)
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

    private static Visual CreatePromptActionButton(
        PromptComposerViewModel promptComposerViewModel,
        Action sendPrompt,
        Action abortThread)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(abortThread);

        return new ComputedVisual(() =>
        {
            var isAbort = promptComposerViewModel.CanAbort;
            var icon = isAbort ? $"{NerdFont.MdSquare}" : $"{NerdFont.MdSend}";
            var tooltipText = isAbort ? "Abort the selected thread run." : "Send the current prompt.";
            var action = isAbort ? abortThread : sendPrompt;
            var tone = isAbort ? ControlTone.Error : ControlTone.Success;
            var isEnabled = isAbort ? promptComposerViewModel.CanAbort : promptComposerViewModel.CanSend;

            return CreateIconButton(
                icon,
                tooltipText,
                action,
                button =>
                {
                    button.Tone = tone;
                    button.IsEnabled = isEnabled;
                });
        });
    }

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        string? placeholder)
        => CreateStyledPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette, projectFileSearchService: null, getPromptReferenceProjectRoot: null, placeholder);

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        IProjectFileSearchService? projectFileSearchService,
        Func<string?>? getPromptReferenceProjectRoot,
        string? placeholder)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);

        var converter = new MarkdownMarkupConverter();
        ITextSnapshot? cachedSnapshot = null;
        Theme? cachedTheme = null;
        string? cachedText = null;
        string? cachedProjectRoot = null;
        List<StyledRun>? cachedRuns = null;
        var editor = new ChatPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette)
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
        if (projectFileSearchService is not null && getPromptReferenceProjectRoot is not null)
        {
            editor.EnableProjectFileReferences(
                projectFileSearchService,
                ProjectFileAppearanceRegistry.Default,
                getPromptReferenceProjectRoot);
        }

        return editor;

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            if (cachedRuns is not null &&
                ReferenceEquals(cachedSnapshot, request.Snapshot) &&
                Equals(cachedTheme, request.Theme) &&
                string.Equals(cachedProjectRoot, getPromptReferenceProjectRoot?.Invoke(), StringComparison.Ordinal))
            {
                runs.AddRange(cachedRuns);
                return;
            }

            var text = SnapshotToString(request.Snapshot);
            var projectRoot = getPromptReferenceProjectRoot?.Invoke();
            if (cachedRuns is not null &&
                string.Equals(cachedText, text, StringComparison.Ordinal) &&
                Equals(cachedTheme, request.Theme) &&
                string.Equals(cachedProjectRoot, projectRoot, StringComparison.Ordinal))
            {
                cachedSnapshot = request.Snapshot;
                runs.AddRange(cachedRuns);
                return;
            }

            converter.Theme = request.Theme;
            converter.Highlight(text, runs);
            ProjectFilePromptHighlighter.AddRuns(text, projectRoot, runs);
            cachedSnapshot = request.Snapshot;
            cachedTheme = request.Theme;
            cachedText = text;
            cachedProjectRoot = projectRoot;
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
