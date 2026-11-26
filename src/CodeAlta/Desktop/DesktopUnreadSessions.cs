namespace CodeAlta.Desktop;

/// <summary>
/// The sessions whose turn ended while the user was not looking at them, until the user looks at them: the Explorer and
/// the tabs mark them. A turn ends as <see cref="DesktopTurnAlertTracker"/> finds it, whether CodeAlta has the focus or
/// not: the session is done, failed, or waits for the user. The user looks at a session while its tab is in front of a
/// pane of a window that has the focus, and a session the user looks at has no mark. A session another session created
/// gets none either: its end goes to that session, as for the notifications.
/// </summary>
/// <remarks>
/// The marks last as long as the application. The readings of the sessions and the windows that say what they show come
/// from different threads: each change is made under one lock, and <see cref="Changed"/> is raised outside it.
/// </remarks>
internal sealed class DesktopUnreadSessions
{
    /// <summary>The most sessions marked at once: past it, the oldest mark goes.</summary>
    internal const int MaximumSessions = 128;

    private readonly Func<IReadOnlyCollection<string>> _inView;
    private readonly Lock _gate = new();
    // The marked sessions, the one whose turn ended first first.
    private readonly List<string> _sessions = [];

    /// <summary>Creates the marks over what the user looks at.</summary>
    /// <param name="inView">The sessions the user looks at now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inView"/> is null.</exception>
    internal DesktopUnreadSessions(Func<IReadOnlyCollection<string>> inView)
    {
        ArgumentNullException.ThrowIfNull(inView);
        _inView = inView;
    }

    /// <summary>Raised when a session got or lost its mark.</summary>
    internal event Action? Changed;

    /// <summary>The marked sessions, the one whose turn ended first first.</summary>
    internal IReadOnlyList<string> Sessions { get { lock (_gate) return [.. _sessions]; } }

    /// <summary>Takes a reading of the sessions: marks those whose turn just ended, unless the user looks at them.</summary>
    /// <param name="alerts">The sessions whose turn ended since the last reading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="alerts"/> is null.</exception>
    internal void Observe(IReadOnlyList<DesktopTurnAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        var inView = InView();
        bool changed;
        lock (_gate)
        {
            changed = ClearLocked(inView);
            foreach (var alert in alerts)
            {
                if (alert.ParentSessionId is not null || inView.Contains(alert.SessionId)) continue;
                var index = _sessions.FindIndex(id => string.Equals(id, alert.SessionId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _sessions.RemoveAt(index);
                else changed = true;
                _sessions.Add(alert.SessionId);
                if (_sessions.Count > MaximumSessions) _sessions.RemoveAt(0);
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>What the user looks at may have changed: the sessions they look at now lose their mark.</summary>
    internal void Look()
    {
        var inView = InView();
        bool changed;
        lock (_gate) changed = ClearLocked(inView);
        if (changed) Changed?.Invoke();
    }

    private HashSet<string> InView() => new(_inView(), StringComparer.OrdinalIgnoreCase);

    private bool ClearLocked(HashSet<string> inView) => inView.Count > 0 && _sessions.RemoveAll(inView.Contains) > 0;
}
