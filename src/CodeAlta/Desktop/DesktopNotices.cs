using System.Globalization;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace CodeAlta.Desktop;

/// <summary>
/// What CodeAlta tells the user for an automation whose run ended, a plugin or a script (<c>notify()</c>), on the channel of the
/// sessions that are done or wait (<see cref="DesktopTurnAlerts"/>): a notification of the system while no window of CodeAlta
/// has the focus and the setting of the notifications shows them, and a notice in the window otherwise. A notice that comes
/// while the window is in the background waits there until it is closed. A click opens the session of the notification, or
/// brings CodeAlta to the front.
/// </summary>
internal sealed class DesktopNotices : IUserNotifications
{
    /// <summary>The start of the identity of these notifications; the rest is their slot.</summary>
    internal const string NotificationPrefix = "codealta.notice.";

    /// <summary>
    /// How many of these notifications the system keeps at once: the next one takes the place of the oldest. The notifications
    /// of the system track a bounded number of identities per process (256 in NeoAstra), shared with the sessions that are done
    /// or wait and with the secrets that ask: notices that nobody clicks must not use them up.
    /// </summary>
    internal const int MaximumShown = 32;

    private readonly IDesktopTurnAlertSurface _surface;
    private readonly Action<PluginNotification, bool> _showInWindow;
    private long _next;
    private int _warned;

    /// <summary>Creates the notices over the surface of the alerts and the window.</summary>
    /// <param name="surface">The windows, the setting of the notifications, and the notifications of the system.</param>
    /// <param name="showInWindow">Shows a notice in the window, and whether it waits there until it is closed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopNotices(IDesktopTurnAlertSurface surface, Action<PluginNotification, bool> showInWindow)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(showInWindow);
        _surface = surface;
        _showInWindow = showInWindow;
    }

    /// <summary>Tells whether a notification of the system is one of these notices, and the session it opens.</summary>
    /// <param name="activation">The activation of a notification.</param>
    /// <param name="sessionId">The session it opens; null to bring CodeAlta to the front.</param>
    /// <returns>True for a notice the user opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activation"/> is null.</exception>
    internal static bool IsNotice(NeoNotificationActivation activation, out string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(activation);
        sessionId = null;
        if (activation.Dismissed || !activation.NotificationId.StartsWith(NotificationPrefix, StringComparison.Ordinal)) return false;
        sessionId = activation.ActivationData is { Length: > 0 } data ? data : null;
        return true;
    }

    /// <summary>The notification of the system of a notice: its title and body cut to what every system shows, and its session when it can name one.</summary>
    /// <param name="notification">The notice.</param>
    /// <param name="number">Its slot, from 0 to <see cref="MaximumShown"/> - 1, which makes its identity.</param>
    /// <returns>The notification.</returns>
    internal static NeoNotificationRequest NotificationOf(PluginNotification notification, long number)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var session = notification.SessionId is { } id && DesktopTurnAlerts.CanActivate(id) ? id : null;
        return new NeoNotificationRequest
        {
            Id = NotificationPrefix + number.ToString(CultureInfo.InvariantCulture),
            Title = DesktopTurnAlerts.Cut(notification.Title.ReplaceLineEndings(" ").Trim(), PluginNotification.MaximumTitleLength),
            Body = DesktopTurnAlerts.Cut(notification.Body?.Trim() ?? string.Empty, PluginNotification.MaximumBodyLength),
            ActivationData = session,
        };
    }

    /// <inheritdoc />
    public async ValueTask<bool> NotifyAsync(PluginNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title, nameof(notification));
        var background = !_surface.IsForeground;
        if (background && _surface.Mode == DesktopTurnAlertMode.All)
        {
            var request = NotificationOf(notification, (Interlocked.Increment(ref _next) - 1) % MaximumShown);
            var status = await ShowAsync(request, cancellationToken).ConfigureAwait(false);
            if (status == NeoDesktopStatus.Success) return true;
            // What the platform does not have is no failure; anything else is logged once, not at each notice.
            if (status != NeoDesktopStatus.Unsupported && Interlocked.Exchange(ref _warned, 1) == 0)
                _surface.Warn($"A notification could not be shown by the system ({status}): it waits in the window.");
        }

        // The user sees the window, or the system does not show it: the window does, and keeps it for a user who is not there.
        _showInWindow(notification, background);
        return true;
    }

    private async Task<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _surface.NotifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NeoDesktopStatus.Failed;
        }
    }
}
