using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal readonly record struct ShellStatusUpdate(string Message, bool ShowSpinner, StatusTone Tone);

internal readonly record struct SessionStatusUpdate(string Message, bool ShowSpinner, StatusTone Tone);

internal interface IShellStatusPort
{
    void SetShellStatus(ShellStatusUpdate update);

    void SetSessionStatus(OpenSessionState session, SessionStatusUpdate update);

    void ClearSessionStatus(OpenSessionState session);

    void SetProviderSessionLoadStatus(string? message);
}

internal sealed class ShellStatusPort : IShellStatusPort
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Action<string, bool, StatusTone> _setShellStatus;
    private readonly Action<OpenSessionState, string, bool, StatusTone> _setSessionStatus;
    private readonly Action<OpenSessionState> _clearSessionStatus;
    private readonly Action<string?> _setProviderSessionLoadStatus;

    public ShellStatusPort(
        IUiDispatcher uiDispatcher,
        Action<string, bool, StatusTone> setShellStatus,
        Action<OpenSessionState, string, bool, StatusTone> setSessionStatus,
        Action<OpenSessionState>? clearSessionStatus = null,
        Action<string?>? setProviderSessionLoadStatus = null)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(setShellStatus);
        ArgumentNullException.ThrowIfNull(setSessionStatus);

        _uiDispatcher = uiDispatcher;
        _setShellStatus = setShellStatus;
        _setSessionStatus = setSessionStatus;
        _clearSessionStatus = clearSessionStatus ?? (_ => { });
        _setProviderSessionLoadStatus = setProviderSessionLoadStatus ?? (_ => { });
    }

    public void SetShellStatus(ShellStatusUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Message);
        _uiDispatcher.Invoke(() => _setShellStatus(update.Message, update.ShowSpinner, update.Tone));
    }

    public void SetSessionStatus(OpenSessionState session, SessionStatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Message);
        _uiDispatcher.Invoke(() => _setSessionStatus(session, update.Message, update.ShowSpinner, update.Tone));
    }

    public void ClearSessionStatus(OpenSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _uiDispatcher.Invoke(() => _clearSessionStatus(session));
    }

    public void SetProviderSessionLoadStatus(string? message)
        => _uiDispatcher.Invoke(() => _setProviderSessionLoadStatus(message));
}
