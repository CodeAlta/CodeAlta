namespace CodeAlta.Frontend.Commands;

internal sealed class ShellInputRouter
{
    public ShellInputIntent Route(string? rawInput, bool steerRequested)
    {
        var trimmed = rawInput?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return steerRequested
                ? new SteerPromptIntent(string.Empty)
                : new EmptyShellInputIntent();
        }

        if (string.Equals(trimmed, "?", StringComparison.Ordinal))
        {
            return new OpenHelpIntent(FilterText: null);
        }

        if (!trimmed.StartsWith('/'))
        {
            return steerRequested
                ? new SteerPromptIntent(trimmed)
                : new SendPromptIntent(trimmed);
        }

        var commandText = trimmed[1..].Trim();
        if (commandText.Length == 0)
        {
            return new OpenHelpIntent(FilterText: null);
        }

        var separatorIndex = commandText.IndexOf(' ');
        var commandName = separatorIndex >= 0
            ? commandText[..separatorIndex]
            : commandText;
        var arguments = separatorIndex >= 0
            ? commandText[(separatorIndex + 1)..].Trim()
            : null;

        return ShellCommandCatalog.FindByAlias(commandName) switch
        {
            { Id: "CodeAlta.Shell.Help" } => new OpenHelpIntent(arguments),
            { Id: "CodeAlta.Shell.CommandPalette" } => new OpenCommandPaletteIntent(),
            { Id: "CodeAlta.Shell.Exit", SupportsTextCommand: true } => new ExitAppIntent(),
            { Id: "CodeAlta.Shell.ToggleCommandBarMultiLine" } => new ToggleCommandBarMultiLineIntent(),
            { SupportsTextCommand: false } => steerRequested
                ? new SteerPromptIntent(trimmed)
                : new SendPromptIntent(trimmed),
            { Id: "CodeAlta.Project.OpenFolder" } => new OpenFolderIntent(arguments),
            { Id: "CodeAlta.Acp.Manage" } => new OpenAcpManagementIntent(),
            { Id: "CodeAlta.File.Edit" } => new OpenFileEditorIntent(),
            { Id: "CodeAlta.Providers.Manage" } => new OpenModelProvidersIntent(),
            { Id: "CodeAlta.Models.Browse" } => new OpenModelsIntent(),
            { Id: "CodeAlta.Skills.Manage" } => new OpenSkillsIntent(),
            { Id: "CodeAlta.Plugins.Manage" } => new OpenPluginsIntent(),
            { Id: "CodeAlta.Workspace.Settings" } => new OpenWorkspaceSettingsIntent(),
            { Id: "CodeAlta.Shell.FocusSidebar" } => new FocusSidebarIntent(),
            { Id: "CodeAlta.Shell.FocusPrompt" } => new FocusPromptIntent(),
            { Id: "CodeAlta.Shell.FocusModelProvider" } => new FocusModelProviderIntent(),
            { Id: "CodeAlta.Thread.SessionUsage" } => new OpenSessionUsageIntent(),
            { Id: "CodeAlta.Thread.Info" } => new OpenThreadInfoIntent(),
            { Id: "CodeAlta.Thread.ExpandPrompt" } => new OpenExpandedPromptIntent(),
            { Id: "CodeAlta.Thread.Send" } => new SendPromptIntent(arguments ?? string.Empty),
            { Id: "CodeAlta.Thread.Steer" } => new SteerPromptIntent(arguments ?? string.Empty),
            { Id: "CodeAlta.Thread.Abort" } => new AbortThreadIntent(),
            { Id: "CodeAlta.Thread.Compact" } => new CompactThreadIntent(),
            { Id: "CodeAlta.Thread.CloseTab" } => new CloseTabIntent(),
            { Id: "CodeAlta.Thread.TabLeft" } => new TabLeftIntent(),
            { Id: "CodeAlta.Thread.TabRight" } => new TabRightIntent(),
            { Id: "CodeAlta.Thread.MessagePrevious" } => new MessagePreviousIntent(),
            { Id: "CodeAlta.Thread.MessageNext" } => new MessageNextIntent(),
            { Id: "CodeAlta.Thread.MessageFirst" } => new MessageFirstIntent(),
            { Id: "CodeAlta.Thread.MessageLast" } => new MessageLastIntent(),
            { Id: "CodeAlta.Thread.ClearQueue" } => new ClearQueueIntent(),
            { Id: "CodeAlta.Thread.Queue" } => new QueueStatusIntent(),
            _ => new UnknownTextCommandIntent(commandName, arguments)
        };
    }
}
