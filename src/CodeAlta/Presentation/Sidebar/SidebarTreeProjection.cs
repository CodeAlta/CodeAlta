using System.Text;
using CodeAlta.Presentation.Styling;

namespace CodeAlta.Presentation.Sidebar
{
    internal sealed class SidebarTreeProjection : IEquatable<SidebarTreeProjection>
    {
        public SidebarTreeProjection(IReadOnlyList<SidebarTreeNodeProjection> roots)
        {
            ArgumentNullException.ThrowIfNull(roots);
            Roots = roots;
        }

        public IReadOnlyList<SidebarTreeNodeProjection> Roots { get; }

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

        public bool Equals(SidebarTreeProjection? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null || Roots.Count != other.Roots.Count)
            {
                return false;
            }

            for (var index = 0; index < Roots.Count; index++)
            {
                if (!Roots[index].Equals(other.Roots[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
            => obj is SidebarTreeProjection other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var root in Roots)
            {
                hash.Add(root);
            }

            return hash.ToHashCode();
        }
    }

    internal sealed class SidebarTreeNodeProjection : IEquatable<SidebarTreeNodeProjection>
    {
        public SidebarTreeNodeProjection(
            string nodeId,
            SidebarNodeKind kind,
            SidebarNodeViewModel row,
            Rune icon,
            SidebarAccent accent,
            SidebarSelectionTarget? selectionTarget,
            bool isExpanded,
            IReadOnlyList<SidebarTreeNodeProjection> children)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(children);

            NodeId = nodeId;
            Kind = kind;
            Row = row;
            Icon = icon;
            Accent = accent;
            SelectionTarget = selectionTarget;
            IsExpanded = isExpanded;
            Children = children;
        }

        public string NodeId { get; }

        public SidebarNodeKind Kind { get; }

        public SidebarNodeViewModel Row { get; }

        public Rune Icon { get; }

        public SidebarAccent Accent { get; }

        public SidebarSelectionTarget? SelectionTarget { get; }

        public bool IsExpanded { get; }

        public IReadOnlyList<SidebarTreeNodeProjection> Children { get; }

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

        public bool Equals(SidebarTreeNodeProjection? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null ||
                !string.Equals(NodeId, other.NodeId, StringComparison.Ordinal) ||
                Kind != other.Kind ||
                !ReferenceEquals(Row, other.Row) ||
                Icon != other.Icon ||
                Accent != other.Accent ||
                SelectionTarget != other.SelectionTarget ||
                IsExpanded != other.IsExpanded ||
                Children.Count != other.Children.Count)
            {
                return false;
            }

            for (var index = 0; index < Children.Count; index++)
            {
                if (!Children[index].Equals(other.Children[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
            => obj is SidebarTreeNodeProjection other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(NodeId, StringComparer.Ordinal);
            hash.Add(Kind);
            hash.Add(Row);
            hash.Add(Icon);
            hash.Add((int)Accent);
            hash.Add(SelectionTarget);
            hash.Add(IsExpanded);
            foreach (var child in Children)
            {
                hash.Add(child);
            }

            return hash.ToHashCode();
        }
    }
}
