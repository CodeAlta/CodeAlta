using System.Diagnostics.CodeAnalysis;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Styling;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using ImageControl = XenoAtom.Terminal.UI.Graphics.Image;

namespace CodeAlta.Views;

internal sealed class ThreadWorkspaceView
{
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Binding<string?> _promptTextBinding;
    private readonly Action _openHelp;
    private readonly Action _openCommandPalette;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Func<string?> _getPromptReferenceProjectRoot;
    private readonly ModelProviderSelectorView _modelProviderSelectorView;
    private readonly PromptImageAttachmentStripView _promptImageAttachmentStripView;
    private readonly ThreadTabHostView _threadTabHostView;
    private Dialog? _expandedPromptDialog;

    internal const TerminalKey ExpandPromptShortcutKey = TerminalKey.F6;
    internal static readonly KeySequence ModelProvidersShortcutSequence = ShellCommandCatalog.ModelProvidersShortcutSequence;
    internal static readonly KeySequence SessionUsageShortcutSequence = ShellCommandCatalog.SessionUsageShortcutSequence;
    internal static readonly KeySequence ThreadInfoShortcutSequence = ShellCommandCatalog.ThreadInfoShortcutSequence;

    public ThreadWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        IReadOnlyList<ThreadWorkspaceCommandBinding> commandBindings,
        ThreadWorkspaceViewActions actions,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        Binding<string?> promptText,
        State<float> thinkingAnimationPhase01,
        PromptImageWorkspaceCallbacks? promptImageCallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(commandBindings);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(thinkingAnimationPhase01);

        ValidateActions(actions);
        var buildSessionUsageIndicatorVisual = actions.BuildSessionUsageIndicatorVisual;
        var openSessionUsagePopup = actions.OpenSessionUsagePopup;
        var toggleThreadInfoPopup = actions.ToggleThreadInfoPopup;
        var openHelp = actions.OpenHelp;
        var openCommandPalette = actions.OpenCommandPalette;
        var openModelProviders = actions.OpenModelProviders;
        var acceptPrompt = actions.AcceptPrompt;
        var sendPrompt = actions.SendPrompt;
        var steerPrompt = actions.SteerPrompt;
        var clearQueuedPrompts = actions.ClearQueuedPrompts;
        var convertQueuedPromptToSteer = actions.ConvertQueuedPromptToSteer;
        var deletePendingSteer = actions.DeletePendingSteer;
        var deleteQueuedPrompt = actions.DeleteQueuedPrompt;
        var updateQueuedPromptCount = actions.UpdateQueuedPromptCount;
        var updateQueuedPromptText = actions.UpdateQueuedPromptText;
        var abortThread = actions.AbortThread;
        var compactThread = actions.CompactThread;
        var closeTab = actions.CloseTab;
        var onChatBackendSelectionChanged = actions.OnChatBackendSelectionChanged;
        var onChatModelSelectionChanged = actions.OnChatModelSelectionChanged;
        var onChatReasoningSelectionChanged = actions.OnChatReasoningSelectionChanged;
        var onSelectedTabChanged = actions.OnSelectedTabChanged;

        _promptComposerViewModel = promptComposerViewModel;
        _promptTextBinding = promptText;
        _openHelp = openHelp;
        _openCommandPalette = openCommandPalette;
        _projectFileSearchService = projectFileSearchService;
        _getPromptReferenceProjectRoot = getPromptReferenceProjectRoot;
        _promptImageAttachmentStripView = new PromptImageAttachmentStripView(
            promptComposerViewModel,
            promptImageCallbacks,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            () => ThreadInput);

        ThreadCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
            MultiLine = false,
        };

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
        _promptImageAttachmentStripView.ConfigurePromptImagePasteHandler(ThreadInput);
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
        _modelProviderSelectorView = new ModelProviderSelectorView(
            workspaceViewModel,
            promptComposerViewModel,
            onChatBackendSelectionChanged,
            onChatModelSelectionChanged,
            onChatReasoningSelectionChanged,
            compactThread);
        var providerSummaryButton = new Button(
            new Markup(() => workspaceViewModel.ProviderSummaryMarkup)
            {
                Wrap = false,
            })
            .Style(ButtonStyle.Default with
            {
                Normal = Style.None,
                Padding = Thickness.Zero,
            })
            .Click(openModelProviders)
            .Tooltip(new TextBlock($"Configure model providers ({ModelProvidersShortcutSequence})."));

        var usageIndicator = buildSessionUsageIndicatorVisual();
        var statusLine = new ThreadStatusLineView(shellViewModel, thinkingAnimationPhase01).Root;

        var queuedPromptList = new QueuedPromptStripView(
            workspaceViewModel,
            markdown => (ThreadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
            convertQueuedPromptToSteer,
            deletePendingSteer,
            deleteQueuedPrompt,
            updateQueuedPromptCount,
            updateQueuedPromptText,
            (onAccepted, placeholder) => CreateStyledPromptEditor(onAccepted, openHelp, openCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, placeholder)).Root;

        var promptImageStrip = _promptImageAttachmentStripView.Root;

        var selectionRight = new HStack(
        [
            providerSummaryButton,
            usageIndicator,
            threadInfoButton,
            ExpandPromptButton,
            SendPromptButton,
        ])
        {
            Spacing = 2,
        };

        var selectionLine = new StatusBar()
            .LeftText(_modelProviderSelectorView.Root)
            .RightText(selectionRight);

        ThreadBottomPanel = new DockLayout(
            top: new VStack([queuedPromptList, promptImageStrip, statusLine]) { Spacing = 0 },
            content: ThreadInputView,
            bottom: selectionLine)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        _threadTabHostView = new ThreadTabHostView(ThreadBottomPanel, onSelectedTabChanged);
        ThreadPaneLayout = _threadTabHostView.Root;
        Root = ThreadPaneLayout;
        foreach (var binding in commandBindings)
        {
            if (IsSharedEditorCommand(binding.Metadata.Id))
            {
                Root.AddCommand(BuildCommand(binding));
            }
        }
    }

    private static void ValidateActions(ThreadWorkspaceViewActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions.BuildSessionUsageIndicatorVisual);
        ArgumentNullException.ThrowIfNull(actions.OpenSessionUsagePopup);
        ArgumentNullException.ThrowIfNull(actions.ToggleThreadInfoPopup);
        ArgumentNullException.ThrowIfNull(actions.OpenHelp);
        ArgumentNullException.ThrowIfNull(actions.OpenCommandPalette);
        ArgumentNullException.ThrowIfNull(actions.OpenModelProviders);
        ArgumentNullException.ThrowIfNull(actions.AcceptPrompt);
        ArgumentNullException.ThrowIfNull(actions.SendPrompt);
        ArgumentNullException.ThrowIfNull(actions.SteerPrompt);
        ArgumentNullException.ThrowIfNull(actions.ClearQueuedPrompts);
        ArgumentNullException.ThrowIfNull(actions.ConvertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(actions.DeletePendingSteer);
        ArgumentNullException.ThrowIfNull(actions.DeleteQueuedPrompt);
        ArgumentNullException.ThrowIfNull(actions.UpdateQueuedPromptCount);
        ArgumentNullException.ThrowIfNull(actions.UpdateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(actions.AbortThread);
        ArgumentNullException.ThrowIfNull(actions.CompactThread);
        ArgumentNullException.ThrowIfNull(actions.CloseTab);
        ArgumentNullException.ThrowIfNull(actions.OnChatBackendSelectionChanged);
        ArgumentNullException.ThrowIfNull(actions.OnChatModelSelectionChanged);
        ArgumentNullException.ThrowIfNull(actions.OnChatReasoningSelectionChanged);
        ArgumentNullException.ThrowIfNull(actions.OnSelectedTabChanged);
    }

    public Visual Root { get; }

    public Visual ThreadPaneLayout { get; }

    public Visual ThreadBottomPanel { get; }

    public ChatPromptEditor ThreadInput { get; }

    public Visual ThreadInputView { get; }

    public Visual SendPromptButton { get; }

    public Visual ExpandPromptButton { get; }

    public CommandBar ThreadCommandBar { get; }

    private Select<ChatBackendOption> ChatBackendSelect
        => _modelProviderSelectorView.ChatBackendSelect;

    private Select<ChatModelOption> ChatModelSelect
        => _modelProviderSelectorView.ChatModelSelect;

    private Select<ChatReasoningOption> ChatReasoningSelect
        => _modelProviderSelectorView.ChatReasoningSelect;

    public CheckBox AlwaysEnqueueCheckBox
        => _modelProviderSelectorView.AlwaysEnqueueCheckBox;

    public TabControl ThreadTabControl
        => _threadTabHostView.ThreadTabControl;

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
        => _threadTabHostView.TryGetTabPage(tabId, out page);

    public void RememberTabPage(string tabId, TabPage page)
        => _threadTabHostView.RememberTabPage(tabId, page);

    public bool RemoveTabPage(string tabId)
        => _threadTabHostView.RemoveTabPage(tabId);

    public Visual CreateThreadTabContent(string tabId, Visual primaryContent)
        => _threadTabHostView.CreateThreadTabContent(tabId, primaryContent);

    public void ActivateThreadTabContent(string? tabId)
        => _threadTabHostView.ActivateThreadTabContent(tabId);
    public void OpenExpandedPromptDialog()
        => OpenExpandedPromptDialog(_promptComposerViewModel, _promptTextBinding);

    public void SyncChatSelectorItems(ThreadWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        _modelProviderSelectorView.SyncItems(workspaceViewModel);
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
            "CodeAlta.Providers.Manage" or
            "CodeAlta.Thread.CloseTab" or
            "CodeAlta.Thread.TabLeft" or
            "CodeAlta.Thread.TabRight" or
            "CodeAlta.Thread.MessagePrevious" or
            "CodeAlta.Thread.MessageNext" or
            "CodeAlta.Thread.MessageFirst" or
            "CodeAlta.Thread.MessageLast";

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

    private void OpenExpandedPromptDialog(
        PromptComposerViewModel promptComposerViewModel,
        Binding<string?> promptText)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        if (_expandedPromptDialog is { App: not null })
        {
            return;
        }

        var editor = CreateStyledPromptEditor(_ => CloseExpandedPromptDialog(), _openHelp, _openCommandPalette, _projectFileSearchService, _getPromptReferenceProjectRoot, placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder)
            .Text(promptText)
            .MinHeight(12)
            .IsEnabled(promptComposerViewModel.Bind.IsEnabled);
        _promptImageAttachmentStripView.ConfigurePromptImagePasteHandler(editor);
        editor.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Thread.ExpandPrompt.Close", new KeyGesture(TerminalKey.Escape)));
        editor.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Thread.ExpandPrompt.CloseWithCtrlEnter", new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), CommandPresentation.None));

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
            .BottomRightText(new Markup("[dim]Esc/Ctrl+Enter Close · draft preserved[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(editor.Scrollable());
        ResponsiveDialogSize.Apply(dialog, ThreadPaneLayout.GetAbsoluteBounds(), minWidth: 60, minHeight: 18);
        dialog.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Thread.ExpandPrompt.Close", new KeyGesture(TerminalKey.Escape)));
        dialog.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Thread.ExpandPrompt.CloseWithCtrlEnter", new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), CommandPresentation.None));

        _expandedPromptDialog = dialog;
        dialog.Show();
        dialog.App?.Focus(editor);

        Command CreateExpandedPromptDialogCloseCommand(string id, KeyGesture gesture, CommandPresentation presentation = CommandPresentation.CommandBar)
            => new()
            {
                Id = id,
                LabelMarkup = "Close",
                DescriptionMarkup = "Close the large prompt editor and keep the current draft.",
                Gesture = gesture,
                Importance = CommandImportance.Primary,
                Presentation = presentation,
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
