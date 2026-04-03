using System.Text;

namespace CodeAlta.Presentation.Sidebar;

internal enum SidebarRowActionVisibility
{
    Hover = 0,
    Always = 1,
}

internal enum SidebarRowActionKind
{
    DeleteThread = 0,
    DeleteProject = 1,
    OpenProjectThreads = 2,
    OpenProjectDetails = 3,
    OpenFolder = 4,
}

internal readonly record struct SidebarRowActionDescriptor(
    SidebarRowActionKind Kind,
    Rune Icon,
    string Tooltip,
    SidebarRowActionVisibility Visibility = SidebarRowActionVisibility.Hover);
