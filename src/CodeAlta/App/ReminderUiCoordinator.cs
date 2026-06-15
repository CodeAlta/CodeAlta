using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class ReminderUiCoordinator : IDisposable
{
    private readonly AltaReminderService _reminderService;
    private readonly ReminderUiCoordinatorPort _port;

    public ReminderUiCoordinator(
        AltaReminderService reminderService,
        ReminderUiCoordinatorPort port)
    {
        ArgumentNullException.ThrowIfNull(reminderService);
        ArgumentNullException.ThrowIfNull(port);

        _reminderService = reminderService;
        _port = port;
        _reminderService.Changed += OnReminderServiceChanged;
    }

    public void Open()
    {
        if (_port.GetSelectedSession() is not { } session)
        {
            _port.SetStatus(SR.T("Select a session before managing reminders."), StatusTone.Warning);
            return;
        }

        new ReminderManagementDialog(
                _reminderService,
                session,
                _port.GetDialogBounds,
                _port.GetFocusTarget,
                _port.SetStatus,
                _port.RefreshProjection)
            .Show();
    }

    public int GetSelectedSessionReminderCount()
        => _port.GetSelectedSession() is { } session ? GetActiveReminderCount(session.SessionId) : 0;

    public bool HasActiveReminder(string sessionId)
        => GetActiveReminderCount(sessionId) > 0;

    public void Dispose()
        => _reminderService.Changed -= OnReminderServiceChanged;

    private int GetActiveReminderCount(string sessionId)
        => _reminderService.List(sessionId, includeCompleted: false).Count;

    private void OnReminderServiceChanged(object? sender, EventArgs e)
        => _port.DispatchToUi(_port.RefreshProjection);
}

internal sealed class ReminderUiCoordinatorPort
{
    public required Func<SessionViewDescriptor?> GetSelectedSession { get; init; }

    public required Func<Rectangle?> GetDialogBounds { get; init; }

    public required Func<Visual?> GetFocusTarget { get; init; }

    public required Action<string, StatusTone> SetStatus { get; init; }

    public required Action<Action> DispatchToUi { get; init; }

    public required Action RefreshProjection { get; init; }
}
