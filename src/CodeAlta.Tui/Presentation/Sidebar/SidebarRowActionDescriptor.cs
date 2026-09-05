using System.Text;

namespace CodeAlta.Tui.Presentation.Sidebar;

internal enum SidebarRowActionVisibility
{
    Hover = 0,
    Always = 1,
}

internal enum SidebarRowActionKind
{
    DeleteSession = 0,
    DeleteProject = 1,
    OpenProjectSessions = 2,
    OpenProjectDetails = 3,
    OpenFolder = 4,
}

internal readonly record struct SidebarRowActionDescriptor(
    SidebarRowActionKind Kind,
    Rune Icon,
    string Tooltip,
    SidebarRowActionVisibility Visibility = SidebarRowActionVisibility.Hover);
