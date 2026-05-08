using System.Diagnostics.CodeAnalysis;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using ImageControl = XenoAtom.Terminal.UI.Graphics.Image;

namespace CodeAlta.Views;

internal sealed class ThreadWorkspaceView
{
    private readonly ModelProviderSelectorView _modelProviderSelectorView;
    private readonly PromptImageAttachmentStripView _promptImageAttachmentStripView;
    private readonly PromptComposerView _promptComposerView;
    private readonly ThreadTabHostView _threadTabHostView;

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
        _promptComposerView = new PromptComposerView(
            promptComposerViewModel,
            commandBindings,
            projectFileSearchService,
            getPromptReferenceProjectRoot,
            promptText,
            _promptImageAttachmentStripView,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            acceptPrompt,
            sendPrompt,
            abortThread,
            openHelp,
            openCommandPalette);
        ThreadInput = _promptComposerView.Editor;
        ThreadInputView = _promptComposerView.EditorView;

        SendPromptButton = _promptComposerView.SendButton;
        ExpandPromptButton = _promptComposerView.ExpandButton;
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
        => _promptComposerView.OpenExpandedPromptDialog();

    public void SyncChatSelectorItems(ThreadWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        _modelProviderSelectorView.SyncItems(workspaceViewModel);
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

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        string? placeholder)
        => PromptComposerView.CreateStyledPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette, placeholder);

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        IProjectFileSearchService? projectFileSearchService,
        Func<string?>? getPromptReferenceProjectRoot,
        string? placeholder)
        => PromptComposerView.CreateStyledPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, placeholder);
}
