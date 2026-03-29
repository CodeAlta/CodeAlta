using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using System.Text;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class SidebarView
{
    private static readonly ButtonStyle ToolbarButtonStyle = ButtonStyle.Default with
    {
        Padding = Thickness.Zero,
    };

    private readonly Dictionary<SidebarSelectionTarget, TreeNode> _nodesByTarget = new();

    public SidebarView(
        SidebarViewModel viewModel,
        Action refreshCatalog,
        Action cycleSortMode,
        Action openNavigatorSettings,
        Action<SidebarSelectionTarget?> onSelectedTargetChanged)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(refreshCatalog);
        ArgumentNullException.ThrowIfNull(cycleSortMode);
        ArgumentNullException.ThrowIfNull(openNavigatorSettings);
        ArgumentNullException.ThrowIfNull(onSelectedTargetChanged);

        Tree = new TreeView
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var treeHost = new ScrollViewer(Tree)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true);

        var footer = new HStack(
        [
            CreateToolbarButton(
                () => NerdFont.MdRefresh,
                "Refresh projects and threads",
                refreshCatalog),
            CreateToolbarButton(
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? NerdFont.MdSortAlphabeticalAscending
                    : NerdFont.MdSortCalendarDescending,
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? "Sort projects by name"
                    : "Sort projects by last activity",
                cycleSortMode),
            CreateToolbarButton(
                () => NerdFont.MdCogOutline,
                "Navigator settings",
                openNavigatorSettings),
        ])
        {
            Spacing = 2,
            HorizontalAlignment = Align.Stretch,
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

        var group = new Group(
            new Markup($"[bold]{AnsiMarkup.Escape($"{NerdFont.FaFolderTree} Navigator")}[/]"),
            contentGrid)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        Root = new ZStack(
            group,
            new BindableObserver<SidebarSelectionTarget?>(
                () => SelectedTarget,
                onSelectedTargetChanged));
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

        var node = new TreeNode(CreateSidebarHeader(projection.Row))
        {
            Icon = projection.Icon,
            IconStyle = UiPalette.GetSidebarIconStyle(projection.Accent),
            Data = projection.SelectionTarget,
            IsExpanded = projection.IsExpanded,
        };

        if (projection.Kind is SidebarNodeKind.Global or SidebarNodeKind.Project or SidebarNodeKind.Thread)
        {
            node.AddRightVisual(CreateTimestampVisual(projection.Row), TreeNodeRightVisualVisibility.Always);
        }

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

    private static Visual CreateSidebarHeader(SidebarNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new Markup(() => $"[bold]{AnsiMarkup.Escape(row.Title)}[/]")
        {
            Wrap = false,
        };
    }

    private static Visual CreateTimestampVisual(SidebarNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new Markup(() =>
                $"[{UiPalette.MutedMarkup}]{AnsiMarkup.Escape(row.RelativeActivityText)}[/]")
            .Wrap(false)
            .TextAlignment(TextAlignment.Right)
            .MinWidth(12)
            .Tooltip(new TextBlock(() => row.ExactActivityText));
    }

    private static Visual CreateToolbarButton(
        Func<Rune> iconFactory,
        string tooltip,
        Action onClick)
    {
        ArgumentNullException.ThrowIfNull(iconFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltip);
        ArgumentNullException.ThrowIfNull(onClick);

        return new Button(new TextBlock(() => iconFactory().ToString()))
            .Style(ToolbarButtonStyle)
            .Click(onClick)
            .Tooltip(new TextBlock(tooltip));
    }

    private static Visual CreateToolbarButton(
        Func<Rune> iconFactory,
        Func<string> tooltipFactory,
        Action onClick)
    {
        ArgumentNullException.ThrowIfNull(iconFactory);
        ArgumentNullException.ThrowIfNull(tooltipFactory);
        ArgumentNullException.ThrowIfNull(onClick);

        return new Button(new TextBlock(() => iconFactory().ToString()))
            .Style(ToolbarButtonStyle)
            .Click(onClick)
            .Tooltip(new TextBlock(tooltipFactory));
    }
}
