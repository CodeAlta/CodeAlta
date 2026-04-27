using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Threading;

namespace CodeAlta.App.Context;

internal sealed class ThreadCommandContext
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _trySetPromptUnavailableStatus;
    private readonly Func<string?, Task<WorkThreadDescriptor?>> _createGlobalThreadAsync;
    private readonly Func<string?, Task<WorkThreadDescriptor?>> _createProjectThreadAsync;
    private readonly Func<Task> _persistViewStateAsync;
    private readonly Func<bool> _getAutoApproveEnabled;
    private readonly Action _clearDraftInput;
    private readonly Action _setReadyStatusForCurrentSelection;
    private readonly Action _clearThreadInput;
    private readonly Func<bool> _isThreadInputEmpty;
    private readonly Action<string> _restoreThreadInput;
    private readonly Func<IReadOnlyList<PromptImageAttachment>> _snapshotPromptImages;
    private readonly Action<IReadOnlyList<PromptImageAttachment>> _restorePromptImages;
    private readonly Action _refreshHeaderAndThreadWorkspace;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action<string, bool, StatusTone> _setShellStatus;
    private readonly Action<OpenThreadState, string, bool, StatusTone> _setThreadStatus;
    private readonly Action<OpenThreadState, Action, string> _tryRenderInteraction;

    public ThreadCommandContext(
        IUiDispatcher uiDispatcher,
        Func<bool> trySetPromptUnavailableStatus,
        Func<string?, Task<WorkThreadDescriptor?>> createGlobalThreadAsync,
        Func<string?, Task<WorkThreadDescriptor?>> createProjectThreadAsync,
        Func<Task> persistViewStateAsync,
        Func<bool> getAutoApproveEnabled,
        Action clearDraftInput,
        Action setReadyStatusForCurrentSelection,
        Action clearThreadInput,
        Func<bool> isThreadInputEmpty,
        Action<string> restoreThreadInput,
        Func<IReadOnlyList<PromptImageAttachment>> snapshotPromptImages,
        Action<IReadOnlyList<PromptImageAttachment>> restorePromptImages,
        Action refreshHeaderAndThreadWorkspace,
        Action refreshCatalogAndThreadWorkspace,
        Action<string, bool, StatusTone> setShellStatus,
        Action<OpenThreadState, string, bool, StatusTone> setThreadStatus,
        Action<OpenThreadState, Action, string> tryRenderInteraction)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(trySetPromptUnavailableStatus);
        ArgumentNullException.ThrowIfNull(createGlobalThreadAsync);
        ArgumentNullException.ThrowIfNull(createProjectThreadAsync);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(clearDraftInput);
        ArgumentNullException.ThrowIfNull(setReadyStatusForCurrentSelection);
        ArgumentNullException.ThrowIfNull(clearThreadInput);
        ArgumentNullException.ThrowIfNull(isThreadInputEmpty);
        ArgumentNullException.ThrowIfNull(restoreThreadInput);
        ArgumentNullException.ThrowIfNull(snapshotPromptImages);
        ArgumentNullException.ThrowIfNull(restorePromptImages);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setShellStatus);
        ArgumentNullException.ThrowIfNull(setThreadStatus);
        ArgumentNullException.ThrowIfNull(tryRenderInteraction);

        _uiDispatcher = uiDispatcher;
        _trySetPromptUnavailableStatus = trySetPromptUnavailableStatus;
        _createGlobalThreadAsync = createGlobalThreadAsync;
        _createProjectThreadAsync = createProjectThreadAsync;
        _persistViewStateAsync = persistViewStateAsync;
        _getAutoApproveEnabled = getAutoApproveEnabled;
        _clearDraftInput = clearDraftInput;
        _setReadyStatusForCurrentSelection = setReadyStatusForCurrentSelection;
        _clearThreadInput = clearThreadInput;
        _isThreadInputEmpty = isThreadInputEmpty;
        _restoreThreadInput = restoreThreadInput;
        _snapshotPromptImages = snapshotPromptImages;
        _restorePromptImages = restorePromptImages;
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
        _setShellStatus = setShellStatus;
        _setThreadStatus = setThreadStatus;
        _tryRenderInteraction = tryRenderInteraction;
    }

    public bool TrySetPromptUnavailableStatus()
        => UiDispatch.Invoke(_uiDispatcher, _trySetPromptUnavailableStatus);

    public Task<WorkThreadDescriptor?> CreateGlobalThreadAsync(string? title = null)
        => _createGlobalThreadAsync(title);

    public Task<WorkThreadDescriptor?> CreateProjectThreadAsync(string? title = null)
        => _createProjectThreadAsync(title);

    public Task PersistViewStateAsync()
        => _persistViewStateAsync();

    public bool GetAutoApproveEnabled()
        => UiDispatch.Invoke(_uiDispatcher, _getAutoApproveEnabled);

    public void ClearDraftInput()
        => UiDispatch.Invoke(_uiDispatcher, _clearDraftInput);

    public void SetReadyStatusForCurrentSelection()
        => UiDispatch.Invoke(_uiDispatcher, _setReadyStatusForCurrentSelection);

    public void ClearThreadInput()
        => UiDispatch.Invoke(_uiDispatcher, _clearThreadInput);

    public bool IsThreadInputEmpty()
        => UiDispatch.Invoke(_uiDispatcher, _isThreadInputEmpty);

    public void RestoreThreadInput(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        UiDispatch.Invoke(_uiDispatcher, () => _restoreThreadInput(prompt));
    }

    public PromptSubmission CaptureThreadInput(string? promptText)
        => UiDispatch.Invoke(_uiDispatcher, () => PromptSubmission.Create(promptText, _snapshotPromptImages()));

    public void RestoreThreadInput(PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        UiDispatch.Invoke(_uiDispatcher, () =>
        {
            _restoreThreadInput(prompt.Text);
            _restorePromptImages(prompt.Images);
        });
    }

    public void RefreshHeaderAndThreadWorkspace()
        => UiDispatch.Invoke(_uiDispatcher, _refreshHeaderAndThreadWorkspace);

    public void RefreshCatalogAndThreadWorkspace()
        => UiDispatch.Invoke(_uiDispatcher, _refreshCatalogAndThreadWorkspace);

    public void SetShellStatus(string message, bool showSpinner, StatusTone tone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        UiDispatch.Invoke(_uiDispatcher, () => _setShellStatus(message, showSpinner, tone));
    }

    public void SetThreadStatus(OpenThreadState tab, string message, bool showSpinner, StatusTone tone)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        UiDispatch.Invoke(_uiDispatcher, () => _setThreadStatus(tab, message, showSpinner, tone));
    }

    public void TryRenderInteraction(OpenThreadState tab, Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        UiDispatch.Invoke(_uiDispatcher, () => _tryRenderInteraction(tab, action, context));
    }
}
