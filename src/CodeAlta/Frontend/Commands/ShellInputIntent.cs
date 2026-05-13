namespace CodeAlta.Frontend.Commands;

internal abstract record ShellInputIntent;

internal sealed record EmptyShellInputIntent : ShellInputIntent;

internal sealed record SendPromptIntent(string PromptText) : ShellInputIntent;

internal sealed record SteerPromptIntent(string PromptText) : ShellInputIntent;

internal sealed record OpenHelpIntent(string? FilterText) : ShellInputIntent;

internal sealed record OpenCommandPaletteIntent : ShellInputIntent;

internal sealed record ExitAppIntent : ShellInputIntent;

internal sealed record ToggleCommandBarMultiLineIntent : ShellInputIntent;

internal sealed record FocusSidebarIntent : ShellInputIntent;

internal sealed record FocusPromptIntent : ShellInputIntent;

internal sealed record FocusModelProviderIntent : ShellInputIntent;

internal sealed record OpenModelProvidersIntent : ShellInputIntent;

internal sealed record OpenModelsIntent : ShellInputIntent;

internal sealed record OpenAcpManagementIntent : ShellInputIntent;

internal sealed record OpenFileEditorIntent : ShellInputIntent;

internal sealed record OpenSkillsIntent : ShellInputIntent;

internal sealed record OpenPluginsIntent : ShellInputIntent;

internal sealed record OpenSessionUsageIntent : ShellInputIntent;

internal sealed record OpenThreadInfoIntent : ShellInputIntent;

internal sealed record OpenExpandedPromptIntent : ShellInputIntent;

internal sealed record OpenFolderIntent(string? InitialPath) : ShellInputIntent;

internal sealed record AbortThreadIntent : ShellInputIntent;

internal sealed record CompactThreadIntent : ShellInputIntent;

internal sealed record CloseTabIntent : ShellInputIntent;

internal sealed record TabLeftIntent : ShellInputIntent;

internal sealed record TabRightIntent : ShellInputIntent;

internal sealed record MessagePreviousIntent : ShellInputIntent;

internal sealed record MessageNextIntent : ShellInputIntent;

internal sealed record MessageFirstIntent : ShellInputIntent;

internal sealed record MessageLastIntent : ShellInputIntent;

internal sealed record ClearQueueIntent : ShellInputIntent;

internal sealed record QueueStatusIntent : ShellInputIntent;

internal sealed record UnknownTextCommandIntent(string CommandName, string? Arguments = null) : ShellInputIntent;
