using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Commands;
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

    public static readonly KeySequence AboutShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlA, TerminalModifiers.Ctrl));

    public static readonly KeySequence ModelProvidersShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlR, TerminalModifiers.Ctrl));

    public static readonly KeySequence ModelsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl));

    public static readonly KeySequence SkillsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlK, TerminalModifiers.Ctrl));

    public static readonly KeySequence PluginsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl));

    // Ctrl+G Ctrl+M is intentionally not used for MCP because some terminal
    // backends report Enter as Ctrl+M. Ctrl+G Ctrl+Y is otherwise unused by the
    // shell command catalog and avoids prompt submission ambiguity.
    public static readonly KeySequence McpServersShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlY, TerminalModifiers.Ctrl));

    public static readonly KeySequence WorkspaceSettingsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlW, TerminalModifiers.Ctrl));

    public static readonly KeySequence ApplicationLogsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlL, TerminalModifiers.Ctrl));

    public static readonly KeySequence SessionUsageShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlU, TerminalModifiers.Ctrl));

    public static readonly KeySequence ToggleCommandBarMultiLineShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlB, TerminalModifiers.Ctrl));

    public static readonly KeySequence SessionInfoShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlT, TerminalModifiers.Ctrl));

    public static readonly KeySequence ToggleNavigatorShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl));

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
            AdditionalHelpBindings: ["?"],
            Aliases: ["help"]),
        new(
            "CodeAlta.Shell.CommandPalette",
            "Command Palette",
            "Search and run available shell commands.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
            AdditionalHelpBindings: ["/"]),
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
            "CodeAlta.Shell.About",
            "About",
            "Show CodeAlta version, copyright, and update status.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: AboutShortcutSequence,
            CommandName: "about",
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
            "CodeAlta.Plugins.Manage",
            "Plugins",
            "Open plugin management and inspect plugin state, diagnostics, and contributions.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: PluginsShortcutSequence,
            CommandName: "plugins",
            Aliases: ["plugin"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Mcp.Manage",
            "MCP Servers",
            "Open MCP server management and inspect configuration, policy, overlay state, and cached tool counts.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: McpServersShortcutSequence,
            CommandName: "mcp",
            Aliases: ["mcp_servers"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Workspace.Settings",
            "Workspace Settings",
            "Open workspace settings for the navigator and UI theme.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: WorkspaceSettingsShortcutSequence,
            CommandName: "settings",
            Aliases: ["workspace_settings"],
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
            "CodeAlta.Shell.ToggleNavigator",
            "Toggle Navigator",
            "Collapse or expand the navigator sidebar.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: ToggleNavigatorShortcutSequence,
            ShowInCommandBar: false,
            SupportsTextCommand: false),
        new(
            "CodeAlta.Shell.FocusPrompt",
            "Go to Prompt",
            "Focus the current session prompt editor.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: FocusPromptShortcutSequence,
            Aliases: ["prompt"],
            ShowInCommandBar: false),
        new(
            "CodeAlta.Shell.FocusModelProvider",
            "Model",
            "Focus the provider/model selector in the prompt bottom bar.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            CommandName: "model",
            Aliases: ["model_selector"],
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
            Aliases: ["providers"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Models.Browse",
            "Models",
            "Browse provider models and enriched model metadata, then select one for the current prompt or session.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.Always,
            Sequence: ModelsShortcutSequence,
            CommandName: "models",
            Aliases: ["model_list"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.ApplicationLogs.Open",
            "Show Logs",
            "Open application logs captured for the current UI thread.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: ApplicationLogsShortcutSequence,
            CommandName: "logs",
            Aliases: ["show_logs"],
            ShowInCommandBar: true),
        new(
            "CodeAlta.Shell.ToggleCommandBarMultiLine",
            "Show More Shortcuts",
            "Toggle the command bar between a stable single-line layout and a multi-line layout.",
            ShellCommandHelpCategory.General,
            ShellCommandScope.AnyShell,
            ShellCommandAvailability.Always,
            Sequence: ToggleCommandBarMultiLineShortcutSequence,
            CommandName: "command_bar_lines",
            Aliases: ["command_bar", "bar"],
            Importance: CommandImportance.Primary,
            ShowInCommandBar: true),
        new(
            "CodeAlta.Session.SessionUsage",
            "Context Usage",
            "Show context and usage details for the selected session.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.Always,
            Sequence: SessionUsageShortcutSequence),
        new(
            "CodeAlta.Session.Info",
            "Session Info",
            "Show information about the selected session.",
            ShellCommandHelpCategory.Inspection,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanShowSessionInfo,
            Sequence: SessionInfoShortcutSequence,
            CommandName: "session_info",
            Aliases: ["session_info"]),
        new(
            "CodeAlta.Session.MessagePrevious",
            "Previous Message",
            "Scroll to the previous user prompt or assistant message in the selected session.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanShowSessionInfo,
            Gesture: new KeyGesture(TerminalKey.F3),
            CommandName: "msg_prev",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Session.MessageNext",
            "Next Message",
            "Scroll to the next user prompt or assistant message in the selected session.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanShowSessionInfo,
            Gesture: new KeyGesture(TerminalKey.F4),
            CommandName: "msg_next",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Session.MessageFirst",
            "First Message",
            "Scroll to the first user prompt or assistant message in the selected session.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanShowSessionInfo,
            Gesture: new KeyGesture(TerminalKey.F3, TerminalModifiers.Ctrl),
            CommandName: "msg_first",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Session.MessageLast",
            "Last Message",
            "Scroll to the bottom of the latest message in the selected session.",
            ShellCommandHelpCategory.Navigation,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanShowSessionInfo,
            Gesture: new KeyGesture(TerminalKey.F4, TerminalModifiers.Ctrl),
            CommandName: "msg_last",
            ShowInCommandBar: false),
        new(
            "CodeAlta.Session.ExpandPrompt",
            "Full Prompt",
            "Open the current prompt in a large editor window. Enter, Escape, or Ctrl+Enter closes the window and keeps the draft.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.PromptEnabled,
            Gesture: new KeyGesture(TerminalKey.F6)),
        new(
            "CodeAlta.Session.Send",
            "Send",
            "Send the current prompt.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.CanSend,
            ShowInCommandBar: false),
        new(
            "CodeAlta.Session.Steer",
            "Steer",
            "Send an immediate steering instruction to the selected session.",
            ShellCommandHelpCategory.Prompt,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanSteer,
            Gesture: new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl),
            ShowInCommandPalette: false,
            SupportsTextCommand: false),
        new(
            "CodeAlta.Session.Abort",
            "Abort",
            "Abort the selected session run.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanAbort,
            Gesture: new KeyGesture(TerminalKey.F8),
            Aliases: ["abort"]),
        new(
            "CodeAlta.Session.CloseTab",
            "Close Tab",
            "Close the current session tab or draft tab.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.CanCloseTab,
            Gesture: new KeyGesture(TerminalChar.CtrlW, TerminalModifiers.Ctrl),
            Aliases: ["close"]),
        new(
            "CodeAlta.Session.TabLeft",
            "Tab Left",
            "Select the tab to the left, wrapping to the last tab when needed.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalKey.Left, TerminalModifiers.Ctrl | TerminalModifiers.Alt),
            Aliases: ["tab_left"]),
        new(
            "CodeAlta.Session.TabRight",
            "Tab Right",
            "Select the tab to the right, wrapping to the first tab when needed.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.DraftOrSession,
            ShellCommandAvailability.Always,
            Gesture: new KeyGesture(TerminalKey.Right, TerminalModifiers.Ctrl | TerminalModifiers.Alt),
            Aliases: ["tab_right"]),
        new(
            "CodeAlta.Session.ClearQueue",
            "Clear Queue",
            "Clear all queued prompts for the selected session.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanClearQueue,
            Gesture: new KeyGesture(TerminalKey.F10)),
        new(
            "CodeAlta.Session.Compact",
            "Compact",
            "Compact the selected session when it is idle.",
            ShellCommandHelpCategory.Session,
            ShellCommandScope.SessionOnly,
            ShellCommandAvailability.CanCompact,
            Gesture: new KeyGesture(TerminalKey.F11, TerminalModifiers.Ctrl),
            Aliases: ["compact"]),
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

    public static bool Contains(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        return Commands.Any(command => string.Equals(command.Id, commandId, StringComparison.Ordinal));
    }
}
