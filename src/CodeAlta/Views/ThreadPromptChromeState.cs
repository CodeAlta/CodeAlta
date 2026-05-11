using CodeAlta.ViewModels;

namespace CodeAlta.Views;

internal sealed class ThreadPromptChromeState
{
    public ThreadPromptChromeState(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
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

    public ThreadWorkspaceViewModel WorkspaceViewModel { get; }

    public PromptComposerViewModel PromptComposerViewModel { get; }

    public static ThreadPromptChromeState CloneFrom(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel)
    {
        var state = new ThreadPromptChromeState(
            new CodeAltaShellViewModel(),
            new ThreadWorkspaceViewModel(),
            new PromptComposerViewModel());
        state.ApplyProjection(shellViewModel, workspaceViewModel, promptComposerViewModel, preserveAlwaysEnqueue: false);
        return state;
    }

    public void ApplyProjection(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
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

    private static void CopyWorkspaceViewModel(ThreadWorkspaceViewModel source, ThreadWorkspaceViewModel target)
    {
        using var _ = target.SuppressSelectionChangedNotifications();
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
}
