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

    /// <summary>The page is asked to exit: it has its own questions first (unsaved files), then asks to exit.</summary>
    RequestExit,

    /// <summary>The page asks first: sessions are running and exiting stops them.</summary>
    ConfirmExit,

    /// <summary>The application exits.</summary>
    Exit,
}

/// <summary>
/// A notice for the page: <c>exit-requested</c> (the tray's Exit, or a closed window that cannot stay hidden),
/// <c>confirm-exit</c>, which carries the number of running sessions, and <c>entry-added</c> (the application
/// was just added to the desktop's applications).
/// </summary>
internal sealed record DesktopShellEvent(string Kind, int RunningSessions);

/// <summary>
/// How the application lives beyond its window: an icon in the notification area (the menu bar on macOS, the
/// status area on Linux) with <c>Open</c> and <c>Exit</c>, the application's menu bar on macOS, a window that
/// can be closed without exiting, and a question before an exit that would stop running sessions.
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
    private DesktopPreferences _preferences;
    private bool _tray;
    private bool _commands;
    private bool _exiting;
    private bool _entryAdded;

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
        _preferences = DesktopPreferences.Load(dataRoot);
    }

    /// <summary>The number of sessions with a run in flight; zero until there is a host to ask.</summary>
    internal Func<int>? RunningSessions { get; set; }

    /// <summary>Whether closing the window leaves the application running.</summary>
    internal bool CloseToTray { get { lock (_gate) return _preferences.CloseToTray; } }

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
    /// the end of the session and anything that cannot be canceled exit at once.
    /// </summary>
    internal static DesktopCloseAction Decide(NeoWindowCloseReason reason, bool canCancel, bool closeToTray, bool canHide, bool hasPage)
    {
        if (!canCancel || reason is NeoWindowCloseReason.SessionEnd or NeoWindowCloseReason.System or NeoWindowCloseReason.Owner) return DesktopCloseAction.Exit;
        if (reason is NeoWindowCloseReason.User or NeoWindowCloseReason.Programmatic && closeToTray && canHide) return DesktopCloseAction.Hide;
        return DecideUserExit(hasPage);
    }

    /// <summary>What Exit in the tray leads to: the page's own exit, or an exit at once when no page listens.</summary>
    internal static DesktopCloseAction DecideUserExit(bool hasPage) => hasPage ? DesktopCloseAction.RequestExit : DesktopCloseAction.Exit;

    /// <summary>What the page's exit leads to: a question while sessions run, unless it was already answered.</summary>
    internal static DesktopCloseAction DecideExit(bool confirmed, int runningSessions, bool canAsk)
        => !confirmed && runningSessions > 0 && canAsk ? DesktopCloseAction.ConfirmExit : DesktopCloseAction.Exit;

    /// <summary>Puts the icon in the notification area. Where the platform has none, closing the window exits.</summary>
    internal async ValueTask StartTrayAsync(NeoDesktopServices services, bool developer)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            if (services.Tray.Support.SupportLevel is NeoSupportLevel.None or NeoSupportLevel.Emulated) return;
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
        Apply(Decide(request.Reason, request.CanCancel, CloseToTray, CanHide, HasWatchers()), 0);
    }

    /// <summary>
    /// Exit as the user asks for it outside the page (the tray's menu, <c>alta --exit</c>): the page exits in
    /// its own way, with its questions; without a page the application exits at once.
    /// </summary>
    internal void RequestUserExit() => Apply(DecideUserExit(HasWatchers()), 0);

    /// <summary>The page's exit: the application exits, after the page's question while sessions run.</summary>
    internal void RequestExit(bool confirmed)
    {
        var running = CountRunning();
        Apply(DecideExit(confirmed, running, HasWatchers()), running);
    }

    /// <summary>Shows the window again and brings it to the front.</summary>
    internal void Show() => OnWindowThread(() =>
    {
        if (_window.IsClosed) return;
        _window.Show();
        if (_window.State == NeoWindowState.Minimized) _window.Restore();
        _window.Activate();
    });

    /// <summary>Changes whether closing the window leaves the application running, and keeps the choice.</summary>
    internal void SetCloseToTray(bool value)
    {
        DesktopPreferences preferences;
        lock (_gate) preferences = _preferences = _preferences with { CloseToTray = value };
        preferences.Save(_dataRoot);
    }

    /// <summary>Lets a page receive the shell's notices until the returned registration is disposed.</summary>
    internal IDisposable Watch(Action<DesktopShellEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private void Apply(DesktopCloseAction action, int running)
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
                Publish(new("confirm-exit", running));
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

    private int CountRunning()
    {
        try { return Math.Max(0, RunningSessions?.Invoke() ?? 0); }
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
