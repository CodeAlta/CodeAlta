using System.Diagnostics.CodeAnalysis;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Catalog;
using PluginPromptEditorContribution = CodeAlta.Plugins.Abstractions.PluginPromptEditorContribution;
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

internal sealed class SessionWorkspaceView
{
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly ShellCommandSurfaceCoordinator _shellCommandSurfaceCoordinator;
    private readonly SessionWorkspaceChromeController _chromeController;
    private readonly PromptComposerViewController _promptComposerController;
    private readonly QueuedPromptStripController _queuedPromptController;
    private readonly ModelProviderSelectorController _modelProviderController;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Func<string?> _getPromptReferenceProjectRoot;
    private readonly IReadOnlyList<PluginPromptEditorContribution> _promptEditorContributions;
    private readonly Func<string, SessionState?, PromptComposerSessionBinding> _getPromptComposerSession;
    private readonly State<float> _thinkingAnimationPhase01;
    private readonly PromptImageWorkspaceCallbacks? _fallbackPromptImageCallbacks;
    private readonly SessionTabHostView _sessionTabHostView;
    private SessionPromptPanel? _fallbackPromptPanel;

    internal const TerminalKey ExpandPromptShortcutKey = TerminalKey.F6;
    internal static readonly KeySequence ModelProvidersShortcutSequence = BuiltinShellCommands.ModelProvidersShortcutSequence;
    internal static readonly KeySequence SessionUsageShortcutSequence = BuiltinShellCommands.SessionUsageShortcutSequence;
    internal static readonly KeySequence SessionInfoShortcutSequence = BuiltinShellCommands.SessionInfoShortcutSequence;

    public SessionWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        SessionWorkspaceChromeController chromeController,
        PromptComposerViewController promptComposerController,
        QueuedPromptStripController queuedPromptController,
        ModelProviderSelectorController modelProviderController,
        SessionTabHostController tabHostController,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        Func<string, SessionState?, PromptComposerSessionBinding> getPromptComposerSession,
        State<float> thinkingAnimationPhase01,
        PromptImageWorkspaceCallbacks? promptImageCallbacks = null)
        : this(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            shellCommandSurfaceCoordinator,
            chromeController,
            promptComposerController,
            queuedPromptController,
            modelProviderController,
            tabHostController,
            projectFileSearchService,
            getPromptReferenceProjectRoot,
            [],
            getPromptComposerSession,
            thinkingAnimationPhase01,
            promptImageCallbacks)
    {
    }

    public SessionWorkspaceView(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        SessionWorkspaceChromeController chromeController,
        PromptComposerViewController promptComposerController,
        QueuedPromptStripController queuedPromptController,
        ModelProviderSelectorController modelProviderController,
        SessionTabHostController tabHostController,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        IReadOnlyList<PluginPromptEditorContribution> promptEditorContributions,
        Func<string, SessionState?, PromptComposerSessionBinding> getPromptComposerSession,
        State<float> thinkingAnimationPhase01,
        PromptImageWorkspaceCallbacks? promptImageCallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(shellCommandSurfaceCoordinator);
        ArgumentNullException.ThrowIfNull(chromeController);
        ArgumentNullException.ThrowIfNull(promptComposerController);
        ArgumentNullException.ThrowIfNull(queuedPromptController);
        ArgumentNullException.ThrowIfNull(modelProviderController);
        ArgumentNullException.ThrowIfNull(tabHostController);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(promptEditorContributions);
        ArgumentNullException.ThrowIfNull(getPromptComposerSession);
        ArgumentNullException.ThrowIfNull(thinkingAnimationPhase01);

        _shellViewModel = shellViewModel;
        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _shellCommandSurfaceCoordinator = shellCommandSurfaceCoordinator;
        _chromeController = chromeController;
        _promptComposerController = promptComposerController;
        _queuedPromptController = queuedPromptController;
        _modelProviderController = modelProviderController;
        _projectFileSearchService = projectFileSearchService;
        _getPromptReferenceProjectRoot = getPromptReferenceProjectRoot;
        _promptEditorContributions = promptEditorContributions;
        _getPromptComposerSession = getPromptComposerSession;
        _thinkingAnimationPhase01 = thinkingAnimationPhase01;
        _fallbackPromptImageCallbacks = promptImageCallbacks;

        SessionCommandBar = new CommandBar
        {
            HorizontalAlignment = Align.Stretch,
            MultiLine = false,
        };

        _sessionTabHostView = new SessionTabHostView(tabHostController);
        SessionPaneLayout = _sessionTabHostView.Root;
        Root = SessionPaneLayout;
        foreach (var command in shellCommandSurfaceCoordinator.CommandsFor(ShellCommandPlacement.WorkspaceRoot))
        {
            Root.AddCommand(shellCommandSurfaceCoordinator.CreateViewCommand(command));
        }
    }

    public Visual Root { get; }

    public Visual SessionPaneLayout { get; }

    public Visual SessionBottomPanel => ActivePromptPanel.Root;

    public ChatPromptEditor SessionInput => ActivePromptPanel.Editor;

    public Visual SessionInputView => ActivePromptPanel.EditorView;

    public Visual SendPromptButton => ActivePromptPanel.SendPromptButton;

    public Visual ExpandPromptButton => ActivePromptPanel.ExpandPromptButton;

    public CommandBar SessionCommandBar { get; }

    private SessionPromptPanel ActivePromptPanel
        => _sessionTabHostView.ActivePromptPanel ?? (_fallbackPromptPanel ??= CreatePromptPanel(CodeAltaApp.DraftTabId, null, _fallbackPromptImageCallbacks));

    private PromptComposerView _promptComposerView => ActivePromptPanel.Composer;

    private Select<ModelProviderOption> ModelProviderSelect
        => ActivePromptPanel.ModelProviderSelectorView.ModelProviderSelect;

    private Select<ChatModelOption> ChatModelSelect
        => ActivePromptPanel.ModelProviderSelectorView.ChatModelSelect;

    private Select<ChatReasoningOption> ChatReasoningSelect
        => ActivePromptPanel.ModelProviderSelectorView.ChatReasoningSelect;

    public CheckBox AlwaysEnqueueCheckBox
        => ActivePromptPanel.ModelProviderSelectorView.AlwaysEnqueueCheckBox;

    public bool AlwaysEnqueue => ActivePromptPanel.PromptComposerViewModel.AlwaysEnqueue;

    public TabControl SessionTabControl
        => _sessionTabHostView.SessionTabControl;

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
        => _sessionTabHostView.TryGetTabPage(tabId, out page);

    public void RememberTabPage(string tabId, TabPage page)
        => _sessionTabHostView.RememberTabPage(tabId, page);

    public bool RemoveTabPage(string tabId)
        => _sessionTabHostView.RemoveTabPage(tabId);

    public bool TryGetPromptPanel(string tabId, [NotNullWhen(true)] out SessionPromptPanel? panel)
        => _sessionTabHostView.TryGetPromptPanel(tabId, out panel);

    public Visual CreateSessionTabContent(string tabId, Visual primaryContent, SessionState? session = null)
        => _sessionTabHostView.CreateSessionTabContent(tabId, primaryContent, () => CreatePromptPanel(tabId, session, promptImageCallbacks: null));

    public void ActivateSessionTabContent(string? tabId)
        => _sessionTabHostView.ActivateSessionTabContent(tabId);

    public void OpenExpandedPromptDialog()
        => ActivePromptPanel.Composer.OpenExpandedPromptDialog();

    public void FocusModelProviderSelector()
        => SessionPaneLayout.App?.Focus(ModelProviderSelect);

    public void FocusReasoningSelector()
        => SessionPaneLayout.App?.Focus(ChatReasoningSelect);

    public void SyncModelProviderSelectorItems(SessionWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        SyncActivePromptPanelProjection();
    }

    public void SyncActivePromptPanelProjection()
    {
        var panel = ActivePromptPanel;
        panel.ChromeState.ApplyProjection(_shellViewModel, _workspaceViewModel, _promptComposerViewModel, preserveAlwaysEnqueue: true);
        panel.ModelProviderSelectorView.SyncItems(panel.WorkspaceViewModel);
    }

    public void RefreshActivePromptImages()
        => ActivePromptPanel.PromptComposerViewModel.PromptImageAttachmentVersion++;

    private SessionPromptPanel CreatePromptPanel(string tabId, SessionState? session, PromptImageWorkspaceCallbacks? promptImageCallbacks)
    {
        var promptSession = _getPromptComposerSession(tabId, session);
        var chromeState = SessionPromptChromeState.CloneFrom(_shellViewModel, _workspaceViewModel, _promptComposerViewModel);
        var shellViewModel = chromeState.ShellViewModel;
        var workspaceViewModel = chromeState.WorkspaceViewModel;
        var promptComposerViewModel = chromeState.PromptComposerViewModel;
        workspaceViewModel.SetModelProviderSelectionChangedHandlers(
            _modelProviderController.SelectProvider,
            _modelProviderController.SelectModel,
            _modelProviderController.SelectReasoning);
        var imageStripView = new PromptImageAttachmentStripView(
            promptComposerViewModel,
            promptImageCallbacks ?? promptSession.PromptImageCallbacks,
            () => SessionPaneLayout?.GetAbsoluteBounds(),
            () => SessionInput);
        var promptComposerView = new PromptComposerView(
            promptComposerViewModel,
            _shellCommandSurfaceCoordinator,
            _projectFileSearchService,
            _getPromptReferenceProjectRoot,
            _promptEditorContributions,
            promptSession.PromptText,
            imageStripView,
            () => SessionPaneLayout?.GetAbsoluteBounds(),
            _promptComposerController);

        Visual? sessionInfoButton = null;
        sessionInfoButton = CreateIconButton(
                $"{NerdFont.MdInformationOutline}",
                $"Show information about the selected session ({SessionInfoShortcutSequence}).",
                () => _chromeController.ToggleSessionInfoPopup(sessionInfoButton!),
                button => button.IsEnabled(workspaceViewModel.Bind.CanShowSessionInfo));
        var modelProviderSelectorView = new ModelProviderSelectorView(
            workspaceViewModel,
            promptComposerViewModel,
            _modelProviderController);
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
        var statusLine = new SessionStatusLineView(shellViewModel, _thinkingAnimationPhase01, _chromeController.BuildPluginSessionStatusVisual).Root;
        var queuedPromptList = new QueuedPromptStripView(
            workspaceViewModel,
            _queuedPromptController).Root;
        var promptImageStrip = imageStripView.Root;
        var selectionRight = new HStack(
        [
            providerSummaryButton,
            usageIndicator,
            sessionInfoButton,
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

        return new SessionPromptPanel(
            root,
            promptComposerView.Editor,
            promptComposerView.EditorView,
            promptComposerView.SendButton,
            promptComposerView.ExpandButton,
            promptComposerView,
            modelProviderSelectorView,
            chromeState);
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
        IReadOnlyList<PluginPromptEditorContribution> promptEditorContributions,
        string? placeholder)
        => PromptComposerView.CreateStyledPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, promptEditorContributions, placeholder);
}
