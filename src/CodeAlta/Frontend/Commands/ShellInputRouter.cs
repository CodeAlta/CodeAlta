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
            { Id: "CodeAlta.File.Edit" } => new OpenFileEditorIntent(),
            { Id: "CodeAlta.Shell.About" } => new OpenAboutIntent(),
            { Id: "CodeAlta.Providers.Manage" } => new OpenModelProvidersIntent(),
            { Id: "CodeAlta.Models.Browse" } => new OpenModelsIntent(),
            { Id: "CodeAlta.ApplicationLogs.Open" } => new OpenApplicationLogsIntent(),
            { Id: "CodeAlta.Skills.Manage" } => new OpenSkillsIntent(),
            { Id: "CodeAlta.Plugins.Manage" } => new OpenPluginsIntent(),
            { Id: "CodeAlta.Mcp.Manage" } => new OpenMcpServersIntent(),
            { Id: "CodeAlta.Workspace.Settings" } => new OpenWorkspaceSettingsIntent(),
            { Id: "CodeAlta.Shell.FocusSidebar" } => new FocusSidebarIntent(),
            { Id: "CodeAlta.Shell.FocusPrompt" } => new FocusPromptIntent(),
            { Id: "CodeAlta.Shell.FocusModelProvider" } => new FocusModelProviderIntent(),
            { Id: "CodeAlta.Session.SessionUsage" } => new OpenSessionUsageIntent(),
            { Id: "CodeAlta.Session.Info" } => new OpenSessionInfoIntent(),
            { Id: "CodeAlta.Session.ExpandPrompt" } => new OpenExpandedPromptIntent(),
            { Id: "CodeAlta.Session.Send" } => new SendPromptIntent(arguments ?? string.Empty),
            { Id: "CodeAlta.Session.Steer" } => new SteerPromptIntent(arguments ?? string.Empty),
            { Id: "CodeAlta.Session.Abort" } => new AbortSessionIntent(),
            { Id: "CodeAlta.Session.Compact" } => new CompactSessionIntent(),
            { Id: "CodeAlta.Session.CloseTab" } => new CloseTabIntent(),
            { Id: "CodeAlta.Session.TabLeft" } => new TabLeftIntent(),
            { Id: "CodeAlta.Session.TabRight" } => new TabRightIntent(),
            { Id: "CodeAlta.Session.MessagePrevious" } => new MessagePreviousIntent(),
            { Id: "CodeAlta.Session.MessageNext" } => new MessageNextIntent(),
            { Id: "CodeAlta.Session.MessageFirst" } => new MessageFirstIntent(),
            { Id: "CodeAlta.Session.MessageLast" } => new MessageLastIntent(),
            { Id: "CodeAlta.Session.ClearQueue" } => new ClearQueueIntent(),
            _ => new UnknownTextCommandIntent(commandName, arguments)
        };
    }
}
