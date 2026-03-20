using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.ViewModels;

namespace CodeAlta.Views;

internal sealed class SidebarCoordinator
{
    private readonly SidebarViewModel _viewModel;
    private readonly CatalogOptions _catalogOptions;
    private readonly int _maxRecentThreadsPerProject;
    private readonly SidebarView _view;
    private bool _selectionSyncEnabled = true;
    private SidebarTreeProjection? _projection;
    private SidebarSelectionTarget? _pendingSelectionTarget;
    private SidebarSelectionTarget? _lastSelectedTarget;

    public SidebarCoordinator(
        SidebarViewModel viewModel,
        CatalogOptions catalogOptions,
        CodeAltaShellController shellController,
        int maxRecentThreadsPerProject)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(shellController);

        _viewModel = viewModel;
        _catalogOptions = catalogOptions;
        _maxRecentThreadsPerProject = maxRecentThreadsPerProject;
        _view = new SidebarView(
            viewModel,
            () => _ = shellController.ReloadCatalogAsync(CancellationToken.None));
    }

    public SidebarView View => _view;

    public void RefreshProjection(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? expandedProjectId,
        SidebarSelectionTarget currentTarget,
        Action verifyBindableAccess)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);

        verifyBindableAccess();

        var projection = SidebarTreeProjectionBuilder.Build(
            projects,
            threads,
            _catalogOptions.GlobalRoot,
            expandedProjectId,
            _maxRecentThreadsPerProject);

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

    public void SyncSelection(
        Action selectGlobalScope,
        Action<string> selectProjectScope,
        Action<string> openThread)
    {
        ArgumentNullException.ThrowIfNull(selectGlobalScope);
        ArgumentNullException.ThrowIfNull(selectProjectScope);
        ArgumentNullException.ThrowIfNull(openThread);

        if (!_selectionSyncEnabled || _pendingSelectionTarget is not null)
        {
            return;
        }

        if (_view.SelectedTarget is not { } target || target == _lastSelectedTarget)
        {
            return;
        }

        _lastSelectedTarget = target;
        switch (target.Kind)
        {
            case SidebarSelectionKind.GlobalScope:
                selectGlobalScope();
                break;
            case SidebarSelectionKind.ProjectScope when target.ProjectId is not null:
                selectProjectScope(target.ProjectId);
                break;
            case SidebarSelectionKind.Thread when target.ThreadId is not null:
                openThread(target.ThreadId);
                break;
        }
    }
}