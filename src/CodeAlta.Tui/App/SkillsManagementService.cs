using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Tui.App;

// Selection/scope adapter only. Capture on the UI thread before dispatching a worker.
internal sealed class SkillsManagementService
{
    private readonly SkillManagementService _backend;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;

    public SkillsManagementService(SkillManagementService backend, Func<ProjectDescriptor?> getSelectedProject)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        _backend = backend;
        _getSelectedProject = getSelectedProject;
    }

    public bool HasSelectedProject => !string.IsNullOrWhiteSpace(_getSelectedProject()?.ProjectPath);

    public SkillsManagementService CaptureContext()
    {
        var path = _getSelectedProject()?.ProjectPath;
        var project = path is null ? null : new ProjectDescriptor { ProjectPath = path };
        return new SkillsManagementService(_backend, () => project);
    }

    public Task<IReadOnlyList<SkillDescriptor>> LoadAsync(SkillsManagementScope scope, CancellationToken cancellationToken = default)
        => _backend.LoadAsync(scope switch
        {
            SkillsManagementScope.Combined => SkillListingScope.Combined,
            SkillsManagementScope.CurrentProject => SkillListingScope.Project,
            SkillsManagementScope.User => SkillListingScope.User,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        }, _getSelectedProject()?.ProjectPath, cancellationToken);

    public Task<SkillCreationResult> CreateSkillAsync(SkillsManagementScope scope, string? name, string? description, CancellationToken cancellationToken = default)
    {
        var projectRoot = _getSelectedProject()?.ProjectPath;
        var target = scope switch
        {
            SkillsManagementScope.CurrentProject => SkillCreationTargetKind.ProjectCodeAlta,
            SkillsManagementScope.Combined when !string.IsNullOrWhiteSpace(projectRoot) => SkillCreationTargetKind.ProjectCodeAlta,
            SkillsManagementScope.Combined or SkillsManagementScope.User => SkillCreationTargetKind.UserCodeAlta,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return _backend.CreateSkillAsync(target, projectRoot, name, description, cancellationToken);
    }

    public SkillEnablementUpdateResult SetSkillEnabled(SkillEnablementScope scope, string name, bool enabled)
        => _backend.SetSkillEnabled(scope, _getSelectedProject()?.ProjectPath, name, enabled);

    public SkillEnablementUpdateResult SetSkillsEnabled(SkillEnablementScope scope, IReadOnlyList<string> names, bool enabled)
        => _backend.SetSkillsEnabled(scope, _getSelectedProject()?.ProjectPath, names, enabled);

    public SkillEnablementUpdateResult InvertSkillsEnabled(SkillEnablementScope scope, IReadOnlyList<string> names)
        => _backend.InvertSkillsEnabled(scope, _getSelectedProject()?.ProjectPath, names);

    public IReadOnlyList<SkillRelatedFile> ListRelatedFiles(SkillDescriptor descriptor, CancellationToken cancellationToken = default)
        => _backend.ListRelatedFiles(descriptor, cancellationToken);
}

internal enum SkillsManagementScope
{
    Combined,
    CurrentProject,
    User,
}
