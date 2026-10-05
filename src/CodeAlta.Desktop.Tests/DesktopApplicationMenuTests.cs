using CodeAlta.Desktop;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;

namespace CodeAlta.Desktop.Tests;

/// <summary>The descriptor and the commands of the macOS menu bar; no native menu is created.</summary>
[TestClass]
public sealed class DesktopApplicationMenuTests
{
    [TestMethod]
    public void MenuBar_HasTheApplicationEditAndWindowMenusWithTheStandardShortcuts()
    {
        var menu = DesktopApplicationMenu.Build("CodeAlta (dev)", DesktopShell.ExitCommand);

        CollectionAssert.AreEqual(new[] { "CodeAlta (dev)", "Edit", "Window" }, menu.Select(static item => item.Text).ToArray());
        Assert.IsTrue(menu.All(static item => item.Kind == NeoMenuItemKind.Submenu), "The menu bar takes submenus only.");
        CollectionAssert.AreEqual(new[]
        {
            "Hide CodeAlta (dev)|Meta+H", "Hide Others|Alt+Meta+H", "Show All|", "|", "Quit CodeAlta (dev)|Meta+Q",
        }, Lines(menu[0]));
        CollectionAssert.AreEqual(new[] { "Undo|Meta+Z", "Redo|Shift+Meta+Z", "|", "Cut|Meta+X", "Copy|Meta+C", "Paste|Meta+V", "Select All|Meta+A" }, Lines(menu[1]));
        CollectionAssert.AreEqual(new[] { "Minimize|Meta+M", "Close|Meta+W" }, Lines(menu[2]));
        // A role item has no shortcut, and the Quit role would skip the page's questions.
        Assert.IsFalse(All(menu).Any(static item => item.Kind == NeoMenuItemKind.Role));
    }

    [TestMethod]
    public async Task MenuBar_HasUniqueIdentifiersAndShortcuts_AndIsAcceptedAsAMenu()
    {
        var menu = DesktopApplicationMenu.Build("CodeAlta", DesktopShell.ExitCommand);
        var items = All(menu).ToArray();

        Assert.AreEqual(items.Length, items.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count());
        var shortcuts = items.Where(static item => item.Accelerator is not null).Select(static item => item.Accelerator!).ToArray();
        Assert.AreEqual(11, shortcuts.Length);
        Assert.AreEqual(shortcuts.Length, shortcuts.Distinct(StringComparer.Ordinal).Count());

        await using var commands = new NeoCommandService();
        await using var menus = new NeoMenuService(commands);
        await menus.SetMenuAsync(DesktopApplicationMenu.Target, menu); // Validates the whole tree.
        Assert.AreEqual(3, menus.GetMenu(DesktopApplicationMenu.Target).Count);
    }

    [TestMethod]
    public async Task EveryItemHasARegisteredCommand_QuitIsTheShellsExitAndTheOthersSendTheirAction()
    {
        var menu = DesktopApplicationMenu.Build("CodeAlta", DesktopShell.ExitCommand);
        await using var commands = new NeoCommandService();
        var sent = new List<string>();
        var exits = 0;
        // The shell registers its exit once, for the tray's menu and for this one.
        commands.Register(DesktopShell.ExitCommand, _ => { exits++; return ValueTask.CompletedTask; });
        DesktopApplicationMenu.Register(commands, sent.Add);

        var items = All(menu).Where(static item => item.Kind == NeoMenuItemKind.Command).ToArray();
        Assert.AreEqual(12, items.Length);
        foreach (var item in items) Assert.AreEqual(NeoDesktopStatus.Success, await commands.ActivateAsync(item.CommandId!), item.Id);

        Assert.AreEqual(DesktopShell.ExitCommand, items.Single(static item => item.Id == "quit").CommandId);
        Assert.AreEqual(1, exits, "Only Quit exits.");
        CollectionAssert.AreEqual(new[]
        {
            "hide:", "hideOtherApplications:", "unhideAllApplications:", "undo:", "redo:", "cut:", "copy:", "paste:", "selectAll:", "performMiniaturize:", "performClose:",
        }, sent);
        Assert.Throws<ArgumentException>(() => DesktopApplicationMenu.Register(commands, sent.Add), "A command is registered once.");
    }

    private static string[] Lines(NeoMenuItem menu) => menu.Children.Select(static item => item.Text + "|" + item.Accelerator).ToArray();

    private static IEnumerable<NeoMenuItem> All(IEnumerable<NeoMenuItem> items) => items.SelectMany(static item => All(item.Children).Prepend(item));
}
