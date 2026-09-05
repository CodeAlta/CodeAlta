using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App;

internal interface IShellStatusService
{
    void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info);
}

internal sealed class DelegatingShellStatusService : IShellStatusService
{
    private readonly Action<string, bool, StatusTone> _setStatus;

    public DelegatingShellStatusService(Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(setStatus);
        _setStatus = setStatus;
    }

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _setStatus(message, showSpinner, tone);
    }
}
