using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>Turns on again, when CodeAlta starts, the Remote Control of the sessions that had it on.</summary>
internal static class DesktopRemoteControl
{
    /// <summary>
    /// Connects each remembered session again, one after the other, in the background. A session that no longer
    /// exists, or whose provider has no Remote Control, is forgotten; one that fails stays remembered, and says why
    /// in its state.
    /// </summary>
    internal static void Reconnect(OwnedSessionCommandService commands, DesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(shell);
        var sessions = shell.RemoteControlSessions.ToArray();
        if (sessions.Length == 0) return;
        _ = Task.Run(() => ReconnectAsync(sessions,
            async sessionId => (await commands.SetRemoteControlAsync(sessionId, true).ConfigureAwait(false)).Status,
            sessionId => shell.NoteRemoteControl(sessionId, false),
            static (sessionId, exception) => LogManager.GetLogger("CodeAlta.Desktop").Warn($"Remote Control of session {sessionId} could not be turned on again: {exception.Message}")));
    }

    /// <summary>
    /// Turns on the Remote Control of each session, one after the other. A session that is <c>unavailable</c> is
    /// forgotten; once the host is <c>closed</c> nothing more is asked; one that cannot be turned on does not keep the
    /// others from being connected.
    /// </summary>
    /// <param name="sessions">The sessions, the last turned on first.</param>
    /// <param name="turnOn">Turns the Remote Control of a session on and gives the status of the request.</param>
    /// <param name="forget">Forgets a session.</param>
    /// <param name="warn">Says why a session could not be turned on.</param>
    internal static async Task ReconnectAsync(IReadOnlyList<string> sessions, Func<string, Task<string>> turnOn, Action<string> forget,
        Action<string, Exception> warn)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(turnOn);
        ArgumentNullException.ThrowIfNull(forget);
        ArgumentNullException.ThrowIfNull(warn);
        foreach (var sessionId in sessions)
        {
            try
            {
                var status = await turnOn(sessionId).ConfigureAwait(false);
                if (status == "unavailable") forget(sessionId);
                if (status == "closed") return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One session that cannot be attached does not keep the others from being connected.
                warn(sessionId, exception);
            }
        }
    }
}
