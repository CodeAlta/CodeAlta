using CodeAlta.Orchestration.Runtime;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>What the desktop shows when a session is done or waits for the user while CodeAlta is in the background.</summary>
internal enum DesktopTurnAlertMode
{
    /// <summary>A notification of the system, and the mark of the application: its Dock badge and bounce, its flashing taskbar button.</summary>
    All,

    /// <summary>The mark of the application alone, without a notification.</summary>
    Mark,

    /// <summary>Nothing.</summary>
    Off,
}

/// <summary>Why a session is worth the attention of the user.</summary>
internal enum DesktopTurnAlertKind
{
    /// <summary>It finished its work: no run, no background task.</summary>
    Done,

    /// <summary>It finished its work, and its last run ended with an error.</summary>
    Failed,

    /// <summary>It waits for the user: a question, a command or a change to approve.</summary>
    Waiting,
}

/// <summary>A session that just finished its work or started to wait for the user.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Title">Its title, as the runtime knows it.</param>
/// <param name="Kind">What happened.</param>
internal sealed record DesktopTurnAlert(string SessionId, string Title, DesktopTurnAlertKind Kind);

/// <summary>
/// Finds, between two readings of the sessions, those that just finished their work or started to wait for the user.
/// A session is done when nothing goes on for it any more: no run, and no background task whose result would start
/// another one. A session another session created is done for that session, not for the user, but its questions are
/// the user's.
/// </summary>
internal sealed class DesktopTurnAlertTracker
{
    private Dictionary<string, (bool Busy, bool Waiting)>? _previous;

    /// <summary>Forgets the last reading: the next one only sets what the following ones compare with.</summary>
    internal void Reset() => _previous = null;

    /// <summary>Reads the sessions once and returns what changed since the last reading; nothing at the first one.</summary>
    /// <param name="sessions">What the sessions do now.</param>
    /// <param name="waiting">The sessions that wait for the user now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> or <paramref name="waiting"/> is null.</exception>
    internal IReadOnlyList<DesktopTurnAlert> Observe(IReadOnlyList<SessionRuntimeOverview> sessions, IReadOnlySet<string> waiting)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(waiting);
        var current = new Dictionary<string, (bool Busy, bool Waiting)>(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<DesktopTurnAlert>();
        foreach (var session in sessions)
        {
            var busy = session.Running || session.BackgroundTasks > 0;
            var waits = waiting.Contains(session.SessionId);
            current[session.SessionId] = (busy, waits);
            if (_previous is null) continue;
            // A session the runtime did not hold at the last reading did nothing then.
            var before = _previous.GetValueOrDefault(session.SessionId);
            if (waits && !before.Waiting)
                alerts.Add(new(session.SessionId, session.Title, DesktopTurnAlertKind.Waiting));
            else if (!waits && !busy && before.Busy && session.ParentSessionId is null)
                alerts.Add(new(session.SessionId, session.Title, session.Failed ? DesktopTurnAlertKind.Failed : DesktopTurnAlertKind.Done));
        }

        _previous = current;
        return alerts;
    }
}

/// <summary>What the alerts read and act on: the sessions, the windows, the notifications and the mark of the application.</summary>
internal interface IDesktopTurnAlertSurface
{
    /// <summary>The user's setting, read at each reading so that a change applies at once.</summary>
    DesktopTurnAlertMode Mode { get; }

    /// <summary>Whether a window of the application has the focus: the user sees what the sessions do.</summary>
    bool IsForeground { get; }

    /// <summary>What the sessions do now.</summary>
    IReadOnlyList<SessionRuntimeOverview> ReadSessions();

    /// <summary>The sessions that wait for the user now.</summary>
    ValueTask<IReadOnlyCollection<string>> ReadWaitingAsync(CancellationToken cancellationToken);

    /// <summary>Shows or replaces a notification of the system.</summary>
    ValueTask<NeoDesktopStatus> NotifyAsync(NeoNotificationRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a notification shown before.</summary>
    ValueTask<NeoDesktopStatus> RemoveNotificationAsync(string id, CancellationToken cancellationToken);

    /// <summary>Sets the badge of the application, or clears it with null.</summary>
    ValueTask<NeoDesktopStatus> SetBadgeAsync(string? badge, CancellationToken cancellationToken);

    /// <summary>Asks for the attention of the user: the Dock icon bounces, the taskbar button flashes.</summary>
    ValueTask<NeoDesktopStatus> RequestAttentionAsync(CancellationToken cancellationToken);

    /// <summary>Writes a warning to the log.</summary>
    void Warn(string message);
}

/// <summary>
/// Tells the user, while CodeAlta is in the background, that a session finished its work or waits for them: a
/// notification of the system that opens the session, and the mark of the application (the number of such sessions
/// on the Dock icon, which bounces, on macOS; the flashing taskbar button on Windows). Both go once a window of the
/// application has the focus again.
/// </summary>
/// <remarks>
/// The sessions are read at a short interval rather than observed, as the taskbar progress reads them: a session is
/// seen whatever started it, and nothing is computed for each event of a turn.
/// </remarks>
internal sealed class DesktopTurnAlerts : IAsyncDisposable
{
    /// <summary>How often the sessions are read.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    /// <summary>The start of the identity of the notifications of the sessions; the rest is the session.</summary>
    internal const string NotificationPrefix = "codealta.session.";

    // The longest title and text that every system shows whole (a Windows balloon takes 63 and 255 characters).
    private const int MaximumTitle = 63;
    private const int MaximumBody = 255;

    private readonly IDesktopTurnAlertSurface _surface;
    private readonly DesktopTurnAlertTracker _tracker = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeGate = new();
    // The sessions the user was told about and has not seen since, in the order they came, and the notifications shown.
    private readonly List<string> _unseen = [];
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failures = new(StringComparer.Ordinal);
    private readonly Task _observation;
    private Task? _disposal;
    private bool _marked;

    /// <summary>Creates the alerts over what they read and act on.</summary>
    /// <param name="surface">The sessions, the windows, the notifications and the mark of the application.</param>
    /// <param name="period">How often the sessions are read; null when the caller reads them with <see cref="RefreshAsync"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
    internal DesktopTurnAlerts(IDesktopTurnAlertSurface surface, TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        _observation = period is { } interval ? Task.Run(() => ObserveAsync(interval)) : Task.CompletedTask;
    }

    /// <summary>The sessions the user was told about and has not seen since, the first first.</summary>
    internal IReadOnlyList<string> Unseen { get { lock (_unseen) return [.. _unseen]; } }

    /// <summary>The session a notification of these alerts opens; null for another notification or a dismissal.</summary>
    internal static string? SessionOf(NeoNotificationActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.Dismissed || !activation.NotificationId.StartsWith(NotificationPrefix, StringComparison.Ordinal)) return null;
        return activation.ActivationData is { Length: > 0 } sessionId ? sessionId : null;
    }

    /// <summary>The notification of an alert; null for a session whose identity cannot name one.</summary>
    internal static NeoNotificationRequest? NotificationOf(DesktopTurnAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var id = NotificationPrefix + alert.SessionId;
        if (id.Length > 128 || alert.SessionId.Length > 512 || !alert.SessionId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')) return null;
        var title = string.IsNullOrWhiteSpace(alert.Title) ? "CodeAlta" : Cut(alert.Title.ReplaceLineEndings(" ").Trim(), MaximumTitle);
        var body = alert.Kind switch
        {
            DesktopTurnAlertKind.Failed => "Stopped on an error.",
            DesktopTurnAlertKind.Waiting => "Waits for you: a question or an approval.",
            _ => "Done: ready for your next prompt.",
        };
        return new NeoNotificationRequest { Id = id, Title = title, Body = Cut(body, MaximumBody), ActivationData = alert.SessionId };
    }

    /// <summary>Reads the sessions once, tells the user of what changed while the application is in the background, and clears the mark once it is not.</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mode = _surface.Mode;
            if (mode == DesktopTurnAlertMode.Off)
            {
                // Turned on again, the alerts start from what the sessions do then, not from what they did before.
                _tracker.Reset();
                await ClearAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var waiting = (await _surface.ReadWaitingAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var alerts = _tracker.Observe(_surface.ReadSessions(), waiting);
            if (_surface.IsForeground)
            {
                await ClearAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (alerts.Count == 0) return;
            foreach (var alert in alerts)
            {
                lock (_unseen)
                {
                    _unseen.Remove(alert.SessionId);
                    _unseen.Add(alert.SessionId);
                }

                if (mode != DesktopTurnAlertMode.All || NotificationOf(alert) is not { } notification) continue;
                if (Succeeded("notification", await Try(token => _surface.NotifyAsync(notification, token), cancellationToken).ConfigureAwait(false)))
                    _shown.Add(notification.Id);
            }

            int count;
            lock (_unseen) count = _unseen.Count;
            _marked = true;
            Succeeded("badge", await Try(token => _surface.SetBadgeAsync(count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture), token), cancellationToken).ConfigureAwait(false));
            Succeeded("attention", await Try(_surface.RequestAttentionAsync, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    // The user is back, or turned the alerts off: the mark and the notifications of the sessions go. Called with the gate held.
    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        lock (_unseen) _unseen.Clear();
        if (_marked)
        {
            _marked = false;
            Succeeded("badge", await Try(token => _surface.SetBadgeAsync(null, token), cancellationToken).ConfigureAwait(false));
        }

        if (_shown.Count == 0) return;
        var shown = _shown.ToArray();
        _shown.Clear();
        foreach (var id in shown)
            await Try(token => _surface.RemoveNotificationAsync(id, token), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NeoDesktopStatus> Try(Func<CancellationToken, ValueTask<NeoDesktopStatus>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
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

    // What the platform does not have is no failure; anything else is logged once for each thing, not at each alert.
    private bool Succeeded(string what, NeoDesktopStatus status)
    {
        if (status == NeoDesktopStatus.Success) return true;
        if (status != NeoDesktopStatus.Unsupported && _failures.Add(what))
            _surface.Warn($"The {what} of a session that is done or waits could not be shown: {status}.");
        return false;
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";

    private async Task ObserveAsync(TimeSpan period)
    {
        try
        {
            using var timer = new PeriodicTimer(period);
            do
            {
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposal stops the readings only.
        }
        catch (Exception exception)
        {
            _surface.Warn($"The alerts of the sessions that are done or wait stopped: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        await _observation.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The badge would outlive the application on the Dock; the notifications stay, and open the session at the next start.
            if (_marked)
            {
                _marked = false;
                await Try(token => _surface.SetBadgeAsync(null, token), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        _stop.Dispose();
    }
}

/// <summary>The alerts of a desktop window: the sessions of the runtime, the windows of the application, and its notifications and mark.</summary>
/// <param name="window">The main window, whose taskbar button flashes on Windows.</param>
/// <param name="services">The desktop services.</param>
/// <param name="runtime">The sessions.</param>
/// <param name="waiting">Reads the sessions that wait for the user.</param>
/// <param name="mode">Reads the user's setting.</param>
/// <param name="foreground">Reads whether a window of the application has the focus.</param>
/// <param name="notifications">
/// Whether the notifications of the system can be used: on macOS only in an application bundle, since the notification
/// center of macOS ends a process that has none.
/// </param>
internal sealed class DesktopTurnAlertSurface(
    NeoWindow window,
    NeoDesktopServices services,
    SessionRuntimeService runtime,
    Func<CancellationToken, ValueTask<IReadOnlyCollection<string>>> waiting,
    Func<DesktopTurnAlertMode> mode,
    Func<bool> foreground,
    bool notifications) : IDesktopTurnAlertSurface
{
    private readonly bool _notifications = notifications && services.Notifications.Support.SupportLevel != NeoSupportLevel.None;

    /// <summary>
    /// Whether the notifications of the system can be used in this process: macOS ends a process that asks for its
    /// notification center without being an application bundle, as `alta` started from a terminal is.
    /// </summary>
    internal static bool CanNotify(string? macBundleIdentifier) => !OperatingSystem.IsMacOS() || !string.IsNullOrEmpty(macBundleIdentifier);

    /// <inheritdoc />
    public DesktopTurnAlertMode Mode => mode();

    /// <inheritdoc />
    public bool IsForeground => foreground();

    /// <inheritdoc />
    public IReadOnlyList<SessionRuntimeOverview> ReadSessions() => runtime.ListOverview();

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> ReadWaitingAsync(CancellationToken cancellationToken) => waiting(cancellationToken);

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> NotifyAsync(NeoNotificationRequest request, CancellationToken cancellationToken)
        => _notifications ? services.Notifications.ShowAsync(request, cancellationToken) : ValueTask.FromResult(NeoDesktopStatus.Unsupported);

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> RemoveNotificationAsync(string id, CancellationToken cancellationToken)
        => _notifications ? services.Notifications.RemoveAsync(id, cancellationToken) : ValueTask.FromResult(NeoDesktopStatus.Unsupported);

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> SetBadgeAsync(string? badge, CancellationToken cancellationToken)
        => window.IsClosed || services.WindowPolish.BadgeSupport.SupportLevel == NeoSupportLevel.None ? ValueTask.FromResult(NeoDesktopStatus.Unsupported)
            : services.WindowPolish.SetBadgeAsync(window, badge, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> RequestAttentionAsync(CancellationToken cancellationToken)
        => window.IsClosed || services.WindowPolish.AttentionSupport.SupportLevel == NeoSupportLevel.None ? ValueTask.FromResult(NeoDesktopStatus.Unsupported)
            : services.WindowPolish.RequestAttentionAsync(window, critical: false, cancellationToken);

    /// <inheritdoc />
    public void Warn(string message) => LogManager.GetLogger("CodeAlta.Desktop").Warn(message);
}
