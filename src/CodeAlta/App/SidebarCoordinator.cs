using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.ViewModels;
using CodeAlta.Views;

namespace CodeAlta.App;

internal sealed class SidebarCoordinator
{
    private readonly SidebarViewModel _viewModel;
    private readonly CatalogOptions _catalogOptions;
    private readonly CodeAltaShellController _shellController;
    private readonly Action _openNavigatorSettings;
    private readonly Func<string, string, Task> _renameProjectDisplayNameAsync;
    private readonly SidebarView _view;
    private readonly Dictionary<string, SidebarNodeViewModel> _rowsById = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectionSyncEnabled = true;
    private SidebarTreeProjection? _projection;
    private SidebarSelectionTarget? _pendingSelectionTarget;
    private SidebarSelectionTarget? _lastSelectedTarget;
    private DateTimeOffset? _nextRecencyRefreshAtUtc;

    public SidebarCoordinator(
        SidebarViewModel viewModel,
        CatalogOptions catalogOptions,
        CodeAltaShellController shellController,
        Action cycleSortMode,
        Action openNavigatorSettings,
        Func<string, string, Task> renameProjectDisplayNameAsync,
        ISidebarRowCommandDispatcher rowCommandDispatcher,
        Action openLogs)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(shellController);
        ArgumentNullException.ThrowIfNull(cycleSortMode);
        ArgumentNullException.ThrowIfNull(openNavigatorSettings);
        ArgumentNullException.ThrowIfNull(renameProjectDisplayNameAsync);
        ArgumentNullException.ThrowIfNull(rowCommandDispatcher);
        ArgumentNullException.ThrowIfNull(openLogs);

        _viewModel = viewModel;
        _catalogOptions = catalogOptions;
        _shellController = shellController;
        _openNavigatorSettings = openNavigatorSettings;
        _renameProjectDisplayNameAsync = renameProjectDisplayNameAsync;
        _view = new SidebarView(
            viewModel,
            () => _ = shellController.ReloadCatalogAsync(CancellationToken.None),
            cycleSortMode,
            openNavigatorSettings,
            BeginInlineRenameSelectedProject,
            SubmitInlineRename,
            CancelInlineRename,
            rowCommandDispatcher,
            OnSelectedTargetChanged,
            openLogs);
    }

    public SidebarView View => _view;

    public void OpenNavigatorSettings() => _openNavigatorSettings();

    public void RefreshProjection(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? preferredExpandedProjectId,
        SidebarSelectionTarget currentTarget,
        NavigatorSettings settings,
        Func<string, ThreadVisualState> getThreadVisualState,
        Func<string?, bool, bool> hasDraftPrompt,
        Action verifyBindableAccess)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getThreadVisualState);
        ArgumentNullException.ThrowIfNull(hasDraftPrompt);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);

        verifyBindableAccess();

        var nowUtc = DateTimeOffset.UtcNow;
        _viewModel.SortMode = settings.SortMode;
        var expandedProjectIds = new HashSet<string>(_view.GetExpandedProjectIds(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferredExpandedProjectId))
        {
            expandedProjectIds.Add(preferredExpandedProjectId);
        }

        var projection = SidebarTreeProjectionBuilder.Build(
            projects,
            threads,
            _catalogOptions.GlobalRoot,
            expandedProjectIds,
            settings,
            getThreadVisualState,
            hasDraftPrompt,
            GetOrCreateRow,
            nowUtc);
        UpdateNextRecencyRefresh(nowUtc);

        if (_projection == projection)
        {
            SyncSelectionToCurrentState(currentTarget);
            return;
        }

        _projection = projection;
        _viewModel.Projection = projection;
        _selectionSyncEnabled = false;
        try
        {
            _view.ApplyProjection(projection);
            _pendingSelectionTarget = SidebarSelectionResolver.ResolveTargetForProjectionChange(
                _lastSelectedTarget,
                _projection,
                currentTarget);
        }
        finally
        {
            _selectionSyncEnabled = true;
        }

        PruneStaleRows(projection);
        ApplyPendingSelection();
    }

    public void RefreshRecency(DateTimeOffset nowUtc, Action verifyBindableAccess)
    {
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        verifyBindableAccess();

        if (_nextRecencyRefreshAtUtc is { } nextRefreshAtUtc &&
            nowUtc < nextRefreshAtUtc)
        {
            return;
        }

        foreach (var row in _rowsById.Values)
        {
            row.UpdateActivity(row.ActivityAtUtc, nowUtc);
        }

        UpdateNextRecencyRefresh(nowUtc);
    }

    public void SyncSelectionToCurrentState(SidebarSelectionTarget currentTarget)
    {
        _pendingSelectionTarget = currentTarget;
        ApplyPendingSelection();
    }

    public void ApplyPendingSelection()
    {
        if (_pendingSelectionTarget is not { } target)
        {
            return;
        }

        if (_projection is null || !_projection.ContainsTarget(target))
        {
            _pendingSelectionTarget = null;
            return;
        }

        if (!_view.TrySelectTarget(target))
        {
            return;
        }

        _lastSelectedTarget = target;
        _pendingSelectionTarget = null;
    }

    public void BeginInlineRenameSelectedProject()
    {
        if (_view.SelectedTarget is not { Kind: SidebarSelectionKind.ProjectScope, ProjectId: { } projectId })
        {
            return;
        }

        foreach (var row in _rowsById.Values)
        {
            if (row.Kind == SidebarNodeKind.Project &&
                !string.Equals(row.SelectionTarget?.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
                row.IsInlineEditing)
            {
                row.CancelInlineEdit();
            }
        }

        if (TryGetProjectRow(projectId, out var projectRow))
        {
            projectRow.BeginInlineEdit();
            _view.FocusInlineRenameEditor(projectRow.NodeId);
        }
    }

    private SidebarNodeViewModel GetOrCreateRow(
        string nodeId,
        SidebarNodeKind kind,
        SidebarSelectionTarget? selectionTarget)
    {
        if (_rowsById.TryGetValue(nodeId, out var existing))
        {
            return existing;
        }

        var created = new SidebarNodeViewModel(nodeId, kind, selectionTarget);
        _rowsById.Add(nodeId, created);
        return created;
    }

    private void UpdateNextRecencyRefresh(DateTimeOffset nowUtc)
    {
        DateTimeOffset? nextRefreshAtUtc = null;
        foreach (var row in _rowsById.Values)
        {
            if (row.NextRelativeRefreshAtUtc is not { } candidate || candidate <= nowUtc)
            {
                continue;
            }

            if (nextRefreshAtUtc is null || candidate < nextRefreshAtUtc.Value)
            {
                nextRefreshAtUtc = candidate;
            }
        }

        _nextRecencyRefreshAtUtc = nextRefreshAtUtc;
    }

    private void PruneStaleRows(SidebarTreeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in projection.Roots)
        {
            CollectNodeIds(root, activeIds);
        }

        foreach (var nodeId in _rowsById.Keys.ToArray())
        {
            if (!activeIds.Contains(nodeId))
            {
                _rowsById.Remove(nodeId);
            }
        }
    }

    private static void CollectNodeIds(SidebarTreeNodeProjection node, HashSet<string> ids)
    {
        ids.Add(node.NodeId);
        foreach (var child in node.Children)
        {
            CollectNodeIds(child, ids);
        }
    }

    private void OnSelectedTargetChanged(SidebarSelectionTarget? target)
    {
        if (!_selectionSyncEnabled || _pendingSelectionTarget is not null)
        {
            return;
        }

        if (target is not { } currentTarget || currentTarget == _lastSelectedTarget)
        {
            return;
        }

        _lastSelectedTarget = currentTarget;
        switch (currentTarget.Kind)
        {
            case SidebarSelectionKind.GlobalScope:
                _ = _shellController.SelectGlobalScopeAsync(CancellationToken.None);
                break;
            case SidebarSelectionKind.ProjectScope when currentTarget.ProjectId is not null:
                _ = _shellController.SelectProjectScopeAsync(currentTarget.ProjectId, CancellationToken.None);
                break;
            case SidebarSelectionKind.Thread when currentTarget.ThreadId is not null:
                _ = _shellController.OpenThreadAsync(currentTarget.ThreadId, CancellationToken.None);
                break;
        }
    }

    private void SubmitInlineRename(SidebarNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Kind != SidebarNodeKind.Project ||
            row.SelectionTarget?.ProjectId is not { } projectId ||
            !row.TryGetInlineEditValue(out var displayName))
        {
            return;
        }

        var previousTitle = row.Title;
        row.UpdateTitle(displayName);
        row.IsInlineEditing = false;
        _ = CommitInlineRenameAsync(row, projectId, displayName, previousTitle);
    }

    private void CancelInlineRename(SidebarNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.CancelInlineEdit();
        _view.Tree.App?.Focus(_view.Tree);
    }

    private async Task CommitInlineRenameAsync(
        SidebarNodeViewModel row,
        string projectId,
        string displayName,
        string previousTitle)
    {
        try
        {
            await _renameProjectDisplayNameAsync(projectId, displayName);
        }
        catch
        {
            row.UpdateTitle(previousTitle);
        }
    }

    private bool TryGetProjectRow(string projectId, out SidebarNodeViewModel row)
    {
        foreach (var candidate in _rowsById.Values)
        {
            if (candidate.Kind == SidebarNodeKind.Project &&
                string.Equals(candidate.SelectionTarget?.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                row = candidate;
                return true;
            }
        }

        row = null!;
        return false;
    }
}
