using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal interface ISessionLifecycleCommandPort
{
    Task<SessionViewDescriptor?> CreateGlobalSessionAsync(string? title = null);

    Task<SessionViewDescriptor?> CreateProjectSessionAsync(string? title = null);

    Task PersistViewStateAsync();

    void RekeySessionIdentity(string oldSessionId, SessionViewDescriptor session);
}

internal sealed class DelegatingSessionLifecycleCommandPort : ISessionLifecycleCommandPort
{
    private readonly Func<string?, Task<SessionViewDescriptor?>> _createGlobalSessionAsync;
    private readonly Func<string?, Task<SessionViewDescriptor?>> _createProjectSessionAsync;
    private readonly Func<Task> _persistViewStateAsync;
    private readonly Action<string, SessionViewDescriptor>? _rekeySessionIdentity;

    public DelegatingSessionLifecycleCommandPort(
        Func<string?, Task<SessionViewDescriptor?>> createGlobalSessionAsync,
        Func<string?, Task<SessionViewDescriptor?>> createProjectSessionAsync,
        Func<Task> persistViewStateAsync,
        Action<string, SessionViewDescriptor>? rekeySessionIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(createGlobalSessionAsync);
        ArgumentNullException.ThrowIfNull(createProjectSessionAsync);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);

        _createGlobalSessionAsync = createGlobalSessionAsync;
        _createProjectSessionAsync = createProjectSessionAsync;
        _persistViewStateAsync = persistViewStateAsync;
        _rekeySessionIdentity = rekeySessionIdentity;
    }

    public Task<SessionViewDescriptor?> CreateGlobalSessionAsync(string? title = null)
        => _createGlobalSessionAsync(title);

    public Task<SessionViewDescriptor?> CreateProjectSessionAsync(string? title = null)
        => _createProjectSessionAsync(title);

    public Task PersistViewStateAsync()
        => _persistViewStateAsync();

    public void RekeySessionIdentity(string oldSessionId, SessionViewDescriptor session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSessionId);
        ArgumentNullException.ThrowIfNull(session);
        _rekeySessionIdentity?.Invoke(oldSessionId, session);
    }
}

internal interface ISessionCommandUiPort
{
    bool TrySetPromptUnavailableStatus();

    bool GetAutoApproveEnabled();

    void ClearDraftInput();

    void SetReadyStatusForCurrentSelection();

    void ApplyHeaderProjection();

    void ApplyCatalogProjection();

    void TryRenderInteraction(OpenSessionState tab, Action action, string context);
}

internal sealed class SessionCommandUiPort : ISessionCommandUiPort
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _trySetPromptUnavailableStatus;
    private readonly Func<bool> _getAutoApproveEnabled;
    private readonly Action _clearDraftInput;
    private readonly Action _setReadyStatusForCurrentSelection;
    private readonly Action _refreshHeaderAndSessionWorkspace;
    private readonly Action _refreshCatalogAndSessionWorkspace;
    private readonly Action<OpenSessionState, Action, string> _tryRenderInteraction;

    public SessionCommandUiPort(
        IUiDispatcher uiDispatcher,
        Func<bool> trySetPromptUnavailableStatus,
        Func<bool> getAutoApproveEnabled,
        Action clearDraftInput,
        Action setReadyStatusForCurrentSelection,
        Action refreshHeaderAndSessionWorkspace,
        Action refreshCatalogAndSessionWorkspace,
        Action<OpenSessionState, Action, string> tryRenderInteraction)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(trySetPromptUnavailableStatus);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(clearDraftInput);
        ArgumentNullException.ThrowIfNull(setReadyStatusForCurrentSelection);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndSessionWorkspace);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndSessionWorkspace);
        ArgumentNullException.ThrowIfNull(tryRenderInteraction);

        _uiDispatcher = uiDispatcher;
        _trySetPromptUnavailableStatus = trySetPromptUnavailableStatus;
        _getAutoApproveEnabled = getAutoApproveEnabled;
        _clearDraftInput = clearDraftInput;
        _setReadyStatusForCurrentSelection = setReadyStatusForCurrentSelection;
        _refreshHeaderAndSessionWorkspace = refreshHeaderAndSessionWorkspace;
        _refreshCatalogAndSessionWorkspace = refreshCatalogAndSessionWorkspace;
        _tryRenderInteraction = tryRenderInteraction;
    }

    public bool TrySetPromptUnavailableStatus()
        => _uiDispatcher.Invoke(_trySetPromptUnavailableStatus);

    public bool GetAutoApproveEnabled()
        => _uiDispatcher.Invoke(_getAutoApproveEnabled);

    public void ClearDraftInput()
        => _uiDispatcher.Invoke(_clearDraftInput);

    public void SetReadyStatusForCurrentSelection()
        => _uiDispatcher.Invoke(_setReadyStatusForCurrentSelection);

    public void ApplyHeaderProjection()
        => _uiDispatcher.Invoke(_refreshHeaderAndSessionWorkspace);

    public void ApplyCatalogProjection()
        => _uiDispatcher.Invoke(_refreshCatalogAndSessionWorkspace);

    public void TryRenderInteraction(OpenSessionState tab, Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        _uiDispatcher.Invoke(() => _tryRenderInteraction(tab, action, context));
    }
}
