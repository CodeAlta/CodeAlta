using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.App.Context;

internal sealed class ThreadCommandContext
{
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
    private readonly Action _refreshHeaderAndThreadWorkspace;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action<string, bool, StatusTone> _setShellStatus;
    private readonly Action<OpenThreadState, string, bool, StatusTone> _setThreadStatus;
    private readonly Action<OpenThreadState, Action, string> _tryRenderInteraction;

    public ThreadCommandContext(
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
        Action refreshHeaderAndThreadWorkspace,
        Action refreshCatalogAndThreadWorkspace,
        Action<string, bool, StatusTone> setShellStatus,
        Action<OpenThreadState, string, bool, StatusTone> setThreadStatus,
        Action<OpenThreadState, Action, string> tryRenderInteraction)
    {
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
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setShellStatus);
        ArgumentNullException.ThrowIfNull(setThreadStatus);
        ArgumentNullException.ThrowIfNull(tryRenderInteraction);

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
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
        _setShellStatus = setShellStatus;
        _setThreadStatus = setThreadStatus;
        _tryRenderInteraction = tryRenderInteraction;
    }

    public bool TrySetPromptUnavailableStatus()
        => _trySetPromptUnavailableStatus();

    public Task<WorkThreadDescriptor?> CreateGlobalThreadAsync(string? title = null)
        => _createGlobalThreadAsync(title);

    public Task<WorkThreadDescriptor?> CreateProjectThreadAsync(string? title = null)
        => _createProjectThreadAsync(title);

    public Task PersistViewStateAsync()
        => _persistViewStateAsync();

    public bool GetAutoApproveEnabled()
        => _getAutoApproveEnabled();

    public void ClearDraftInput()
        => _clearDraftInput();

    public void SetReadyStatusForCurrentSelection()
        => _setReadyStatusForCurrentSelection();

    public void ClearThreadInput()
        => _clearThreadInput();

    public bool IsThreadInputEmpty()
        => _isThreadInputEmpty();

    public void RestoreThreadInput(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _restoreThreadInput(prompt);
    }

    public void RefreshHeaderAndThreadWorkspace()
        => _refreshHeaderAndThreadWorkspace();

    public void RefreshCatalogAndThreadWorkspace()
        => _refreshCatalogAndThreadWorkspace();

    public void SetShellStatus(string message, bool showSpinner, StatusTone tone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _setShellStatus(message, showSpinner, tone);
    }

    public void SetThreadStatus(OpenThreadState tab, string message, bool showSpinner, StatusTone tone)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _setThreadStatus(tab, message, showSpinner, tone);
    }

    public void TryRenderInteraction(OpenThreadState tab, Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        _tryRenderInteraction(tab, action, context);
    }
}
