namespace CodeAlta.Tui.Presentation.Sidebar;

internal abstract record SidebarRowCommand
{
    private SidebarRowCommand()
    {
    }

    public sealed record DeleteSession(string SessionId) : SidebarRowCommand;

    public sealed record DeleteProject(string ProjectId) : SidebarRowCommand;

    public sealed record OpenProjectSessions(string ProjectId) : SidebarRowCommand;

    public sealed record OpenProjectDetails(string ProjectId) : SidebarRowCommand;

    public sealed record OpenFolder : SidebarRowCommand;
}

internal interface ISidebarRowCommandDispatcher
{
    void Dispatch(SidebarRowCommand command);
}
