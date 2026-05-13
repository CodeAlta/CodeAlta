namespace CodeAlta.Frontend.Commands;

internal abstract record ShellCommand;

internal sealed record SubmitPromptCommand(string? Text, bool Steer) : ShellCommand;

internal sealed record AbortSelectedThreadCommand : ShellCommand;

internal sealed record CompactSelectedThreadCommand : ShellCommand;

internal sealed record CloseCurrentTabCommand : ShellCommand;

internal sealed record SelectRelativeTabCommand(int Offset) : ShellCommand;

internal sealed record ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget Target) : ShellCommand;

internal enum ThreadMessageScrollTarget
{
    Previous,
    Next,
    First,
    Last,
}

internal sealed record ShowQueueStatusCommand : ShellCommand;

internal sealed record OpenHelpCommand(string? FilterText = null) : ShellCommand;

internal sealed record OpenCommandPaletteCommand : ShellCommand;

internal sealed record ExitAppCommand : ShellCommand;

internal sealed record ToggleCommandBarMultiLineCommand : ShellCommand;

internal sealed record OpenFolderCommand(string? InitialPath = null) : ShellCommand;

internal sealed record OpenModelProvidersCommand : ShellCommand;

internal sealed record OpenModelsCommand : ShellCommand;

internal sealed record OpenAcpManagementCommand : ShellCommand;

internal sealed record OpenFileEditorCommand : ShellCommand;

internal sealed record OpenSkillsCommand : ShellCommand;

internal sealed record OpenPluginsCommand : ShellCommand;

internal sealed record FocusSidebarCommand : ShellCommand;

internal sealed record FocusPromptCommand : ShellCommand;

internal sealed record FocusModelProviderCommand : ShellCommand;

internal sealed record OpenSessionUsageCommand : ShellCommand;

internal sealed record OpenThreadInfoCommand : ShellCommand;

internal sealed record OpenExpandedPromptCommand : ShellCommand;

internal sealed record ClearSelectedThreadQueueCommand : ShellCommand;

internal sealed record ExecutePluginTextCommand(string CommandName, string? Arguments) : ShellCommand;
