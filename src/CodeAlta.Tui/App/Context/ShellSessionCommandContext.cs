using CodeAlta.Tui.App.State;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App.Context;

internal sealed class ShellSessionCommandContext
{
    private readonly ISessionLifecycleCommandPort _sessionLifecyclePort;
    private readonly ISessionCommandUiPort _uiPort;
    private readonly IPromptSessionPort _promptSessionPort;
    private readonly Func<PromptSessionId> _getCurrentPromptSessionId;
    private readonly IShellStatusPort _statusPort;

    public ShellSessionCommandContext(
        ISessionLifecycleCommandPort sessionLifecyclePort,
        ISessionCommandUiPort uiPort,
        IPromptSessionPort promptSessionPort,
        Func<PromptSessionId> getCurrentPromptSessionId,
        IShellStatusPort statusPort)
    {
        ArgumentNullException.ThrowIfNull(sessionLifecyclePort);
        ArgumentNullException.ThrowIfNull(uiPort);
        ArgumentNullException.ThrowIfNull(promptSessionPort);
        ArgumentNullException.ThrowIfNull(getCurrentPromptSessionId);
        ArgumentNullException.ThrowIfNull(statusPort);

        _sessionLifecyclePort = sessionLifecyclePort;
        _uiPort = uiPort;
        _promptSessionPort = promptSessionPort;
        _getCurrentPromptSessionId = getCurrentPromptSessionId;
        _statusPort = statusPort;
    }

    public bool TrySetPromptUnavailableStatus()
        => _uiPort.TrySetPromptUnavailableStatus();

    public Task<SessionViewDescriptor?> CreateGlobalSessionAsync(string? title = null)
        => _sessionLifecyclePort.CreateGlobalSessionAsync(title);

    public Task<SessionViewDescriptor?> CreateProjectSessionAsync(string? title = null)
        => _sessionLifecyclePort.CreateProjectSessionAsync(title);

    public Task PersistViewStateAsync()
        => _sessionLifecyclePort.PersistViewStateAsync();

    public bool GetAutoApproveEnabled()
        => _uiPort.GetAutoApproveEnabled();

    public void ClearDraftInput()
        => _uiPort.ClearDraftInput();

    public void SetReadyStatusForCurrentSelection()
        => _uiPort.SetReadyStatusForCurrentSelection();

    public void ClearSessionInput()
        => _promptSessionPort.ClearPrompt(GetCurrentPromptSessionId());

    public bool IsSessionInputEmpty()
        => _promptSessionPort.IsPromptEmpty(GetCurrentPromptSessionId());

    public void RestoreSessionInput(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _promptSessionPort.RestorePrompt(GetCurrentPromptSessionId(), PromptSubmission.Create(prompt));
    }

    public PromptSubmission CaptureSessionInput(string? promptText)
        => _promptSessionPort.CapturePrompt(GetCurrentPromptSessionId(), promptText);

    public void RestoreSessionInput(PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _promptSessionPort.RestorePrompt(GetCurrentPromptSessionId(), prompt);
    }

    public void ApplyHeaderProjection()
        => _uiPort.ApplyHeaderProjection();

    public void ApplyCatalogProjection()
        => _uiPort.ApplyCatalogProjection();

    public void RekeySessionIdentity(string oldSessionId, SessionViewDescriptor session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSessionId);
        ArgumentNullException.ThrowIfNull(session);
        _sessionLifecyclePort.RekeySessionIdentity(oldSessionId, session);
    }

    public void SetShellStatus(string message, bool showSpinner, StatusTone tone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _statusPort.SetShellStatus(new ShellStatusUpdate(message, showSpinner, tone));
    }

    public void SetSessionStatus(OpenSessionState tab, string message, bool showSpinner, StatusTone tone)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _statusPort.SetSessionStatus(tab, new SessionStatusUpdate(message, showSpinner, tone));
    }

    public void TryRenderInteraction(OpenSessionState tab, Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        _uiPort.TryRenderInteraction(tab, action, context);
    }

    private PromptSessionId GetCurrentPromptSessionId()
    {
        var promptSessionId = _getCurrentPromptSessionId();
        if (promptSessionId.IsEmpty)
        {
            throw new InvalidOperationException("The current prompt session id cannot be empty.");
        }

        return promptSessionId;
    }
}
