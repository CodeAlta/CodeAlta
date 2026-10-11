using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeoAstra.Desktop.Menus;

namespace CodeAlta.Desktop;

/// <summary>
/// The menu bar of the application on macOS: the application's own menu, Edit and Window, with the standard
/// keyboard shortcuts. A Mac application has no shortcut that its menu bar does not define, so without this
/// menu ⌘Q, ⌘H, ⌘W and ⌘M do nothing, and neither do ⌘C, ⌘X and ⌘V in the page.
/// </summary>
/// <remarks>
/// Every item is a command with its shortcut, never a role item: a role item cannot carry a shortcut, and the
/// Quit role ends the application the way the end of the session does, without the page's questions. An item
/// other than Quit or Close Tab sends the standard action of AppKit through the responder chain. Quit is
/// the shell's exit; Close Tab asks the page to close its selected tab, never the native window.
/// </remarks>
internal static class DesktopApplicationMenu
{
    /// <summary>The target that NeoAstra shows as the menu bar.</summary>
    internal const string Target = "application";

    private const string Prefix = "codealta.menu.";

    // Item identifier, text, shortcut and the action's selector, by menu.
    private static readonly (string Id, string Text, string? Accelerator, string Selector)[] Application =
    [
        ("hide", "Hide {0}", "Cmd+H", "hide:"),
        ("hide-others", "Hide Others", "Alt+Cmd+H", "hideOtherApplications:"),
        ("show-all", "Show All", null, "unhideAllApplications:"),
    ];

    private static readonly (string Id, string Text, string? Accelerator, string Selector)[] Edit =
    [
        ("undo", "Undo", "Cmd+Z", "undo:"),
        ("redo", "Redo", "Cmd+Shift+Z", "redo:"),
        ("cut", "Cut", "Cmd+X", "cut:"),
        ("copy", "Copy", "Cmd+C", "copy:"),
        ("paste", "Paste", "Cmd+V", "paste:"),
        ("select-all", "Select All", "Cmd+A", "selectAll:"),
    ];

    private static readonly (string Id, string Text, string? Accelerator, string Selector)[] Window =
    [
        ("minimize", "Minimize", "Cmd+M", "performMiniaturize:"),
    ];

    /// <summary>The menu bar for an application shown under <paramref name="name"/>.</summary>
    /// <param name="name">The application's display name, as in the tray.</param>
    /// <param name="exitCommand">The command of the Quit item.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="exitCommand"/> is blank.</exception>
    internal static IReadOnlyList<NeoMenuItem> Build(string name, string exitCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(exitCommand);
        NeoMenuItem Item((string Id, string Text, string? Accelerator, string Selector) item)
            => NeoMenuItem.Command(item.Id, string.Format(System.Globalization.CultureInfo.InvariantCulture, item.Text, name), Prefix + item.Id, item.Accelerator);
        return
        [
            // AppKit titles the first menu with the application's name, whatever this text says.
            NeoMenuItem.Submenu("application", name,
            [
                .. Application.Select(Item),
                NeoMenuItem.Separator("application-separator"),
                NeoMenuItem.Command("quit", "Quit " + name, exitCommand, "Cmd+Q"),
            ]),
            NeoMenuItem.Submenu("edit", "Edit",
            [
                .. Edit.Take(2).Select(Item),
                NeoMenuItem.Separator("edit-separator"),
                .. Edit.Skip(2).Select(Item),
            ]),
            NeoMenuItem.Submenu("window", "Window", [.. Window.Select(Item), NeoMenuItem.Command("close", "Close Tab", Prefix + "close", "Cmd+W")]),
        ];
    }

    /// <summary>
    /// Registers the commands other than Quit. Close Tab goes to the page; standard actions go to AppKit.
    /// Callbacks run on whatever thread the command is activated on.
    /// </summary>
    /// <param name="commands">The command service of the menus.</param>
    /// <param name="send">Sends the action named by a selector.</param>
    /// <param name="closeTab">Asks the page to close the focused tab, preserving its final prompt tab.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The commands are already registered.</exception>
    internal static void Register(NeoCommandService commands, Action<string> send, Action closeTab)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(closeTab);
        foreach (var item in Application.Concat(Edit).Concat(Window))
        {
            var selector = item.Selector;
            commands.Register(Prefix + item.Id, _ => { send(selector); return ValueTask.CompletedTask; });
        }
        commands.Register(Prefix + "close", _ => { closeTab(); return ValueTask.CompletedTask; });
    }

    /// <summary>
    /// Sends a standard action (<c>copy:</c>, <c>hide:</c>, <c>performMiniaturize:</c>) to the first object of the
    /// responder chain that takes it, as a menu item without a target does. Only on the application's thread.
    /// </summary>
    /// <param name="selector">The selector of the action.</param>
    /// <returns>True when an object took the action.</returns>
    [SupportedOSPlatform("macos")]
    internal static bool SendAction(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        return application != 0 && SendAction(application, sel_registerName("sendAction:to:from:"), sel_registerName(selector), 0, 0);
    }

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjectiveC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendAction(nint receiver, nint selector, nint action, nint target, nint sender);
}
