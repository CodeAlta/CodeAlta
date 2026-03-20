using System.Text;
using CodeAlta.Presentation.Styling;

namespace CodeAlta.Presentation.Sidebar
{
    internal sealed record SidebarTreeProjection(IReadOnlyList<SidebarTreeNodeProjection> Roots)
    {
        public bool ContainsTarget(SidebarSelectionTarget target)
        {
            foreach (var root in Roots)
            {
                if (root.ContainsTarget(target))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed record SidebarTreeNodeProjection(
        string Title,
        string? Tooltip,
        Rune Icon,
        SidebarAccent Accent,
        SidebarSelectionTarget? SelectionTarget,
        bool IsExpanded,
        IReadOnlyList<SidebarTreeNodeProjection> Children)
    {
        public bool ContainsTarget(SidebarSelectionTarget target)
        {
            if (SelectionTarget is { } selectionTarget && selectionTarget == target)
            {
                return true;
            }

            foreach (var child in Children)
            {
                if (child.ContainsTarget(target))
                {
                    return true;
                }
            }

            return false;
        }
    }
}