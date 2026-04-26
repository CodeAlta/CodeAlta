using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Frontend.Commands;

internal static class ShellCommandCatalog
{
    public static readonly KeySequence FocusSidebarShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl));

    public static readonly KeySequence FocusPromptShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl));

    public static readonly KeySequence ModelProvidersShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlM, TerminalModifiers.Ctrl));

    public static readonly KeySequence SkillsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlK, TerminalModifiers.Ctrl));

    public static readonly KeySequence SessionUsageShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlU, TerminalModifiers.Ctrl));

    public static readonly KeySequence ToggleCommandBarMultiLineShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlB, TerminalModifiers.Ctrl));

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
            "CodeAlta.Shell.Exit",
            "Exit",
            "Quit CodeAlta.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl)),
        new(
            "CodeAlta.Project.OpenFolder",
            "Open Project",
            "Open a rooted path or switch to a visible project by name from the same dialog.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl),
            CommandName: "open_folder",
            Aliases: ["open"],
            ShowInCommandBar: false),
        new(
            "CodeAlta.File.Edit",
            "Edit File",
            "Open a project file in a dedicated editor tab.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlE, TerminalModifiers.Ctrl),
            CommandName: "edit",
            Aliases: ["open_file"]),
        new(
            "CodeAlta.Acp.Manage",
            "ACP Agents",
            "Browse the ACP registry and inspect installed ACP backends.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            CommandName: "acp_agents",
            Aliases: ["acp"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Skills.Manage",
            "Skills",
            "Browse discovered skills, validation diagnostics, source precedence, and provenance.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: SkillsShortcutSequence,
            CommandName: "skills",
            Aliases: ["skill"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Shell.FocusSidebar",
            "Go to Sidebar",
            "Focus the navigator sidebar on the current selection.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: FocusSidebarShortcutSequence,
            Aliases: ["sidebar"],
            ShowInCommandBar: false),
        new(
            "CodeAlta.Shell.FocusPrompt",
            "Go to Prompt",
            "Focus the current thread prompt editor.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: FocusPromptShortcutSequence,
            Aliases: ["prompt"],
            ShowInCommandBar: false),
        new(
            "CodeAlta.Providers.Manage",
            "Model Providers",
            "Configure enabled model providers, credentials, and connection details.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: ModelProvidersShortcutSequence,
            CommandName: "model_providers",
            Aliases: ["providers", "models"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Shell.ToggleCommandBarMultiLine",
            "Command Bar Lines",
            "Toggle the command bar between a stable single-line layout and a multi-line layout.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: ToggleCommandBarMultiLineShortcutSequence,
            CommandName: "command_bar_lines",
            Aliases: ["command_bar", "bar"],
            ShowInCommandBar: true),
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
            "CodeAlta.Thread.MessagePrevious",
            "Previous Message",
            "Scroll to the previous user prompt or assistant message in the selected thread.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanShowThreadInfo,
            Gesture: new KeyGesture(TerminalKey.F3),
            CommandName: "msg_prev",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Thread.MessageNext",
            "Next Message",
            "Scroll to the next user prompt or assistant message in the selected thread.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanShowThreadInfo,
            Gesture: new KeyGesture(TerminalKey.F4),
            CommandName: "msg_next",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Thread.MessageFirst",
            "First Message",
            "Scroll to the first user prompt or assistant message in the selected thread.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanShowThreadInfo,
            Gesture: new KeyGesture(TerminalKey.F3, TerminalModifiers.Ctrl),
            CommandName: "msg_first",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Thread.MessageLast",
            "Last Message",
            "Scroll to the bottom of the latest message in the selected thread.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanShowThreadInfo,
            Gesture: new KeyGesture(TerminalKey.F4, TerminalModifiers.Ctrl),
            CommandName: "msg_last",
            ShowInCommandBar: false),
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
            Gesture: new KeyGesture(TerminalKey.F5),
            ShowInCommandPalette: false,
            SupportsTextCommand: false),
        new(
            "CodeAlta.Thread.Delegate",
            "Delegate",
            "Create a delegated internal thread from the current project thread.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.ThreadOnly,
            ShellCommandAvailability.CanDelegate,
            Gesture: new KeyGesture(TerminalKey.F7),
            Aliases: ["delegate"],
            ShowInCommandPalette: false,
            SupportsTextCommand: false),
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
            Gesture: new KeyGesture(TerminalChar.CtrlW, TerminalModifiers.Ctrl),
            Aliases: ["close"]),
        new(
            "CodeAlta.Thread.TabLeft",
            "Tab Left",
            "Select the tab to the left, wrapping to the last tab when needed.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalKey.Left, TerminalModifiers.Alt),
            Aliases: ["tab_left"]),
        new(
            "CodeAlta.Thread.TabRight",
            "Tab Right",
            "Select the tab to the right, wrapping to the first tab when needed.",
            ShellCommandHelpCategory.Thread,
            ShellCommandScope.DraftOrThread,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalKey.Right, TerminalModifiers.Alt),
            Aliases: ["tab_right"]),
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
