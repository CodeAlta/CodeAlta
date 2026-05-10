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
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly ThreadWorkspaceViewModel _workspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly IReadOnlyList<ThreadWorkspaceCommandBinding> _commandBindings;
    private readonly ThreadWorkspaceChromeController _chromeController;
    private readonly PromptComposerViewController _promptComposerController;
    private readonly QueuedPromptStripController _queuedPromptController;
    private readonly ModelProviderSelectorController _modelProviderController;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Func<string?> _getPromptReferenceProjectRoot;
    private readonly Func<string, ThreadSessionState?, PromptComposerSessionBinding> _getPromptComposerSession;
    private readonly State<float> _thinkingAnimationPhase01;
    private readonly PromptImageWorkspaceCallbacks? _fallbackPromptImageCallbacks;
    private readonly List<ModelProviderSelectorView> _modelProviderSelectorViews = [];
    private readonly ThreadTabHostView _threadTabHostView;
    private ThreadPromptPanel? _fallbackPromptPanel;

    internal const TerminalKey ExpandPromptShortcutKey = TerminalKey.F6;
    internal static readonly KeySequence ModelProvidersShortcutSequence = ShellCommandCatalog.ModelProvidersShortcutSequence;
    internal static readonly KeySequence FocusModelProviderShortcutSequence = ShellCommandCatalog.FocusModelProviderShortcutSequence;
    internal static readonly KeySequence SessionUsageShortcutSequence = ShellCommandCatalog.SessionUsageShortcutSequence;
    internal static readonly KeySequence ThreadInfoShortcutSequence = ShellCommandCatalog.ThreadInfoShortcutSequence;

    public ThreadWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        IReadOnlyList<ThreadWorkspaceCommandBinding> commandBindings,
        ThreadWorkspaceChromeController chromeController,
        PromptComposerViewController promptComposerController,
        QueuedPromptStripController queuedPromptController,
        ModelProviderSelectorController modelProviderController,
        ThreadTabHostController tabHostController,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        Func<string, ThreadSessionState?, PromptComposerSessionBinding> getPromptComposerSession,
        State<float> thinkingAnimationPhase01,
        PromptImageWorkspaceCallbacks? promptImageCallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(commandBindings);
        ArgumentNullException.ThrowIfNull(chromeController);
        ArgumentNullException.ThrowIfNull(promptComposerController);
        ArgumentNullException.ThrowIfNull(queuedPromptController);
        ArgumentNullException.ThrowIfNull(modelProviderController);
        ArgumentNullException.ThrowIfNull(tabHostController);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(getPromptComposerSession);
        ArgumentNullException.ThrowIfNull(thinkingAnimationPhase01);

        _shellViewModel = shellViewModel;
        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _commandBindings = commandBindings;
        _chromeController = chromeController;
        _promptComposerController = promptComposerController;
        _queuedPromptController = queuedPromptController;
        _modelProviderController = modelProviderController;
        _projectFileSearchService = projectFileSearchService;
        _getPromptReferenceProjectRoot = getPromptReferenceProjectRoot;
        _getPromptComposerSession = getPromptComposerSession;
        _thinkingAnimationPhase01 = thinkingAnimationPhase01;
        _fallbackPromptImageCallbacks = promptImageCallbacks;

        ThreadCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
            MultiLine = false,
        };

        _threadTabHostView = new ThreadTabHostView(tabHostController);
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

    public Visual Root { get; }

    public Visual ThreadPaneLayout { get; }

    public Visual ThreadBottomPanel => ActivePromptPanel.Root;

    public ChatPromptEditor ThreadInput => ActivePromptPanel.Editor;

    public Visual ThreadInputView => ActivePromptPanel.EditorView;

    public Visual SendPromptButton => ActivePromptPanel.SendPromptButton;

    public Visual ExpandPromptButton => ActivePromptPanel.ExpandPromptButton;

    public CommandBar ThreadCommandBar { get; }

    private ThreadPromptPanel ActivePromptPanel
        => _threadTabHostView.ActivePromptPanel ?? (_fallbackPromptPanel ??= CreatePromptPanel(CodeAltaApp.DraftTabId, null, _fallbackPromptImageCallbacks));

    private PromptComposerView _promptComposerView => ActivePromptPanel.Composer;

    private Select<ChatBackendOption> ChatBackendSelect
        => ActivePromptPanel.ModelProviderSelectorView.ChatBackendSelect;

    private Select<ChatModelOption> ChatModelSelect
        => ActivePromptPanel.ModelProviderSelectorView.ChatModelSelect;

    private Select<ChatReasoningOption> ChatReasoningSelect
        => ActivePromptPanel.ModelProviderSelectorView.ChatReasoningSelect;

    public CheckBox AlwaysEnqueueCheckBox
        => ActivePromptPanel.ModelProviderSelectorView.AlwaysEnqueueCheckBox;

    public bool AlwaysEnqueue => ActivePromptPanel.PromptComposerViewModel.AlwaysEnqueue;

    public TabControl ThreadTabControl
        => _threadTabHostView.ThreadTabControl;

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
        => _threadTabHostView.TryGetTabPage(tabId, out page);

    public void RememberTabPage(string tabId, TabPage page)
        => _threadTabHostView.RememberTabPage(tabId, page);

    public bool RemoveTabPage(string tabId)
        => _threadTabHostView.RemoveTabPage(tabId);

    public Visual CreateThreadTabContent(string tabId, Visual primaryContent, ThreadSessionState? session = null)
        => _threadTabHostView.CreateThreadTabContent(tabId, primaryContent, () => CreatePromptPanel(tabId, session, promptImageCallbacks: null));

    public void ActivateThreadTabContent(string? tabId)
        => _threadTabHostView.ActivateThreadTabContent(tabId);

    public void OpenExpandedPromptDialog()
        => ActivePromptPanel.Composer.OpenExpandedPromptDialog();

    public void FocusModelProviderSelector()
        => ThreadPaneLayout.App?.Focus(ChatBackendSelect);

    public void SyncModelProviderSelectorItems(ThreadWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        SyncActivePromptPanelProjection();
        foreach (var selectorView in _modelProviderSelectorViews)
        {
            selectorView.SyncItems(workspaceViewModel);
        }
    }

    public void SyncActivePromptPanelProjection()
    {
        var panel = ActivePromptPanel;
        CopyShellViewModel(_shellViewModel, panel.ShellViewModel);
        CopyWorkspaceViewModel(_workspaceViewModel, panel.WorkspaceViewModel);
        CopyPromptComposerViewModel(_promptComposerViewModel, panel.PromptComposerViewModel, preserveAlwaysEnqueue: true);
        panel.ModelProviderSelectorView.SyncItems(panel.WorkspaceViewModel);
    }

    public void RefreshActivePromptImages()
        => ActivePromptPanel.PromptComposerViewModel.PromptImageAttachmentVersion++;

    private ThreadPromptPanel CreatePromptPanel(string tabId, ThreadSessionState? session, PromptImageWorkspaceCallbacks? promptImageCallbacks)
    {
        var promptSession = _getPromptComposerSession(tabId, session);
        var shellViewModel = CloneShellViewModel(_shellViewModel);
        var workspaceViewModel = CloneWorkspaceViewModel(_workspaceViewModel);
        var promptComposerViewModel = ClonePromptComposerViewModel(_promptComposerViewModel);
        var imageStripView = new PromptImageAttachmentStripView(
            promptComposerViewModel,
            promptImageCallbacks ?? promptSession.PromptImageCallbacks,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            () => ThreadInput);
        var promptComposerView = new PromptComposerView(
            promptComposerViewModel,
            _commandBindings,
            _projectFileSearchService,
            _getPromptReferenceProjectRoot,
            promptSession.PromptText,
            imageStripView,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            _promptComposerController);

        Visual? threadInfoButton = null;
        threadInfoButton = CreateIconButton(
                $"{NerdFont.MdInformationOutline}",
                $"Show information about the selected thread ({ThreadInfoShortcutSequence}).",
                () => _chromeController.ToggleThreadInfoPopup(threadInfoButton!),
                button => button.IsEnabled(workspaceViewModel.Bind.CanShowThreadInfo));
        var modelProviderSelectorView = new ModelProviderSelectorView(
            workspaceViewModel,
            promptComposerViewModel,
            _modelProviderController);
        _modelProviderSelectorViews.Add(modelProviderSelectorView);
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
            .Click(_chromeController.OpenModelProviders)
            .Tooltip(new TextBlock($"Configure model providers ({ModelProvidersShortcutSequence})."));

        var usageIndicator = _chromeController.BuildSessionUsageIndicatorVisual();
        var statusLine = new ThreadStatusLineView(shellViewModel, _thinkingAnimationPhase01, _chromeController.BuildPluginThreadStatusVisual).Root;
        var queuedPromptList = new QueuedPromptStripView(
            workspaceViewModel,
            _queuedPromptController).Root;
        var promptImageStrip = imageStripView.Root;
        var selectionRight = new HStack(
        [
            providerSummaryButton,
            usageIndicator,
            threadInfoButton,
            promptComposerView.ExpandButton,
            promptComposerView.SendButton,
        ])
        {
            Spacing = 2,
        };
        var selectionLine = new StatusBar()
            .LeftText(modelProviderSelectorView.Root)
            .RightText(selectionRight);
        var root = new DockLayout(
            top: new VStack([queuedPromptList, promptImageStrip, statusLine]) { Spacing = 0 },
            content: promptComposerView.EditorView,
            bottom: selectionLine)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        return new ThreadPromptPanel(
            root,
            promptComposerView.Editor,
            promptComposerView.EditorView,
            promptComposerView.SendButton,
            promptComposerView.ExpandButton,
            promptComposerView,
            modelProviderSelectorView,
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel);
    }

    private static CodeAltaShellViewModel CloneShellViewModel(CodeAltaShellViewModel source)
    {
        var target = new CodeAltaShellViewModel();
        CopyShellViewModel(source, target);
        return target;
    }

    private static ThreadWorkspaceViewModel CloneWorkspaceViewModel(ThreadWorkspaceViewModel source)
    {
        var target = new ThreadWorkspaceViewModel();
        CopyWorkspaceViewModel(source, target);
        return target;
    }

    private static PromptComposerViewModel ClonePromptComposerViewModel(PromptComposerViewModel source)
    {
        var target = new PromptComposerViewModel();
        CopyPromptComposerViewModel(source, target, preserveAlwaysEnqueue: false);
        return target;
    }

    private static void CopyShellViewModel(CodeAltaShellViewModel source, CodeAltaShellViewModel target)
    {
        target.StatusText = source.StatusText;
        target.StatusIconMarkup = source.StatusIconMarkup;
        target.ProviderSessionLoadStatusText = source.ProviderSessionLoadStatusText;
        target.StatusBusy = source.StatusBusy;
        target.StatusTone = source.StatusTone;
        target.IsInitialized = source.IsInitialized;
    }

    private static void CopyWorkspaceViewModel(ThreadWorkspaceViewModel source, ThreadWorkspaceViewModel target)
    {
        target.ModelProviderStatusMarkup = source.ModelProviderStatusMarkup;
        target.ProviderSummaryMarkup = source.ProviderSummaryMarkup;
        target.CanSelectModelProvider = source.CanSelectModelProvider;
        target.CanSelectModel = source.CanSelectModel;
        target.CanSelectReasoning = source.CanSelectReasoning;
        target.ModelProviderOptions = source.ModelProviderOptions;
        target.SelectedModelProviderIndex = source.SelectedModelProviderIndex;
        target.ModelOptions = source.ModelOptions;
        target.SelectedModelIndex = source.SelectedModelIndex;
        target.ReasoningOptions = source.ReasoningOptions;
        target.SelectedReasoningIndex = source.SelectedReasoningIndex;
        target.CanShowThreadInfo = source.CanShowThreadInfo;
        target.SetPromptStripItems(source.PromptStripItems, source.HasQueuedPrompts);
    }

    private static void CopyPromptComposerViewModel(PromptComposerViewModel source, PromptComposerViewModel target, bool preserveAlwaysEnqueue)
    {
        target.Placeholder = source.Placeholder;
        target.IsEnabled = source.IsEnabled;
        target.CanSend = source.CanSend;
        target.CanSteer = source.CanSteer;
        target.CanAbort = source.CanAbort;
        target.CanCompact = source.CanCompact;
        target.CanCloseTab = source.CanCloseTab;
        target.CanClearQueue = source.CanClearQueue;
        target.CanAlwaysEnqueue = source.CanAlwaysEnqueue;
        if (!preserveAlwaysEnqueue)
        {
            target.AlwaysEnqueue = source.AlwaysEnqueue;
        }

        target.PromptImageAttachmentVersion = source.PromptImageAttachmentVersion;
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
