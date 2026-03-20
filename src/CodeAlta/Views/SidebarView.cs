using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class SidebarView
{
    private readonly Dictionary<SidebarSelectionTarget, TreeNode> _nodesByTarget = new();

    public SidebarView(
        SidebarViewModel viewModel,
        Action refreshCatalog)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(refreshCatalog);

        Tree = new TreeView
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var treeHost = new ScrollViewer(Tree)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true);

        var footer = new VStack(
        [
            new TextBlock("Thread Title (optional)"),
            new TextBox().Text(viewModel.Bind.DraftThreadTitle),
            new Button(new TextBlock("Refresh Catalog")).Click(refreshCatalog),
        ])
        {
            Spacing = 1,
        };

        var contentGrid = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        contentGrid.Cell(treeHost, 0, 0);
        contentGrid.Cell(footer, 1, 0);

        Root = new Group(
            new Markup($"[bold]{AnsiMarkup.Escape($"{NerdFont.FaFolderTree} Navigator")}[/]"),
            contentGrid)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    public Visual Root { get; }

    public TreeView Tree { get; }

    public SidebarSelectionTarget? SelectedTarget
        => Tree.SelectedNode?.Data is SidebarSelectionTarget target ? target : null;

    public void ApplyProjection(SidebarTreeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        _nodesByTarget.Clear();
        Tree.Roots.Clear();
        foreach (var root in projection.Roots)
        {
            Tree.Roots.Add(CreateNode(root));
        }
    }

    public bool TrySelectTarget(SidebarSelectionTarget target)
    {
        return _nodesByTarget.TryGetValue(target, out var node) &&
               Tree.TrySelectNode(node);
    }

    private TreeNode CreateNode(SidebarTreeNodeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var node = new TreeNode(CreateSidebarHeader(projection.Title, projection.Tooltip))
        {
            Icon = projection.Icon,
            IconStyle = UiPalette.GetSidebarIconStyle(projection.Accent),
            Data = projection.SelectionTarget,
            IsExpanded = projection.IsExpanded,
        };

        if (projection.SelectionTarget is { } target)
        {
            _nodesByTarget[target] = node;
        }

        foreach (var child in projection.Children)
        {
            node.Children.Add(CreateNode(child));
        }

        return node;
    }

    private static Visual CreateSidebarHeader(string title, string? tooltip)
    {
        var markup = new Markup($"[bold]{AnsiMarkup.Escape(title)}[/]")
        {
            Wrap = false,
        };

        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return markup;
        }

        return markup.Tooltip(tooltip);
    }
}