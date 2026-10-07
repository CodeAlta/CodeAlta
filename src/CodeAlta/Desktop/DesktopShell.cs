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
internal sealed record DesktopShellEvent(string Kind, int RunningSessions, int BusyTerminals = 0);

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
        _preferences = DesktopPreferences.Load(dataRoot);
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
