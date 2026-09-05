using CodeAlta.Tui.Presentation.Sidebar;

namespace CodeAlta.Tui.App;

internal sealed class SidebarRowCommandDispatcher : ISidebarRowCommandDispatcher
{
    private readonly NavigatorActionCoordinator _navigatorActions;

    public SidebarRowCommandDispatcher(NavigatorActionCoordinator navigatorActions)
    {
        ArgumentNullException.ThrowIfNull(navigatorActions);
        _navigatorActions = navigatorActions;
    }

    public void Dispatch(SidebarRowCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        switch (command)
        {
            case SidebarRowCommand.DeleteSession deleteSession:
                _navigatorActions.ConfirmDeleteSession(deleteSession.SessionId);
                break;
            case SidebarRowCommand.DeleteProject deleteProject:
                _navigatorActions.ConfirmDeleteProject(deleteProject.ProjectId);
                break;
            case SidebarRowCommand.OpenProjectSessions openProjectSessions:
                _navigatorActions.OpenProjectSessions(openProjectSessions.ProjectId);
                break;
            case SidebarRowCommand.OpenProjectDetails openProjectDetails:
                _navigatorActions.OpenProjectDetails(openProjectDetails.ProjectId);
                break;
            case SidebarRowCommand.OpenFolder:
                _navigatorActions.OpenFolder();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown sidebar row command.");
        }
    }
}
