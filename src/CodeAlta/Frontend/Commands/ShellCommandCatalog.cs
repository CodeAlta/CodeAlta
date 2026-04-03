using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Frontend.Commands;

internal static class ShellCommandCatalog
{
    public static readonly KeySequence SessionUsageShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlU, TerminalModifiers.Ctrl));

    public static readonly KeySequence ThreadInfoShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlT, TerminalModifiers.Ctrl));

    public static readonly IReadOnlyList<ShellCommandMetadata> Commands =
    [
        new(
            "CodeAlta.Shell.Help",
            "Help",
            "Show shell commands, textual aliases, and keyboard shortcuts.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalKey.F1),
            Aliases: ["help"]),
        new(
            "CodeAlta.Shell.CommandPalette",
            "Command Palette",
            "Search and run available shell commands.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl)),
        new(
            "CodeAlta.Project.OpenFolder",
            "Open Folder",
            "Open a folder picker dialog and add that directory to the project list.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl),
            Aliases: ["open"],
            ShowInCommandBar: false),
        new(
            "CodeAlta.Thread.SessionUsage",
            "Context Usage",
            "Show context and usage details for the selected backend session.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.Always,
            Sequence: SessionUsageShortcutSequence),
        new(
            "CodeAlta.Thread.Info",
            "Thread Info",
            "Show information about the selected thread.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanShowThreadInfo,
            Sequence: ThreadInfoShortcutSequence),
        new(
            "CodeAlta.Thread.ExpandPrompt",
            "Full Prompt",
            "Open the current prompt in a large editor window. Escape closes the window and keeps the draft.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.PromptEnabled,
            Gesture: new KeyGesture(TerminalKey.F6)),
        new(
            "CodeAlta.Thread.Send",
            "Send",
            "Send the current prompt.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.CanSend,
            ShowInCommandBar: false),
        new(
            "CodeAlta.Thread.Steer",
            "Steer",
            "Send an immediate steering instruction to the selected thread.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanSteer,
            Gesture: new KeyGesture(TerminalKey.F5)),
        new(
            "CodeAlta.Thread.Delegate",
            "Delegate",
            "Create a delegated internal thread from the current project thread.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanDelegate,
            Gesture: new KeyGesture(TerminalKey.F7),
            Aliases: ["delegate"]),
        new(
            "CodeAlta.Thread.Abort",
            "Abort",
            "Abort the selected thread run.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanAbort,
            Gesture: new KeyGesture(TerminalKey.F8),
            Aliases: ["abort"]),
        new(
            "CodeAlta.Thread.CloseTab",
            "Close Tab",
            "Close the current thread tab or draft tab.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.CanCloseTab,
            Gesture: new KeyGesture(TerminalKey.F9),
            Aliases: ["close"]),
        new(
            "CodeAlta.Thread.ClearQueue",
            "Clear Queue",
            "Clear all queued prompts for the selected thread.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanClearQueue,
            Gesture: new KeyGesture(TerminalKey.F10)),
        new(
            "CodeAlta.Thread.Compact",
            "Compact",
            "Compact the selected thread session when it is idle.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanCompact,
            Gesture: new KeyGesture(TerminalKey.F11),
            Aliases: ["compact"]),
        new(
            "CodeAlta.Thread.Queue",
            "Queue",
            "Show the current queued prompt count for the selected thread.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanCloseTab,
            Aliases: ["queue"],
            ShowInCommandBar: false)
    ];

    public static ShellCommandMetadata? FindByAlias(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        return Commands.FirstOrDefault(
            command => command.Aliases.Any(
                candidate => string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase)));
    }

    public static ShellCommandMetadata Get(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        return Commands.First(command => string.Equals(command.Id, commandId, StringComparison.Ordinal));
    }
}
