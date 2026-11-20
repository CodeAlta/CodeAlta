using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Where what CodeAlta tells the user on its own reaches them: the notification of a script (<c>notify()</c>) or of an automation
/// whose run ended. In CodeAlta Desktop, a notification of the system while no window of CodeAlta has the focus, and a notice in
/// the window otherwise.
/// </summary>
public interface IUserNotifications
{
    /// <summary>Shows a notification.</summary>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it was shown: false when the host has nowhere to show it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="notification"/> is null.</exception>
    ValueTask<bool> NotifyAsync(PluginNotification notification, CancellationToken cancellationToken);
}
