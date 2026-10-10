using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Tray;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>What a request to close the window, or to exit, leads to.</summary>
internal enum DesktopCloseAction
{
    /// <summary>The window is hidden; the application keeps running behind its tray icon.</summary>
    Hide,

    /// <summary>The page asks whether the application keeps running behind its tray icon or exits.</summary>
    Ask,

    /// <summary>The page is asked to exit: it has its own questions first (unsaved files), then asks to exit.</summary>
    RequestExit,

    /// <summary>The page asks first: sessions are running, or terminals run a command, and exiting stops them.</summary>
    ConfirmExit,

    /// <summary>The application exits.</summary>
    Exit,
}

/// <summary>
/// A notice for the page: <c>exit-requested</c> (the tray's Exit, or a closed window that cannot stay hidden),
/// <c>confirm-exit</c>, which carries the number of running sessions and of terminals that run a command,
/// <c>confirm-close</c> (the window was closed, and the user has not said yet what that does),
/// <c>entry-added</c> (the application was just added to the desktop's applications) and
/// <c>sessions-changed</c> (something outside the window may have created or changed sessions).
/// </summary>
internal sealed record DesktopShellEvent(string Kind, int RunningSessions, int BusyTerminals = 0, int SessionWidth = 0, string? SessionId = null);

/// <summary>
/// How the application lives beyond its window: an icon in the notification area (the menu bar on macOS, the
/// status area on Linux) with <c>Open</c> and <c>Exit</c>, the application's menu bar on macOS, a window that
/// can be closed without exiting (the user is asked until an answer is remembered), and a question before an
/// exit that would stop running sessions.
/// </summary>
internal sealed class DesktopShell
{
    internal const string OpenCommand = "codealta.shell.open";
    internal const string ExitCommand = "codealta.shell.exit";

    private readonly NeoWindow _window;
    private readonly NeoDispatcher _dispatcher;
    private readonly string _dataRoot;
    private readonly Action _exit;
    private readonly Lock _gate = new();
    private readonly List<Action<DesktopShellEvent>> _watchers = [];
    // The width some sessions are shown with instead of the user's setting: what `alta appearance set` changes.
    private readonly Dictionary<string, int> _sessionWidths = new(StringComparer.OrdinalIgnoreCase);
    private DesktopPreferences _preferences;
    private bool _tray;
    private bool _commands;
    private bool _exiting;
    private bool _entryAdded;
    private int _pickingFolder;

    /// <param name="window">The main window.</param>
    /// <param name="dispatcher">The dispatcher of the window's thread.</param>
    /// <param name="dataRoot">Where the preferences are kept.</param>
    /// <param name="exit">Starts the application's shutdown; called once.</param>
    internal DesktopShell(NeoWindow window, NeoDispatcher dispatcher, string dataRoot, Action exit)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(exit);
        _window = window;
        _dispatcher = dispatcher;
        _dataRoot = dataRoot;
        _exit = exit;
        _preferences = DesktopPreferences.Load(dataRoot).AtStart();
    }

    /// <summary>The number of sessions with a run in flight; zero until there is a host to ask.</summary>
    internal Func<int>? RunningSessions { get; set; }

    /// <summary>The number of terminals whose shell runs a command; zero until there are terminals to ask.</summary>
    internal Func<int>? BusyTerminals { get; set; }

    /// <summary>The desktop's dialogs; null until the desktop services have started.</summary>
    internal NeoAstra.Desktop.Dialogs.INeoDialogs? Dialogs { get; set; }

    /// <summary>
    /// Lets the user choose a folder with the dialog of the operating system, over the main window. One
    /// dialog is shown at a time.
    /// </summary>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="initialDirectory">The folder shown first, when it exists.</param>
    /// <param name="cancellationToken">Cancels the wait where the platform dialog can be canceled.</param>
    /// <returns>The pick, or null while another folder dialog is open.</returns>
    internal async Task<DesktopFolderPick?> PickFolderAsync(string title, string? initialDirectory, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _pickingFolder, 1, 0) != 0) return null;
        try
        {
            return await DesktopFolderPicker.PickAsync(_window, _dispatcher, Dialogs, title, initialDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _pickingFolder, 0);
        }
    }

    /// <summary>What closing the window does: a question, until the user's answer is remembered.</summary>
    internal DesktopCloseBehavior OnClose { get { lock (_gate) return _preferences.OnClose; } }

    /// <summary>
    /// True when this start added the application to the desktop (the Start Menu, the Applications folder,
    /// the applications menu): the page says so once.
    /// </summary>
    internal bool EntryAdded { get { lock (_gate) return _entryAdded; } }

    /// <summary>Records that the application was just added to the desktop, and tells the page.</summary>
    internal void NotifyEntryAdded()
    {
        lock (_gate) _entryAdded = true;
        Publish(new("entry-added", 0));
    }

    /// <summary>
    /// Tells the page that something outside it may have created or changed sessions (a command of a client of
    /// the MCP server): it reads its list again.
    /// </summary>
    internal void NotifySessionsChanged() => Publish(new("sessions-changed", 0));

    /// <summary>
    /// Tells the page that the plugins of a package were started, replaced or stopped: it reads again their
    /// commands, their shortcuts, their pickers and what they show.
    /// </summary>
    internal void NotifyPluginsChanged() => Publish(new("plugins-changed", 0));

    /// <summary>Whether the tray icon exists: without it a hidden window could not be brought back.</summary>
    internal bool TrayAvailable { get { lock (_gate) return _tray; } }

    /// <summary>
    /// True once the workspace runs behind the window. Before that (start-up, the repair of a configuration
    /// file) there is nothing to keep running: closing the window exits.
    /// </summary>
    internal bool HasWorkspace { get; set; }

    /// <summary>
    /// Whether a closed window can stay hidden: there is a workspace to keep, and a tray icon to bring the
    /// window back or, on macOS, the Dock icon, which every running application keeps.
    /// </summary>
    internal bool CanHide => HasWorkspace && (TrayAvailable || OperatingSystem.IsMacOS());

    /// <summary>
    /// What a close request leads to. Only what the user asks for can hide the window or wait for an answer;
    /// the end of the session and anything that cannot be canceled exit at once. Where the window can stay
    /// hidden, the user chooses: asked by the page until the answer is remembered, and hidden, as by default,
    /// when no page can ask.
    /// </summary>
    internal static DesktopCloseAction Decide(NeoWindowCloseReason reason, bool canCancel, DesktopCloseBehavior onClose, bool canHide, bool hasPage)
    {
        if (!canCancel || reason is NeoWindowCloseReason.SessionEnd or NeoWindowCloseReason.System or NeoWindowCloseReason.Owner) return DesktopCloseAction.Exit;
        if (reason is NeoWindowCloseReason.User or NeoWindowCloseReason.Programmatic && canHide && onClose != DesktopCloseBehavior.Exit)
            return onClose == DesktopCloseBehavior.Ask && hasPage ? DesktopCloseAction.Ask : DesktopCloseAction.Hide;
        return DecideUserExit(hasPage);
    }

    /// <summary>What Exit in the tray leads to: the page's own exit, or an exit at once when no page listens.</summary>
    internal static DesktopCloseAction DecideUserExit(bool hasPage) => hasPage ? DesktopCloseAction.RequestExit : DesktopCloseAction.Exit;

    /// <summary>What the page's exit leads to: a question while sessions run, unless it was already answered.</summary>
    internal static DesktopCloseAction DecideExit(bool confirmed, int runningSessions, bool canAsk)
        => !confirmed && runningSessions > 0 && canAsk ? DesktopCloseAction.ConfirmExit : DesktopCloseAction.Exit;

    /// <summary>
    /// Whether the application puts an icon in the notification area. Not <c>CodeAlta.app</c> on macOS, which
    /// stays in the Dock: its process is the script of the bundle that became the tool, which the menu bar no
    /// longer takes for the application the system started. It refuses the icon, AppKit asks again every
    /// second, and one of these requests can block the window's thread for good (seen on macOS 26).
    /// </summary>
    /// <param name="macOS">Whether the platform is macOS.</param>
    /// <param name="bundleIdentifier">The identifier of the bundle the process was started as; null for none.</param>
    internal static bool HasTrayIcon(bool macOS, string? bundleIdentifier) => !macOS || bundleIdentifier is null;

    /// <summary>
    /// Puts the icon in the notification area. Where the platform has none, closing the window exits, except on
    /// macOS, where the Dock keeps the application.
    /// </summary>
    internal async ValueTask StartTrayAsync(NeoDesktopServices services, bool developer)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            if (services.Tray.Support.SupportLevel is NeoSupportLevel.None or NeoSupportLevel.Emulated) return;
            if (OperatingSystem.IsMacOS() && !HasTrayIcon(macOS: true, DesktopIntegration.MacRunningBundleIdentifier())) return;
            RegisterCommands(services);
            services.Tray.Activated += (_, activation) => { if (!activation.Secondary) Show(); };
            var name = DisplayName(developer);
            // Windows takes an .ico; macOS an image at the menu bar's size; Linux the application's icon.
            var icon = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "alta.ico" : OperatingSystem.IsMacOS() ? "alta-tray.png" : "alta.png");
            await services.Tray.SetAsync(new NeoTrayItemOptions
            {
                Id = developer ? "codealta-dev" : "codealta", ToolTip = name, IconPath = File.Exists(icon) ? icon : null,
                Menu =
                [
                    NeoMenuItem.Command("open", "Open " + name, OpenCommand),
                    NeoMenuItem.Separator("separator"),
                    NeoMenuItem.Command("exit", "Exit", ExitCommand),
                ],
            }).ConfigureAwait(true);
            lock (_gate) _tray = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No status area on this desktop (a Linux session without one, say): the window is the application.
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The notification area icon is unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Gives the application its menu bar on macOS, with the standard shortcuts; elsewhere the window has no
    /// menu. Quit is the tray's Exit, so the page's questions come first.
    /// </summary>
    internal async ValueTask StartApplicationMenuAsync(NeoDesktopServices services, bool developer)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            RegisterCommands(services);
            // A command can be activated off the window's thread, and AppKit is called only on it.
            DesktopApplicationMenu.Register(services.Menus.Commands, selector => OnWindowThread(() => { if (OperatingSystem.IsMacOS()) DesktopApplicationMenu.SendAction(selector); }));
            await services.Menus.SetMenuAsync(DesktopApplicationMenu.Target, DesktopApplicationMenu.Build(DisplayName(developer), ExitCommand)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The application menu is unavailable: {exception.Message}");
        }
    }

    // The tray's menu and the application's menu share Open and Exit; a command is registered once.
    private void RegisterCommands(NeoDesktopServices services)
    {
        lock (_gate)
        {
            if (_commands) return;
            _commands = true;
        }

        services.Menus.Commands.Register(OpenCommand, _ => { Show(); return ValueTask.CompletedTask; });
        services.Menus.Commands.Register(ExitCommand, _ => { RequestUserExit(); return ValueTask.CompletedTask; });
    }

    private static string DisplayName(bool developer) => developer ? "CodeAlta (dev)" : "CodeAlta";

    /// <summary>Answers a request to close the window; anything but an exit has canceled it.</summary>
    internal void Close(NeoWindowCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Cancel();
        Apply(Decide(request.Reason, request.CanCancel, OnClose, CanHide, HasWatchers()), 0);
    }

    /// <summary>
    /// Exit as the user asks for it outside the page (the tray's menu, <c>alta --exit</c>): the page exits in
    /// its own way, with its questions; without a page the application exits at once.
    /// </summary>
    internal void RequestUserExit() => Apply(DecideUserExit(HasWatchers()), 0);

    /// <summary>The page's exit: the application exits, after the page's question while sessions run or terminals run a command.</summary>
    internal void RequestExit(bool confirmed)
    {
        var running = Count(RunningSessions);
        var terminals = Count(BusyTerminals);
        Apply(DecideExit(confirmed, running + terminals, HasWatchers()), running, terminals);
    }

    /// <summary>Shows the window again and brings it to the front.</summary>
    internal void Show() => OnWindowThread(() =>
    {
        if (_window.IsClosed) return;
        _window.Show();
        if (_window.State == NeoWindowState.Minimized) _window.Restore();
        _window.Activate();
    });

    /// <summary>Changes what closing the window does, and keeps the choice.</summary>
    internal void SetOnClose(DesktopCloseBehavior value)
    {
        DesktopPreferences preferences;
        lock (_gate) preferences = _preferences = _preferences with { OnClose = value };
        preferences.Save(_dataRoot);
    }

    /// <summary>
    /// The user's setting: whether the commands and the file changes of a session are reviewed instead of being
    /// approved automatically. Read at every send, so a change applies to the next one.
    /// </summary>
    internal bool ReviewPermissions { get { lock (_gate) return _preferences.ReviewPermissions; } }

    /// <summary>Changes that setting and keeps it.</summary>
    /// <param name="value">Whether commands and file changes are reviewed.</param>
    internal void SetReviewPermissions(bool value)
    {
        DesktopPreferences? preferences = null;
        lock (_gate)
        {
            if (_preferences.ReviewPermissions != value) preferences = _preferences = _preferences with { ReviewPermissions = value };
        }

        preferences?.Save(_dataRoot);
    }

    /// <summary>
    /// The user's setting: whether a session that another session creates takes the permission mode of its
    /// creator, instead of not asking. Read when a session is created.
    /// </summary>
    internal bool InheritPermissions { get { lock (_gate) return _preferences.InheritPermissions; } }

    /// <summary>Changes that setting and keeps it.</summary>
    /// <param name="value">Whether a created session takes the mode of its creator.</param>
    internal void SetInheritPermissions(bool value)
    {
        DesktopPreferences? preferences = null;
        lock (_gate)
        {
            if (_preferences.InheritPermissions != value) preferences = _preferences = _preferences with { InheritPermissions = value };
        }

        preferences?.Save(_dataRoot);
    }

    /// <summary>
    /// The user's setting: whether the sessions that had Remote Control on when CodeAlta exited have it turned on
    /// again when it starts.
    /// </summary>
    internal bool ReconnectRemoteControl { get { lock (_gate) return _preferences.ReconnectRemoteControl; } }

    /// <summary>Changes that setting and keeps it.</summary>
    /// <param name="value">Whether Remote Control is turned on again at start.</param>
    internal void SetReconnectRemoteControl(bool value)
    {
        DesktopPreferences? preferences = null;
        lock (_gate)
        {
            if (_preferences.ReconnectRemoteControl != value) preferences = _preferences = _preferences with { ReconnectRemoteControl = value };
        }

        preferences?.Save(_dataRoot);
    }

    /// <summary>The sessions that have Remote Control on, the last turned on first.</summary>
    internal IReadOnlyList<string> RemoteControlSessions { get { lock (_gate) return _preferences.RemoteControlSessions; } }

    /// <summary>Remembers that a session has Remote Control on, or no longer has it, and keeps it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="on">Whether its Remote Control is on.</param>
    internal void NoteRemoteControl(string sessionId, bool on)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        DesktopPreferences? preferences = null;
        lock (_gate)
        {
            var sessions = _preferences.RemoteControlSessions.Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal));
            System.Collections.Immutable.ImmutableArray<string> next = on
                ? [.. new[] { sessionId }.Concat(sessions).Take(DesktopPreferences.MaximumRemoteControlSessions)]
                : [.. sessions];
            if (!next.SequenceEqual(_preferences.RemoteControlSessions)) preferences = _preferences = _preferences with { RemoteControlSessions = next };
        }

        preferences?.Save(_dataRoot);
    }

    /// <summary>The most sessions that are shown with a width of their own at a time.</summary>
    internal const int MaximumSessionWidths = 256;

    /// <summary>The user's setting: the width of the conversations, in percent of the space of a session.</summary>
    internal int SessionWidth { get { lock (_gate) return _preferences.SessionWidth; } }

    /// <summary>
    /// Changes the user's setting, keeps it, and tells the pages (a <c>session-width</c> notice without a session).
    /// </summary>
    /// <param name="value">The width, in percent.</param>
    /// <param name="resized">The session the user resized, if any: it follows the setting again.</param>
    /// <returns>False when the width is out of range: nothing changes.</returns>
    internal bool SetSessionWidth(int value, string? resized = null)
    {
        if (!DesktopPreferences.IsSessionWidth(value)) return false;
        DesktopPreferences? preferences = null;
        var released = false;
        lock (_gate)
        {
            if (_preferences.SessionWidth != value) preferences = _preferences = _preferences with { SessionWidth = value };
            released = !string.IsNullOrWhiteSpace(resized) && _sessionWidths.Remove(resized.Trim());
        }

        if (preferences is not null)
        {
            preferences.Save(_dataRoot);
            Publish(new("session-width", 0, SessionWidth: value));
        }

        if (released) Publish(new("session-width", 0, SessionId: resized!.Trim()));
        return true;
    }

    /// <summary>The width one session is shown with instead of the user's setting; null when it follows the setting.</summary>
    internal int? SessionWidthOf(string sessionId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(sessionId) && _sessionWidths.TryGetValue(sessionId.Trim(), out var value) ? value : null;
    }

    /// <summary>The sessions that are shown with a width of their own.</summary>
    internal IReadOnlyList<KeyValuePair<string, int>> SessionWidths()
    {
        lock (_gate) return [.. _sessionWidths];
    }

    /// <summary>
    /// Changes the width one session is shown with, until the application exits or the user resizes that session,
    /// and tells the pages (a <c>session-width</c> notice that names the session; a width of 0 says it follows the
    /// user's setting again). The user's setting is not touched: two sessions never compete for it.
    /// </summary>
    /// <returns>False when the session is not named, the width is out of range, or too many sessions have one.</returns>
    internal bool SetSessionWidthOf(string sessionId, int? value)
    {
        var id = sessionId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(char.IsControl) || value is { } width && !DesktopPreferences.IsSessionWidth(width)) return false;
        lock (_gate)
        {
            if (value is null)
            {
                if (!_sessionWidths.Remove(id)) return true;
            }
            else
            {
                if (_sessionWidths.TryGetValue(id, out var current) && current == value) return true;
                if (!_sessionWidths.ContainsKey(id) && _sessionWidths.Count >= MaximumSessionWidths) return false;
                _sessionWidths[id] = value.Value;
            }
        }

        Publish(new("session-width", 0, SessionWidth: value ?? 0, SessionId: id));
        return true;
    }

    /// <summary>The zoom of the window's view, in percent.</summary>
    internal int Zoom { get { lock (_gate) return _preferences.Zoom; } }

    /// <summary>Applies a zoom factor (1 is 100 percent) to the window's view; null until the view exists.</summary>
    internal Action<double>? ApplyZoom { get; set; }

    /// <summary>
    /// Zooms the view one step in (<paramref name="direction"/> 1) or out (-1), or back to 100 percent (0), and keeps
    /// the zoom for the next starts. Called on the window's thread.
    /// </summary>
    internal void StepZoom(int direction)
    {
        DesktopPreferences? preferences = null;
        int zoom;
        lock (_gate)
        {
            zoom = DesktopPreferences.NextZoom(_preferences.Zoom, direction);
            if (_preferences.Zoom != zoom) preferences = _preferences = _preferences with { Zoom = zoom };
        }

        if (preferences is not null)
        {
            ApplyZoom?.Invoke(zoom / 100d);
            preferences.Save(_dataRoot);
        }
    }

    /// <summary>Whether the MCP server of the application is turned on.</summary>
    internal bool McpServer { get { lock (_gate) return _preferences.McpServer; } }

    /// <summary>Turns the MCP server on or off for the next starts; the server itself is started and stopped by its owner.</summary>
    internal void SetMcpServer(bool value)
    {
        DesktopPreferences preferences;
        lock (_gate) preferences = _preferences = _preferences with { McpServer = value };
        preferences.Save(_dataRoot);
    }

    /// <summary>
    /// Hides the window and leaves the application running: the user's answer to the question asked when the
    /// window was closed.
    /// </summary>
    /// <returns>False when a hidden window could not be brought back: it stays as it is.</returns>
    internal bool Hide()
    {
        if (!CanHide) return false;
        Apply(DesktopCloseAction.Hide, 0);
        return true;
    }

    /// <summary>Lets a page receive the shell's notices until the returned registration is disposed.</summary>
    internal IDisposable Watch(Action<DesktopShellEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private void Apply(DesktopCloseAction action, int running, int terminals = 0)
    {
        switch (action)
        {
            case DesktopCloseAction.Hide:
                OnWindowThread(() => { if (!_window.IsClosed) _window.Hide(); });
                break;
            case DesktopCloseAction.RequestExit:
                // The page exits in its own way (it asks about unsaved files): the window comes back for it.
                Show();
                Publish(new("exit-requested", 0));
                break;
            case DesktopCloseAction.ConfirmExit:
                Show();
                Publish(new("confirm-exit", running, terminals));
                break;
            case DesktopCloseAction.Ask:
                // The window may have been closed from the taskbar while minimized: the question has to be seen.
                Show();
                Publish(new("confirm-close", 0));
                break;
            default:
                lock (_gate)
                {
                    if (_exiting) return;
                    _exiting = true;
                }
                _exit();
                break;
        }
    }

    private static int Count(Func<int>? count)
    {
        try { return Math.Max(0, count?.Invoke() ?? 0); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { return 0; }
    }

    private bool HasWatchers() { lock (_gate) return _watchers.Count > 0; }

    private void Publish(DesktopShellEvent value)
    {
        Action<DesktopShellEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher(value);
    }

    private void OnWindowThread(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Post(action);
    }

    private sealed class Registration(DesktopShell shell, Action<DesktopShellEvent> watcher) : IDisposable
    {
        public void Dispose() { lock (shell._gate) shell._watchers.Remove(watcher); }
    }
}
