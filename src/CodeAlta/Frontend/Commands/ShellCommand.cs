namespace CodeAlta.Frontend.Commands;

internal abstract record ShellCommand;

internal sealed record SubmitPromptCommand(string? Text, bool Steer) : ShellCommand;

internal sealed record AbortSelectedSessionCommand : ShellCommand;

internal sealed record CompactSelectedSessionCommand : ShellCommand;

internal sealed record CloseCurrentTabCommand : ShellCommand;

internal sealed record SelectRelativeTabCommand(int Offset) : ShellCommand;

internal sealed record ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget Target) : ShellCommand;

internal enum SessionMessageScrollTarget
{
    Previous,
    Next,
    First,
    Last,
}

internal sealed record OpenHelpCommand(string? FilterText = null) : ShellCommand;

internal sealed record OpenCommandPaletteCommand : ShellCommand;

internal sealed record ExitAppCommand : ShellCommand;

internal sealed record ToggleCommandBarMultiLineCommand : ShellCommand;

internal sealed record OpenFolderCommand(string? InitialPath = null) : ShellCommand;

internal sealed record OpenModelProvidersCommand : ShellCommand;

internal sealed record OpenAboutCommand : ShellCommand;

internal sealed record OpenModelsCommand : ShellCommand;

internal sealed record OpenApplicationLogsCommand : ShellCommand;

internal sealed record OpenFileEditorCommand : ShellCommand;

internal sealed record OpenSkillsCommand : ShellCommand;

internal sealed record OpenPluginsCommand : ShellCommand;

internal sealed record OpenMcpServersCommand : ShellCommand;

internal sealed record OpenWorkspaceSettingsCommand : ShellCommand;

internal sealed record FocusSidebarCommand : ShellCommand;

internal sealed record FocusPromptCommand : ShellCommand;

internal sealed record FocusModelProviderCommand : ShellCommand;

internal sealed record OpenSessionUsageCommand : ShellCommand;

internal sealed record OpenSessionInfoCommand : ShellCommand;

internal sealed record OpenExpandedPromptCommand : ShellCommand;

internal sealed record ClearSelectedSessionQueueCommand : ShellCommand;

internal sealed record ExecutePluginTextCommand(string CommandName, string? Arguments) : ShellCommand;
