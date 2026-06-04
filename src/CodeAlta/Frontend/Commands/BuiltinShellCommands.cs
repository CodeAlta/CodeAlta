using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Frontend.Commands;

internal static class BuiltinShellCommands
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

    public static readonly KeySequence PromptsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlH, TerminalModifiers.Ctrl));

    public static readonly KeySequence ModelsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl));

    public static readonly KeySequence SkillsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlK, TerminalModifiers.Ctrl));

    public static readonly KeySequence PluginsShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl));

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

    public static readonly KeySequence RemindersShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlD, TerminalModifiers.Ctrl));

    public static readonly KeySequence ToggleNavigatorShortcutSequence = new(
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
        new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl));

    internal static readonly ShellCommand OpenHelp = new()
    {
        Id = "CodeAlta.Shell.Help",
        Label = "Help",
        Description = "Show shell commands and keyboard shortcuts.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot | ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot,
        Gesture = new KeyGesture(TerminalKey.F1),
        Name = "help",
        SearchText = "? commands shortcuts",
        AdditionalHelpBindings = ["?"],
        ExecuteAsync = static (context, _, _) => new(context.Presenter.ShowHelpDialogAsync(context.GetCommands(), filterText: null)),
    };

    internal static readonly ShellCommand CommandPalette = new()
    {
        Id = "CodeAlta.Shell.CommandPalette",
        Label = "Command Palette",
        Description = "Search and run available shell commands.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
        Name = "command_palette",
        SearchText = "/ palette commands",
        AdditionalHelpBindings = ["/"],
        ShowInCommandBar = true,
        ConsumesGestureWhenUnavailable = false,
        CanExecute = static (context, _) => context.Availability.CanUseCommandPalette(),
        IsVisible = static (context, _) => context.Availability.CanUseCommandPalette(),
        ExecuteAsync = static (context, _, _) => { context.Presenter.ShowCommandPalette(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand Exit = new()
    {
        Id = "CodeAlta.Shell.Exit",
        Label = "Exit",
        Description = "Quit CodeAlta.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Gesture = new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl),
        Name = "exit",
        ExecuteAsync = static (context, _, _) => { context.Dialogs.ExitApp(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand ToggleTerminalLoop = new()
    {
        Id = "CodeAlta.Diagnostics.ToggleTerminalLoop",
        Label = "Loop",
        Description = "Toggle per-frame loop work.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Name = "loop",
        ShowInHelp = false,
        ShowInCommandPalette = false,
        ExecuteAsync = static (context, _, _) => { context.Diagnostics.ToggleTerminalLoop(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand OpenProject = new()
    {
        Id = "CodeAlta.Project.Open",
        Label = "Open",
        Description = "Open a project by name or a rooted folder path.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Gesture = new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl),
        Name = "open",
        SearchText = "project folder open_project open_folder",
        ShowInCommandBar = false,
        ExecuteAsync = static (context, _, _) => { context.Presenter.ShowOpenFolderDialog(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand EditFile = Dialog("CodeAlta.File.Edit", "Edit File", "Open a project file in a dedicated editor tab.", "edit", ShellCommandHelpCategory.General, new KeyGesture(TerminalChar.CtrlE, TerminalModifiers.Ctrl), static context => context.Dialogs.OpenFileEditorAsync(), searchText: "open_file");
    internal static readonly ShellCommand About = Dialog("CodeAlta.Shell.About", "About", "Show CodeAlta version, copyright, and update status.", "about", ShellCommandHelpCategory.General, AboutShortcutSequence, static context => { context.Dialogs.OpenAbout(); return Task.CompletedTask; });
    internal static readonly ShellCommand Skills = Dialog("CodeAlta.Skills.Manage", "Skills", "Browse discovered skills, validation diagnostics, source precedence, and provenance.", "skills", ShellCommandHelpCategory.General, SkillsShortcutSequence, static context => context.Dialogs.OpenSkillsAsync(), searchText: "skill");
    internal static readonly ShellCommand Plugins = Dialog("CodeAlta.Plugins.Manage", "Plugins", "Open plugin management and inspect plugin state, diagnostics, and contributions.", "plugins", ShellCommandHelpCategory.General, PluginsShortcutSequence, static context => context.Dialogs.OpenPluginsAsync(), searchText: "plugin");
    internal static readonly ShellCommand WorkspaceSettings = Dialog("CodeAlta.Workspace.Settings", "Workspace Settings", "Open workspace settings for the navigator and UI theme.", "settings", ShellCommandHelpCategory.General, WorkspaceSettingsShortcutSequence, static context => { context.Dialogs.OpenWorkspaceSettings(); return Task.CompletedTask; }, searchText: "workspace_settings");
    internal static readonly ShellCommand Prompts = Dialog("CodeAlta.Prompts.Manage", "Agent Prompts", "Create, edit, delete, and inspect agent prompts from built-in, global, and project prompt roots.", "prompt", ShellCommandHelpCategory.General, PromptsShortcutSequence, static context => context.Dialogs.OpenPromptsAsync(), searchText: "prompts agent_prompt instructions system_prompt");
    internal static readonly ShellCommand Providers = Dialog("CodeAlta.Providers.Manage", "Model Providers", "Configure enabled model providers, credentials, and connection details.", "model_providers", ShellCommandHelpCategory.General, ModelProvidersShortcutSequence, static context => context.Dialogs.OpenModelProvidersAsync(), searchText: "providers");
    internal static readonly ShellCommand ProvidersRefresh = new()
    {
        Id = "CodeAlta.Providers.Refresh",
        Label = "Refresh Model Providers",
        Description = "Reload model provider configuration from disk and retest provider availability.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Name = "model_providers_refresh",
        SearchText = "providers reload reconnect retest availability",
        ExecuteAsync = static (context, _, cancellationToken) => new(context.Dialogs.RefreshModelProvidersAsync(cancellationToken)),
    };
    internal static readonly ShellCommand Models = Dialog("CodeAlta.Models.Browse", "Models", "Browse provider models and enriched model metadata, then select one for the current prompt or session.", "models", ShellCommandHelpCategory.Inspection, ModelsShortcutSequence, static context => { context.Dialogs.OpenModels(); return Task.CompletedTask; }, ShellCommandPlacement.PromptEditor, searchText: "model_list");
    internal static readonly ShellCommand ApplicationLogs = Dialog("CodeAlta.ApplicationLogs.Open", "Show Logs", "Open application logs captured for the current UI thread.", "logs", ShellCommandHelpCategory.Inspection, ApplicationLogsShortcutSequence, static context => { context.Dialogs.OpenApplicationLogs(); return Task.CompletedTask; }, searchText: "show_logs");

    internal static readonly ShellCommand FocusSidebar = new()
    {
        Id = "CodeAlta.Shell.FocusSidebar",
        Label = "Go to Sidebar",
        Description = "Focus the navigator sidebar on the current selection.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Sequence = FocusSidebarShortcutSequence,
        Name = "go_to_sidebar",
        SearchText = "sidebar",
        ShowInCommandBar = false,
        ExecuteAsync = static (context, _, _) => { context.Navigation.FocusSidebar(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand ToggleNavigator = new()
    {
        Id = "CodeAlta.Shell.ToggleNavigator",
        Label = "Toggle Navigator",
        Description = "Collapse or expand the navigator sidebar.",
        HelpCategory = ShellCommandHelpCategory.Navigation,
        Placement = ShellCommandPlacement.ShellRoot,
        Sequence = ToggleNavigatorShortcutSequence,
        Name = "toggle_navigator",
        ShowInCommandBar = false,
        ExecuteAsync = static (context, _, _) => { context.Navigation.ToggleNavigator(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand FocusPrompt = new()
    {
        Id = "CodeAlta.Shell.FocusPrompt",
        Label = "Go to Prompt",
        Description = "Focus the current session prompt editor.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Sequence = FocusPromptShortcutSequence,
        Name = "go_to_prompt",
        SearchText = "prompt",
        ShowInCommandBar = false,
        ExecuteAsync = static (context, _, _) => { context.Navigation.FocusPrompt(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand FocusModelProvider = new()
    {
        Id = "CodeAlta.Shell.FocusModelProvider",
        Label = "Model",
        Description = "Focus the provider/model selector in the prompt bottom bar.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Name = "model",
        SearchText = "model_selector provider selector",
        ShowInCommandBar = false,
        ExecuteAsync = static (context, _, _) => { context.Navigation.FocusModelProvider(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand ToggleCommandBarMultiLine = new()
    {
        Id = "CodeAlta.Shell.ToggleCommandBarMultiLine",
        Label = "Show More Shortcuts",
        Description = "Toggle the command bar between a stable single-line layout and a multi-line layout.",
        HelpCategory = ShellCommandHelpCategory.General,
        Placement = ShellCommandPlacement.ShellRoot,
        Sequence = ToggleCommandBarMultiLineShortcutSequence,
        Name = "command_bar_lines",
        SearchText = "command_bar bar show more shortcuts show less shortcuts",
        Importance = CommandImportance.Primary,
        GetLabelMarkup = static (context, _) => context.IsCommandBarMultiLine() ? "Show Less Shortcuts" : AnsiMarkup.Escape("Show More Shortcuts"),
        ExecuteAsync = static (context, _, _) => { context.Dialogs.ToggleCommandBarMultiLine(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand SessionUsage = Dialog("CodeAlta.Session.SessionUsage", "Context Usage", "Show context and usage details for the selected session.", "context_usage", ShellCommandHelpCategory.Inspection, SessionUsageShortcutSequence, static context => { context.Dialogs.OpenSessionUsage(); return Task.CompletedTask; }, ShellCommandPlacement.PromptEditor);
    internal static readonly ShellCommand SessionInfo = Dialog("CodeAlta.Session.Info", "Session Info", "Show information about the selected session.", "session_info", ShellCommandHelpCategory.Inspection, SessionInfoShortcutSequence, static context => { context.Dialogs.OpenSessionInfo(); return Task.CompletedTask; }, ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot, canExecute: static context => context.Availability.CanShowSessionInfo());
    internal static readonly ShellCommand Reminders = Dialog("CodeAlta.Session.Reminders", "Reminders", "Create, list, and delete delayed prompt reminders for the selected session.", "reminder", ShellCommandHelpCategory.Session, RemindersShortcutSequence, static context => { context.Dialogs.OpenReminders(); return Task.CompletedTask; }, ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot, searchText: "reminders delayed_prompt", canExecute: static context => context.Availability.CanShowSessionInfo());

    internal static readonly ShellCommand MessagePrevious = ScrollMessage("CodeAlta.Session.MessagePrevious", "Previous Message", "Scroll to the previous user prompt or assistant message in the selected session.", "msg_prev", new KeyGesture(TerminalKey.F3), SessionMessageScrollTarget.Previous);
    internal static readonly ShellCommand MessageNext = ScrollMessage("CodeAlta.Session.MessageNext", "Next Message", "Scroll to the next user prompt or assistant message in the selected session.", "msg_next", new KeyGesture(TerminalKey.F4), SessionMessageScrollTarget.Next);
    internal static readonly ShellCommand MessageFirst = ScrollMessage("CodeAlta.Session.MessageFirst", "First Message", "Scroll to the first user prompt or assistant message in the selected session.", "msg_first", new KeyGesture(TerminalKey.F3, TerminalModifiers.Ctrl), SessionMessageScrollTarget.First);
    internal static readonly ShellCommand MessageLast = ScrollMessage("CodeAlta.Session.MessageLast", "Last Message", "Scroll to the bottom of the latest message in the selected session.", "msg_last", new KeyGesture(TerminalKey.F4, TerminalModifiers.Ctrl), SessionMessageScrollTarget.Last);

    internal static readonly ShellCommand ExpandPrompt = new()
    {
        Id = "CodeAlta.Session.ExpandPrompt",
        Label = "Full Prompt",
        Description = "Open the current prompt in a large editor window. Enter, Escape, or Ctrl+Enter closes the window and keeps the draft.",
        HelpCategory = ShellCommandHelpCategory.Prompt,
        Placement = ShellCommandPlacement.PromptEditor,
        Gesture = new KeyGesture(TerminalKey.F6),
        Name = "full_prompt",
        CanExecute = static (context, _) => context.Availability.IsPromptEnabled(),
        ExecuteAsync = static (context, _, _) => { context.Dialogs.OpenExpandedPromptEditor(); return ValueTask.CompletedTask; },
    };

    internal static readonly ShellCommand SendPrompt = new()
    {
        Id = "CodeAlta.Session.Send",
        Label = "Send",
        Description = "Send the current prompt.",
        HelpCategory = ShellCommandHelpCategory.Prompt,
        Placement = ShellCommandPlacement.PromptEditor,
        Name = "send",
        ShowInCommandBar = false,
        CanExecute = static (context, _) => context.Availability.CanSendPrompt(),
        ExecuteAsync = static (context, _, cancellationToken) => new(context.PromptDispatch.SendPromptAsync(context.PromptInput.GetPromptText(), steer: false, cancellationToken)),
    };

    internal static readonly ShellCommand SteerPrompt = new()
    {
        Id = "CodeAlta.Session.Steer",
        Label = "Steer",
        Description = "Send an immediate steering instruction to the selected session.",
        HelpCategory = ShellCommandHelpCategory.Prompt,
        Placement = ShellCommandPlacement.PromptEditor,
        Gesture = new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl),
        Name = "steer",
        ShowInCommandPalette = false,
        CanExecute = static (context, _) => context.Availability.CanSteerPrompt(),
        ExecuteAsync = static (context, _, cancellationToken) => new(context.PromptDispatch.SendPromptAsync(context.PromptInput.GetPromptText(), steer: true, cancellationToken)),
    };

    internal static readonly ShellCommand AbortSession = Session("CodeAlta.Session.Abort", "Abort", "Abort the selected session run.", "abort", new KeyGesture(TerminalKey.F8), static context => context.SessionActions.AbortSelectedSessionAsync(), static context => context.Availability.CanAbortSelectedSession());
    internal static readonly ShellCommand CloseTab = new()
    {
        Id = "CodeAlta.Session.CloseTab",
        Label = "Close Tab",
        Description = "Close the current session tab or draft tab.",
        HelpCategory = ShellCommandHelpCategory.Session,
        Placement = ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot,
        Gesture = new KeyGesture(TerminalChar.CtrlW, TerminalModifiers.Ctrl),
        Name = "close_tab",
        SearchText = "close",
        CanExecute = static (context, _) => context.Availability.CanCloseCurrentTab(),
        ExecuteAsync = static (context, _, _) => new(context.Tabs.CloseCurrentTabAsync()),
    };
    internal static readonly ShellCommand TabLeft = new()
    {
        Id = "CodeAlta.Session.TabLeft",
        Label = "Tab Left",
        Description = "Select the tab to the left, wrapping to the last tab when needed.",
        HelpCategory = ShellCommandHelpCategory.Session,
        Placement = ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot,
        Gesture = new KeyGesture(TerminalKey.Left, TerminalModifiers.Ctrl | TerminalModifiers.Alt),
        Name = "tab_left",
        ExecuteAsync = static (context, _, _) => new(context.Navigation.SelectRelativeTabAsync(-1)),
    };
    internal static readonly ShellCommand TabRight = new()
    {
        Id = "CodeAlta.Session.TabRight",
        Label = "Tab Right",
        Description = "Select the tab to the right, wrapping to the first tab when needed.",
        HelpCategory = ShellCommandHelpCategory.Session,
        Placement = ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot,
        Gesture = new KeyGesture(TerminalKey.Right, TerminalModifiers.Ctrl | TerminalModifiers.Alt),
        Name = "tab_right",
        ExecuteAsync = static (context, _, _) => new(context.Navigation.SelectRelativeTabAsync(1)),
    };
    internal static readonly ShellCommand ClearQueue = Session("CodeAlta.Session.ClearQueue", "Clear Queue", "Clear all queued prompts for the selected session.", "clear_queue", new KeyGesture(TerminalKey.F10), static context => context.SessionActions.ClearSelectedSessionQueueAsync(), static context => context.Availability.CanClearSelectedSessionQueue());
    internal static readonly ShellCommand Compact = Session("CodeAlta.Session.Compact", "Compact", "Compact the selected session when it is idle.", "compact", new KeyGesture(TerminalKey.F11, TerminalModifiers.Ctrl), static context => context.SessionActions.CompactSelectedSessionAsync(), static context => context.Availability.CanCompactSelectedSession());

    public static IEnumerable<ShellCommand> Enumerate()
    {
        yield return OpenHelp;
        yield return CommandPalette;
        yield return Exit;
        yield return ToggleTerminalLoop;
        yield return OpenProject;
        yield return EditFile;
        yield return About;
        yield return Skills;
        yield return Plugins;
        yield return WorkspaceSettings;
        yield return Prompts;
        yield return FocusSidebar;
        yield return ToggleNavigator;
        yield return FocusPrompt;
        yield return FocusModelProvider;
        yield return Providers;
        yield return ProvidersRefresh;
        yield return Models;
        yield return ApplicationLogs;
        yield return ToggleCommandBarMultiLine;
        yield return SessionUsage;
        yield return SessionInfo;
        yield return Reminders;
        yield return MessagePrevious;
        yield return MessageNext;
        yield return MessageFirst;
        yield return MessageLast;
        yield return ExpandPrompt;
        yield return SendPrompt;
        yield return SteerPrompt;
        yield return AbortSession;
        yield return CloseTab;
        yield return TabLeft;
        yield return TabRight;
        yield return ClearQueue;
        yield return Compact;
    }

    private static ShellCommand Dialog(string id, string label, string description, string name, ShellCommandHelpCategory category, KeyGesture gesture, Func<ShellCommandContext, Task> executeAsync, ShellCommandPlacement placement = ShellCommandPlacement.ShellRoot, string? searchText = null, Func<ShellCommandContext, bool>? canExecute = null)
        => new()
        {
            Id = id,
            Label = label,
            Description = description,
            HelpCategory = category,
            Placement = placement,
            Gesture = gesture,
            Name = name,
            SearchText = searchText,
            CanExecute = canExecute is null ? null : (context, _) => canExecute(context),
            ExecuteAsync = (context, _, _) => new(executeAsync(context)),
        };

    private static ShellCommand Dialog(string id, string label, string description, string name, ShellCommandHelpCategory category, KeySequence sequence, Func<ShellCommandContext, Task> executeAsync, ShellCommandPlacement placement = ShellCommandPlacement.ShellRoot, string? searchText = null, Func<ShellCommandContext, bool>? canExecute = null)
        => new()
        {
            Id = id,
            Label = label,
            Description = description,
            HelpCategory = category,
            Placement = placement,
            Sequence = sequence,
            Name = name,
            SearchText = searchText,
            CanExecute = canExecute is null ? null : (context, _) => canExecute(context),
            ExecuteAsync = (context, _, _) => new(executeAsync(context)),
        };

    private static ShellCommand ScrollMessage(string id, string label, string description, string name, KeyGesture gesture, SessionMessageScrollTarget target)
        => new()
        {
            Id = id,
            Label = label,
            Description = description,
            HelpCategory = ShellCommandHelpCategory.Navigation,
            Placement = ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot,
            Gesture = gesture,
            Name = name,
            ShowInCommandBar = false,
            CanExecute = static (context, _) => context.Availability.CanShowSessionInfo(),
            ExecuteAsync = (context, _, _) => new(context.Navigation.ScrollSelectedSessionMessageAsync(target)),
        };

    private static ShellCommand Session(string id, string label, string description, string name, KeyGesture gesture, Func<ShellCommandContext, Task> executeAsync, Func<ShellCommandContext, bool> canExecute)
        => new()
        {
            Id = id,
            Label = label,
            Description = description,
            HelpCategory = ShellCommandHelpCategory.Session,
            Placement = ShellCommandPlacement.PromptEditor,
            Gesture = gesture,
            Name = name,
            CanExecute = (context, _) => canExecute(context),
            ExecuteAsync = (context, _, _) => new(executeAsync(context)),
        };
}
