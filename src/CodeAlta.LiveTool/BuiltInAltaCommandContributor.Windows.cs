using System.Globalization;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta window` commands: the windows of a host that has several (the desktop app), the monitors they go to, and
// the tabs they hold. Nothing here changes what a session does: the windows only show it.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] WindowPolicies =
    [
        Read("window list"),
        Read("window show"),
        Read("window monitors"),
        // They change nothing but what the windows show, and where.
        Read("window open"),
        Read("window focus"),
        Read("window close"),
        Read("window tab move"),
        Read("window place"),
        // The name, the icon, the color, whether a window is kept and its monitor are kept for the user.
        Mutating("window set"),
    ];

    private static readonly string[] WindowStates = ["normal", "maximized", "minimized", "fullscreen"];

    private static Command CreateWindowCommand(AltaCommandContext context)
    {
        var group = Group("window", "Use the windows of CodeAlta: list them and their tabs, open one, move a tab to another, place a window on a monitor.");
        group.Add(CreateWindowListCommand(context));
        group.Add(CreateWindowShowCommand(context));
        group.Add(CreateWindowMonitorsCommand(context));
        group.Add(CreateWindowOpenCommand(context));
        group.Add(CreateWindowFocusCommand(context));
        group.Add(CreateWindowCloseCommand(context));
        group.Add(CreateWindowTabCommand(context));
        group.Add(CreateWindowPlaceCommand(context));
        group.Add(CreateWindowSetCommand(context));
        AddHelpText(
            group,
            "The main window has the activity rail, the projects and Settings; a secondary window holds tabs moved out of it. A tab is in one window at a time, and a window shows one space.",
            "<window> is the id of a window, `main`, its name, or the start of its id when only one window starts so. A saved window is kept when it is closed, and opens again with `alta window open --saved`.",
            "<tab> is the reference of a tab as `alta window list --tabs` prints it (`session:<id>`, `editor:<project>`, `terminal:<id>`…); `current` is the tab of the calling session.",
            "<monitor> is the id of a monitor, its number from `alta window monitors`, its name, or `primary`. A window only goes to a monitor that is connected.",
            "Move windows and tabs when the user asks for it: the windows are the user's.",
            "Examples: `alta window list --tabs`; `alta window monitors`; `alta window tab move current --to new --monitor 2`; `alta window place <window> --state maximized`; `alta window set <window> --name Review --icon telescope`.");
        return group;
    }

    private static Command CreateWindowListCommand(AltaCommandContext context)
    {
        var tabs = false;
        var command = Leaf("list", "List the windows: the main one first, then the open ones, then the saved ones that are closed.");
        command.Add("tabs", "Also list the tabs of each window.", value => tabs = value is not null);
        command.Add(async (_, _) => await HandleWindowListAsync(context, tabs).ConfigureAwait(false));
        AddHelpText(command,
            "`monitor` is where an open window is; `pinnedMonitor` is the monitor it goes to whenever that monitor is connected (`pinnedConnected`).",
            "Examples: `alta window list`; `alta window list --tabs`.");
        return command;
    }

    private static Command CreateWindowShowCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("show", "Show one window with its tabs.");
        command.Add("<window>", "Window id, `main`, name, or start of id.", value => reference = value);
        command.Add(async (_, _) => await HandleWindowShowAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta window show main`; `alta window show Review`.");
        return command;
    }

    private static Command CreateWindowMonitorsCommand(AltaCommandContext context)
    {
        var command = Leaf("monitors", "List the monitors that are connected, the primary one first.");
        command.Add(async (_, _) => await HandleWindowMonitorsAsync(context).ConfigureAwait(false));
        AddHelpText(command,
            "`bounds` and `workArea` (what the taskbar, the menu bar or the Dock leave to windows) are in the units of the monitors. An id names its monitor after a restart where `idPersistent` is true.",
            "Example: `alta window monitors`.");
        return command;
    }

    private static Command CreateWindowOpenCommand(AltaCommandContext context)
    {
        var options = new WindowOpenOptions();
        var command = Leaf("open", "Open a new window, or a saved window that is closed, and move tabs into it.");
        command.Add("name=", "Name of a new window, 1 to 64 characters. A named window is saved: it is kept when it is closed.", value => options.Name = value);
        command.Add("icon=", "Icon of a new window, one of the icons of the spaces.", value => options.Icon = value);
        command.Add("color=", "Color of a new window, `#rgb` or `#rrggbb`.", value => options.Color = value);
        command.Add("monitor=", "Monitor to open on: id, number, name, or `primary`. Without it, the default place.", value => options.Monitor = value);
        command.Add("tab=", "A tab to move into the window. Repeat the option for several.", value => { if (value is not null) options.Tabs.Add(value); });
        command.Add("saved=", "Open again the saved window that is closed: id, name, or start of id.", value => options.Saved = value);
        command.Add(async (_, _) => await HandleWindowOpenAsync(context, options).ConfigureAwait(false));
        AddHelpText(command,
            "A saved window opens with its name, its icon and its color: `--saved` takes no `--name`, `--icon` or `--color`.",
            "Examples: `alta window open --tab current`; `alta window open --name Review --icon telescope --monitor 2 --tab editor:<project>`; `alta window open --saved Review`.");
        return command;
    }

    private static Command CreateWindowFocusCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("focus", "Bring an open window to the front.");
        command.Add("<window>", "Window id, `main`, name, or start of id.", value => reference = value);
        command.Add(async (_, _) => await HandleWindowFocusAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta window focus Review`.");
        return command;
    }

    private static Command CreateWindowCloseCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("close", "Close a secondary window. Its tabs close, as each would; the sessions keep running.");
        command.Add("<window>", "Window id, name, or start of id.", value => reference = value);
        command.Add(async (_, _) => await HandleWindowCloseAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command,
            "The main window is not closed this way. A saved window stays in the list, closed, and opens again with `alta window open --saved`.",
            "Example: `alta window close Review`.");
        return command;
    }

    private static Command CreateWindowTabCommand(AltaCommandContext context)
    {
        var group = Group("tab", "Move the tabs of the windows.");
        string? tab = null, target = null, monitor = null;
        var move = Leaf("move", "Move a tab to another window, or to a new one.");
        move.Add("<tab>", "Reference of the tab, as `alta window list --tabs` prints it; `current` for the tab of the calling session.", value => tab = value);
        move.Add("to=", "The window it goes to: id, `main`, name, start of id, or `new` for a new window. Required.", value => target = value);
        move.Add("monitor=", "For a new window, or a saved one that is closed, the monitor it opens on: id, number, name, or `primary`.", value => monitor = value);
        move.Add(async (_, _) => await HandleWindowTabMoveAsync(context, tab, target, monitor).ConfigureAwait(false));
        AddHelpText(move,
            "A window shows one space: a tab of another space cannot go to it (`window.tabNotMovable`). A tab where a prompt is being sent cannot move until it is sent, and a code editor with unsaved edits until they are saved.",
            "Examples: `alta window tab move current --to new`; `alta window tab move session:<id> --to main`; `alta window tab move editor:<project> --to Review`.");
        group.Add(move);
        AddHelpText(group, "Example: `alta window tab move current --to new --monitor 2`.");
        return group;
    }

    private static Command CreateWindowPlaceCommand(AltaCommandContext context)
    {
        var options = new WindowPlaceOptions();
        var command = Leaf("place", "Place an open window: on a monitor, in a state, at bounds, on the current virtual desktop.");
        command.Add("<window>", "Window id, `main`, name, or start of id.", value => options.Window = value);
        command.Add("monitor=", "Monitor it goes to: id, number, name, or `primary`.", value => options.Monitor = value);
        command.Add("state=", "Its state: normal, maximized, minimized or fullscreen.", value => options.State = value);
        command.Add("bounds=", "Its position and size, `x,y,width,height`, from the top-left corner of the work area of its monitor.", value => options.Bounds = value);
        command.Add("desktop=", "`current` to bring it to the current virtual desktop.", value => options.Desktop = value);
        command.Add(async (_, _) => await HandleWindowPlaceAsync(context, options).ConfigureAwait(false));
        AddHelpText(command,
            "What is not given does not change. A monitor that is not connected is refused: list them with `alta window monitors`.",
            "Examples: `alta window place Review --monitor 2 --state maximized`; `alta window place main --bounds 0,0,1280,900`; `alta window place Review --desktop current`.");
        return command;
    }

    private static Command CreateWindowSetCommand(AltaCommandContext context)
    {
        var options = new WindowSetOptions();
        var command = Leaf("set", "Change the name, the icon, the color of a window, whether it is kept when it is closed, and the monitor it is pinned to.");
        command.Add("<window>", "Window id, `main`, name, or start of id; a saved window that is closed too.", value => options.Window = value);
        command.Add("name=", "New name, 1 to 64 characters; a window given a name is saved. Empty for the name it has by itself.", value => options.Name = value);
        command.Add("icon=", "Icon, one of the icons of the spaces; empty for none.", value => options.Icon = value);
        command.Add("color=", "Color, `#rgb` or `#rrggbb`; empty for none.", value => options.Color = value);
        command.Add("saved=", "`true` to keep the window when it is closed, `false` to forget it then.", value => options.Saved = value);
        command.Add("pin-monitor=", "Monitor the window goes to whenever it is connected: id, number, name, or `primary`; `none` for where it was.", value => options.PinMonitor = value);
        command.Add(async (_, _) => await HandleWindowSetAsync(context, options).ConfigureAwait(false));
        AddHelpText(command,
            "What is not given stays as it is. The windows are the user's: change them when the user asks for it.",
            "Examples: `alta window set Review --name \"Code review\" --color #2d72d2`; `alta window set Review --pin-monitor 2`; `alta window set Review --pin-monitor none --saved false`.");
        return command;
    }

    private static bool TryGetWindows(AltaCommandContext context, out IAltaWindows windows)
    {
        if (context.Services.Get<IAltaWindows>() is { } found)
        {
            windows = found;
            return true;
        }

        windows = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaWindows' is unavailable.");
        return false;
    }

    // A window by its id, then `main`, then its name, then the start of its id when only one window starts so.
    private static (AltaWindow? Window, int ExitCode) ResolveWindow(AltaCommandContext context, IReadOnlyList<AltaWindow> windows, string? reference, string commandPath)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            return (null, UsageError(context, "usage.missingWindow", "A window is required: its id, `main`, its name, or the start of its id.", commandPath));
        }

        if (windows.FirstOrDefault(window => string.Equals(window.Id, text, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact, AltaExitCodes.Success);
        }

        if (string.Equals(text, "main", StringComparison.OrdinalIgnoreCase) && windows.FirstOrDefault(static window => window.Main) is { } main)
        {
            return (main, AltaExitCodes.Success);
        }

        var named = windows.Where(window => string.Equals(window.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
        {
            named = windows.Where(window => window.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return named.Length switch
        {
            1 => (named[0], AltaExitCodes.Success),
            0 => (null, NotFound(context, "window.notFound", $"No window has the id or the name '{text}'. List them with `alta window list`.")),
            _ => (null, UsageError(context, "window.ambiguous",
                $"Several windows match '{text}': {string.Join(", ", named.Select(static window => $"{window.Id} ({window.Name})"))}. Use the id.", commandPath)),
        };
    }

    // An open window: focus, close and place need one.
    private static async Task<(AltaWindow? Window, int ExitCode)> ResolveOpenWindowAsync(AltaCommandContext context, IAltaWindows windows, string? reference, string commandPath)
    {
        var all = await windows.ListAsync(context.CancellationToken).ConfigureAwait(false);
        var (window, exitCode) = ResolveWindow(context, all, reference, commandPath);
        if (window is null || window.Open)
        {
            return (window, exitCode);
        }

        return (null, NotFound(context, "window.notOpen", $"The window '{window.Name}' ({window.Id}) is closed. Open it with `alta window open --saved {window.Id}`."));
    }

    // A monitor by its id, then `primary`, then its number, then its name when only one monitor has it. Only a monitor
    // that is connected is one: no window is ever sent where there is none.
    private static async Task<(AltaMonitor? Monitor, int ExitCode)> ResolveMonitorAsync(AltaCommandContext context, IAltaWindows windows, string reference, string commandPath)
    {
        var text = reference.Trim();
        var monitors = await windows.ListMonitorsAsync(context.CancellationToken).ConfigureAwait(false);
        if (monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, text, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact, AltaExitCodes.Success);
        }

        if (string.Equals(text, "primary", StringComparison.OrdinalIgnoreCase) && monitors.FirstOrDefault(static monitor => monitor.Primary) is { } primary)
        {
            return (primary, AltaExitCodes.Success);
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && monitors.FirstOrDefault(monitor => monitor.Number == number) is { } numbered)
        {
            return (numbered, AltaExitCodes.Success);
        }

        var named = monitors.Where(monitor => string.Equals(monitor.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length switch
        {
            1 => (named[0], AltaExitCodes.Success),
            0 => (null, MonitorNotFound(context, text, monitors)),
            _ => (null, UsageError(context, "window.monitorAmbiguous",
                $"Several monitors are named '{text}': use the number or the id of one ({string.Join(", ", named.Select(static monitor => $"{monitor.Number.ToString(CultureInfo.InvariantCulture)}: {monitor.Id}"))}).", commandPath)),
        };
    }

    private static int MonitorNotFound(AltaCommandContext context, string text, IReadOnlyList<AltaMonitor>? monitors)
        => NotFound(context, "window.monitorNotFound", monitors is { Count: 0 }
            ? $"No monitor '{text}' is connected: this platform does not tell the monitors, so leave the monitor out."
            : $"No monitor '{text}' is connected. List them with `alta window monitors`.");

    // A tab by its reference; `current` and `session:current` are the tab of the calling session.
    private static (string? Tab, int ExitCode) ResolveWindowTab(AltaCommandContext context, string? reference, string commandPath)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            return (null, UsageError(context, "usage.missingTab", "A tab is required: its reference, as `alta window list --tabs` prints it, or `current`.", commandPath));
        }

        if (!string.Equals(text, "current", StringComparison.OrdinalIgnoreCase) && !string.Equals(text, "session:current", StringComparison.OrdinalIgnoreCase))
        {
            return (text, AltaExitCodes.Success);
        }

        return NormalizeOptionalText(context.Caller.SourceSessionId) is { } sessionId
            ? ($"session:{sessionId}", AltaExitCodes.Success)
            : (null, UsageError(context, "usage.missingSession", "`current` is the tab of the calling session, and no session calls: name the tab, such as `session:<id>`.", commandPath));
    }

    // What a result that is not `ok` answers.
    private static int WindowRequestFailed(AltaCommandContext context, string status, string? windowName, string? tab = null)
    {
        var window = windowName is null ? "the window" : $"the window '{windowName}'";
        switch (status)
        {
            case "not_found":
                return NotFound(context, "window.notFound", tab is null
                    ? $"{Capitalize(window)} is not there any more. List the windows with `alta window list`."
                    : $"No window holds the tab '{tab}', or {window} is not there any more. List the tabs with `alta window list --tabs`.");
            case "main":
                return Unsupported(context, "window.mainNotClosable", "The main window is not closed this way: it is closed with the application.");
            case "other_space":
                return Unsupported(context, "window.tabNotMovable", $"The tab '{tab}' cannot go to {window}: that window shows another space.");
            case "not_movable":
                return Unsupported(context, "window.tabNotMovable", $"The tab '{tab}' cannot move now: a prompt is being sent in it. Try again once it is sent.");
            case "unsaved":
                return Unsupported(context, "window.tabNotMovable", $"The tab '{tab}' cannot move now: its files have unsaved edits. Save them first.");
            case "monitor_not_found":
            case "display_not_found":
                return NotFound(context, "window.monitorNotFound", "The monitor is not connected any more. List them with `alta window monitors`.");
            case "not_supported":
                return Unsupported(context, "window.notSupported", "This platform cannot place windows that way.");
            case "timeout":
            case "unavailable":
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable,
                    status == "timeout" ? $"{Capitalize(window)} did not answer in time." : "No window can do it now: the application may be starting or exiting.");
                return AltaExitCodes.ServiceUnavailable;
            default:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "window.failed", AltaExitCodes.Failure, $"The request about {window} failed: {status}.");
                return AltaExitCodes.Failure;
        }

        static string Capitalize(string text) => string.Concat(char.ToUpperInvariant(text[0]).ToString(), text.AsSpan(1));
    }

    private static Dictionary<string, object?> WindowRecord(AltaCommandContext context, string type, AltaWindow window)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = window.Id,
            ["main"] = window.Main,
            ["name"] = window.Name,
            ["icon"] = window.Icon,
            ["color"] = window.Color,
            ["saved"] = window.Saved,
            ["open"] = window.Open,
            ["spaceId"] = window.SpaceId,
            ["monitor"] = MonitorReferenceRecord(window.Monitor),
            ["pinnedMonitor"] = MonitorReferenceRecord(window.PinnedMonitor),
        };
        if (window.PinnedMonitor is not null) record["pinnedConnected"] = window.PinnedConnected;
        if (window.Open)
        {
            // What a closed window has is what it had: only an open one is in a state, in front or shown.
            record["state"] = window.State;
            record["focused"] = window.Focused;
            record["visible"] = window.Visible;
        }

        record["bounds"] = RectRecord(window.Bounds);
        record["onCurrentDesktop"] = window.OnCurrentDesktop;
        record["tabCount"] = window.Tabs.Count;
        return record;
    }

    private static Dictionary<string, object?>? MonitorReferenceRecord(AltaMonitorReference? monitor)
        => monitor is null
            ? null
            : WithoutNulls(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = monitor.Id, ["name"] = monitor.Name, ["primary"] = monitor.Primary });

    private static Dictionary<string, object?>? RectRecord(AltaRect? rect)
        => rect is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height };

    private static Dictionary<string, object?>[] WindowTabsRecord(AltaWindow window)
        => window.Tabs.Select(static tab => new Dictionary<string, object?>(StringComparer.Ordinal) { ["tab"] = tab.Tab, ["kind"] = tab.Kind, ["active"] = tab.Active }).ToArray();

    private static Dictionary<string, object?> WithoutNulls(Dictionary<string, object?> record)
        => record.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    // What a window does not have is left out of its records, as for a canvas.
    private static void WriteWindowRecord(AltaCommandContext context, Dictionary<string, object?> record)
        => AltaJsonlWriter.WriteRecord(context.Stdout, WithoutNulls(record));

    // The record of a request that worked: the window as it is now, else its id.
    private static void WriteWindowResult(AltaCommandContext context, string type, AltaWindow? window, AltaWindow fallback)
        => WriteWindowRecord(context, window is not null
            ? WindowRecord(context, type, window)
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = type,
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["id"] = fallback.Id,
                ["name"] = fallback.Name,
            });

    private static async ValueTask<int> HandleWindowListAsync(AltaCommandContext context, bool tabs)
    {
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var all = await windows.ListAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (var window in all)
        {
            WriteWindowRecord(context, WindowRecord(context, "alta.window.item", window));
            if (!tabs)
            {
                continue;
            }

            foreach (var tab in window.Tabs)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.window.tab",
                    version = 1,
                    correlationId = context.CorrelationId,
                    windowId = window.Id,
                    tab = tab.Tab,
                    kind = tab.Kind,
                    active = tab.Active,
                });
            }
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.window.summary",
            version = 1,
            correlationId = context.CorrelationId,
            windows = all.Count,
            open = all.Count(static window => window.Open),
            tabs = all.Sum(static window => window.Tabs.Count),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowShowAsync(AltaCommandContext context, string? reference)
    {
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (window, exitCode) = ResolveWindow(context, await windows.ListAsync(context.CancellationToken).ConfigureAwait(false), reference, "alta window show");
        if (window is null)
        {
            return exitCode;
        }

        var record = WindowRecord(context, "alta.window.detail", window);
        record["tabs"] = WindowTabsRecord(window);
        WriteWindowRecord(context, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowMonitorsAsync(AltaCommandContext context)
    {
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var monitors = await windows.ListMonitorsAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (var monitor in monitors)
        {
            WriteWindowRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.window.monitor",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["number"] = monitor.Number,
                ["id"] = monitor.Id,
                ["name"] = monitor.Name,
                ["bounds"] = RectRecord(monitor.Bounds),
                ["workArea"] = RectRecord(monitor.WorkArea),
                ["scale"] = monitor.Scale,
                ["primary"] = monitor.Primary,
                ["idPersistent"] = monitor.IdPersistent,
            });
        }

        WriteSummary(context, "alta.window.monitors", monitors.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowOpenAsync(AltaCommandContext context, WindowOpenOptions options)
    {
        const string CommandPath = "alta window open";
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        string? savedId = null;
        if (options.Saved is not null)
        {
            if (options.Name is not null || options.Icon is not null || options.Color is not null)
            {
                return UsageError(context, "usage.optionConflict", "A saved window opens with its name, its icon and its color: leave out --name, --icon and --color, or change them with `alta window set`.", CommandPath);
            }

            var (saved, exitCode) = ResolveWindow(context, await windows.ListAsync(context.CancellationToken).ConfigureAwait(false), options.Saved, CommandPath);
            if (saved is null)
            {
                return exitCode;
            }

            if (saved.Open)
            {
                return Unsupported(context, "window.alreadyOpen",
                    $"The window '{saved.Name}' ({saved.Id}) is open. Bring it to the front with `alta window focus {saved.Id}`, and move tabs to it with `alta window tab move <tab> --to {saved.Id}`.");
            }

            savedId = saved.Id;
        }

        var (name, nameExit) = ValidateWindowName(context, options.Name, CommandPath, allowEmpty: false);
        if (nameExit != AltaExitCodes.Success) return nameExit;
        var (icon, iconExit) = ValidateWindowIcon(context, options.Icon, CommandPath, allowEmpty: false);
        if (iconExit != AltaExitCodes.Success) return iconExit;
        var (color, colorExit) = ValidateWindowColor(context, options.Color, CommandPath, allowEmpty: false);
        if (colorExit != AltaExitCodes.Success) return colorExit;

        string? monitorId = null;
        if (NormalizeOptionalText(options.Monitor) is { } monitorRef)
        {
            var (monitor, exitCode) = await ResolveMonitorAsync(context, windows, monitorRef, CommandPath).ConfigureAwait(false);
            if (monitor is null)
            {
                return exitCode;
            }

            monitorId = monitor.Id;
        }

        var tabs = new List<string>(options.Tabs.Count);
        foreach (var reference in options.Tabs)
        {
            var (tab, exitCode) = ResolveWindowTab(context, reference, CommandPath);
            if (tab is null)
            {
                return exitCode;
            }

            if (!tabs.Contains(tab, StringComparer.Ordinal)) tabs.Add(tab);
        }

        var result = await windows.OpenAsync(new AltaWindowOpenRequest
        {
            SavedWindowId = savedId,
            Name = name,
            Icon = icon,
            Color = color,
            MonitorId = monitorId,
            Tabs = tabs,
        }, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return WindowRequestFailed(context, result.Status, savedId is null ? name : options.Saved);
        }

        if (result.Window is { } opened)
        {
            var record = WindowRecord(context, "alta.window.opened", opened);
            record["tabs"] = WindowTabsRecord(opened);
            // A tab that did not move stays where it was: the window opens all the same, and says which and why.
            if (result.TabMoves?.Where(static move => move.Status != "ok").ToArray() is { Length: > 0 } notMoved)
            {
                record["notMoved"] = notMoved.Select(static move => new Dictionary<string, object?>(StringComparer.Ordinal) { ["tab"] = move.Tab, ["status"] = move.Status }).ToArray();
            }

            WriteWindowRecord(context, record);
        }
        else
        {
            WriteWindowRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.window.opened",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["id"] = savedId,
            });
        }

        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowFocusAsync(AltaCommandContext context, string? reference)
    {
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (window, exitCode) = await ResolveOpenWindowAsync(context, windows, reference, "alta window focus").ConfigureAwait(false);
        if (window is null)
        {
            return exitCode;
        }

        var result = await windows.FocusAsync(window.Id, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return WindowRequestFailed(context, result.Status, window.Name);
        }

        WriteWindowResult(context, "alta.window.focused", result.Window, window);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowCloseAsync(AltaCommandContext context, string? reference)
    {
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (window, exitCode) = await ResolveOpenWindowAsync(context, windows, reference, "alta window close").ConfigureAwait(false);
        if (window is null)
        {
            return exitCode;
        }

        // Asked of the window, the main one would close the application.
        if (window.Main)
        {
            return WindowRequestFailed(context, "main", window.Name);
        }

        var result = await windows.CloseAsync(window.Id, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return WindowRequestFailed(context, result.Status, window.Name);
        }

        WriteWindowRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.window.closed",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = window.Id,
            ["name"] = window.Name,
            ["saved"] = result.Window?.Saved ?? window.Saved,
            ["tabsClosed"] = window.Tabs.Count,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowTabMoveAsync(AltaCommandContext context, string? tabRef, string? targetRef, string? monitorRef)
    {
        const string CommandPath = "alta window tab move";
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (tab, tabExit) = ResolveWindowTab(context, tabRef, CommandPath);
        if (tab is null)
        {
            return tabExit;
        }

        if (NormalizeOptionalText(targetRef) is not { } targetText)
        {
            return UsageError(context, "usage.missingTarget", "Where the tab goes is required: `--to <window>`, or `--to new` for a new window.", CommandPath);
        }

        AltaWindow? target = null;
        if (!string.Equals(targetText, "new", StringComparison.OrdinalIgnoreCase))
        {
            var (resolved, exitCode) = ResolveWindow(context, await windows.ListAsync(context.CancellationToken).ConfigureAwait(false), targetText, CommandPath);
            if (resolved is null)
            {
                return exitCode;
            }

            target = resolved;
        }

        string? monitorId = null;
        if (NormalizeOptionalText(monitorRef) is { } monitorText)
        {
            if (target is { Open: true })
            {
                return UsageError(context, "usage.optionConflict",
                    $"The window '{target.Name}' is open: the tab goes where it is. Leave out --monitor, or place the window with `alta window place {target.Id} --monitor {monitorText}`.", CommandPath);
            }

            var (monitor, exitCode) = await ResolveMonitorAsync(context, windows, monitorText, CommandPath).ConfigureAwait(false);
            if (monitor is null)
            {
                return exitCode;
            }

            monitorId = monitor.Id;
        }

        var moved = await windows.MoveTabAsync(tab, target?.Id, monitorId, context.CancellationToken).ConfigureAwait(false);
        if (moved.Status != "ok")
        {
            return WindowRequestFailed(context, moved.Status, target?.Name, tab);
        }

        WriteWindowRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.window.tab.moved",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["tab"] = tab,
            ["windowId"] = moved.WindowId ?? target?.Id,
            ["newWindow"] = target is null,
            ["monitorId"] = monitorId,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowPlaceAsync(AltaCommandContext context, WindowPlaceOptions options)
    {
        const string CommandPath = "alta window place";
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        string? state = null;
        if (NormalizeOptionalText(options.State) is { } stateText)
        {
            state = WindowStates.FirstOrDefault(known => string.Equals(known, stateText, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                return UsageError(context, "usage.invalidState", $"'{stateText}' is not a state: use normal, maximized, minimized or fullscreen.", CommandPath);
            }
        }

        AltaRect? bounds = null;
        if (options.Bounds is not null)
        {
            bounds = ParseWindowBounds(options.Bounds);
            if (bounds is null)
            {
                return UsageError(context, "usage.invalidBounds", "The bounds are four whole numbers, `x,y,width,height`, with a width and a height above 0.", CommandPath);
            }
        }

        var currentDesktop = false;
        if (options.Desktop is not null)
        {
            if (!string.Equals(NormalizeOptionalText(options.Desktop), "current", StringComparison.OrdinalIgnoreCase))
            {
                return UsageError(context, "usage.invalidDesktop", "--desktop takes `current`: the virtual desktop the user is on.", CommandPath);
            }

            currentDesktop = true;
        }

        if (options.Monitor is null && state is null && bounds is null && !currentDesktop)
        {
            return UsageError(context, "usage.missingPlacement", "Nothing to change: use --monitor, --state, --bounds or --desktop.", CommandPath);
        }

        var (window, exitCode) = await ResolveOpenWindowAsync(context, windows, options.Window, CommandPath).ConfigureAwait(false);
        if (window is null)
        {
            return exitCode;
        }

        string? monitorId = null;
        if (options.Monitor is not null)
        {
            if (NormalizeOptionalText(options.Monitor) is not { } monitorText)
            {
                return UsageError(context, "usage.missingMonitor", "--monitor needs a monitor: id, number, name, or `primary`.", CommandPath);
            }

            var (monitor, monitorExit) = await ResolveMonitorAsync(context, windows, monitorText, CommandPath).ConfigureAwait(false);
            if (monitor is null)
            {
                return monitorExit;
            }

            monitorId = monitor.Id;
        }

        var result = await windows.PlaceAsync(window.Id, new AltaWindowPlacement { MonitorId = monitorId, State = state, Bounds = bounds, CurrentDesktop = currentDesktop },
            context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return WindowRequestFailed(context, result.Status, window.Name);
        }

        WriteWindowResult(context, "alta.window.placed", result.Window, window);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWindowSetAsync(AltaCommandContext context, WindowSetOptions options)
    {
        const string CommandPath = "alta window set";
        if (!TryGetWindows(context, out var windows))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (name, nameExit) = ValidateWindowName(context, options.Name, CommandPath, allowEmpty: true);
        if (nameExit != AltaExitCodes.Success) return nameExit;
        var (icon, iconExit) = ValidateWindowIcon(context, options.Icon, CommandPath, allowEmpty: true);
        if (iconExit != AltaExitCodes.Success) return iconExit;
        var (color, colorExit) = ValidateWindowColor(context, options.Color, CommandPath, allowEmpty: true);
        if (colorExit != AltaExitCodes.Success) return colorExit;

        bool? saved = null;
        if (options.Saved is not null)
        {
            if (!bool.TryParse(options.Saved.Trim(), out var value))
            {
                return UsageError(context, "usage.invalidSaved", "--saved takes `true` or `false`.", CommandPath);
            }

            saved = value;
        }

        if (name is null && icon is null && color is null && saved is null && options.PinMonitor is null)
        {
            return UsageError(context, "usage.missingChange", "Nothing to change: use --name, --icon, --color, --saved or --pin-monitor.", CommandPath);
        }

        var (window, exitCode) = ResolveWindow(context, await windows.ListAsync(context.CancellationToken).ConfigureAwait(false), options.Window, CommandPath);
        if (window is null)
        {
            return exitCode;
        }

        string? pinned = null;
        if (options.PinMonitor is not null)
        {
            if (NormalizeOptionalText(options.PinMonitor) is not { } pinText || string.Equals(pinText, "none", StringComparison.OrdinalIgnoreCase))
            {
                pinned = string.Empty;
            }
            else
            {
                var (monitor, monitorExit) = await ResolveMonitorAsync(context, windows, pinText, CommandPath).ConfigureAwait(false);
                if (monitor is null)
                {
                    return monitorExit;
                }

                pinned = monitor.Id;
            }
        }

        var result = await windows.UpdateAsync(window.Id, new AltaWindowUpdate { Name = name, Icon = icon, Color = color, Saved = saved, PinnedMonitorId = pinned },
            context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return WindowRequestFailed(context, result.Status, window.Name);
        }

        WriteWindowResult(context, "alta.window.updated", result.Window, window);
        return AltaExitCodes.Success;
    }

    // A name, trimmed; empty takes it away where that is allowed. Null when not given.
    private static (string? Value, int ExitCode) ValidateWindowName(AltaCommandContext context, string? value, string commandPath, bool allowEmpty)
    {
        if (value is null) return (null, AltaExitCodes.Success);
        var text = value.Trim();
        if (text.Length == 0)
        {
            return allowEmpty ? (string.Empty, AltaExitCodes.Success) : (null, UsageError(context, "usage.invalidName", "The name of a window is 1 to 64 characters on one line.", commandPath));
        }

        return SpaceDescriptor.IsValidName(text)
            ? (text, AltaExitCodes.Success)
            : (null, UsageError(context, "usage.invalidName", "The name of a window is 1 to 64 characters on one line.", commandPath));
    }

    // An icon, with the names the icons of the spaces have.
    private static (string? Value, int ExitCode) ValidateWindowIcon(AltaCommandContext context, string? value, string commandPath, bool allowEmpty)
    {
        if (value is null) return (null, AltaExitCodes.Success);
        var text = value.Trim();
        if (text.Length == 0 && allowEmpty) return (string.Empty, AltaExitCodes.Success);
        return SpaceDescriptor.IsValidIcon(text)
            ? (text, AltaExitCodes.Success)
            : (null, UsageError(context, "usage.invalidIcon", $"'{text}' is not the name of an icon: 1 to 64 lower-case letters, digits and `-`, as the icons of the spaces.", commandPath));
    }

    private static (string? Value, int ExitCode) ValidateWindowColor(AltaCommandContext context, string? value, string commandPath, bool allowEmpty)
    {
        if (value is null) return (null, AltaExitCodes.Success);
        var text = value.Trim();
        if (text.Length == 0 && allowEmpty) return (string.Empty, AltaExitCodes.Success);
        return SpaceDescriptor.IsValidColor(text)
            ? (text, AltaExitCodes.Success)
            : (null, UsageError(context, "usage.invalidColor", $"'{text}' is not a color written #rgb or #rrggbb.", commandPath));
    }

    // `x,y,width,height`, whole numbers, with a size above 0.
    private static AltaRect? ParseWindowBounds(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])) return null;
        }

        return values[2] > 0 && values[3] > 0 ? new AltaRect(values[0], values[1], values[2], values[3]) : null;
    }

    private sealed class WindowOpenOptions
    {
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string? Monitor { get; set; }
        public List<string> Tabs { get; } = [];
        public string? Saved { get; set; }
    }

    private sealed class WindowPlaceOptions
    {
        public string? Window { get; set; }
        public string? Monitor { get; set; }
        public string? State { get; set; }
        public string? Bounds { get; set; }
        public string? Desktop { get; set; }
    }

    private sealed class WindowSetOptions
    {
        public string? Window { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string? Saved { get; set; }
        public string? PinMonitor { get; set; }
    }
}
