using CodeAlta.Catalog;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.Views;

internal sealed class SessionPromptChromeState
{
    public SessionPromptChromeState(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        ShellViewModel = shellViewModel;
        WorkspaceViewModel = workspaceViewModel;
        PromptComposerViewModel = promptComposerViewModel;
    }

    public CodeAltaShellViewModel ShellViewModel { get; }

    public SessionWorkspaceViewModel WorkspaceViewModel { get; }

    public PromptComposerViewModel PromptComposerViewModel { get; }

    public static SessionPromptChromeState CloneFrom(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel)
    {
        var state = new SessionPromptChromeState(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            new PromptComposerViewModel());
        state.ApplyProjection(shellViewModel, workspaceViewModel, promptComposerViewModel, preserveAlwaysEnqueue: false);
        return state;
    }

    public void ApplyProjection(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        bool preserveAlwaysEnqueue)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        CopyShellViewModel(shellViewModel, ShellViewModel);
        CopyWorkspaceViewModel(workspaceViewModel, WorkspaceViewModel);
        CopyPromptComposerViewModel(promptComposerViewModel, PromptComposerViewModel, preserveAlwaysEnqueue);
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

    private static void CopyWorkspaceViewModel(SessionWorkspaceViewModel source, SessionWorkspaceViewModel target)
    {
        using var _ = target.SuppressSelectionChangedNotifications();
        target.ModelProviderStatusMarkup = source.ModelProviderStatusMarkup;
        target.ProviderSummaryMarkup = source.ProviderSummaryMarkup;
        target.CanSelectAgentPrompt = source.CanSelectAgentPrompt;
        target.AgentPromptOptions = source.AgentPromptOptions;
        target.SelectedAgentPromptIndex = source.SelectedAgentPromptIndex;
        target.CanSelectModelProvider = source.CanSelectModelProvider;
        target.CanSelectModel = source.CanSelectModel;
        target.CanSelectReasoning = source.CanSelectReasoning;
        target.ModelProviderOptions = source.ModelProviderOptions;
        target.SelectedModelProviderIndex = source.SelectedModelProviderIndex;
        target.ModelOptions = source.ModelOptions;
        target.SelectedModelIndex = source.SelectedModelIndex;
        target.ReasoningOptions = source.ReasoningOptions;
        target.SelectedReasoningIndex = source.SelectedReasoningIndex;
        target.CanShowSessionInfo = source.CanShowSessionInfo;
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
}
