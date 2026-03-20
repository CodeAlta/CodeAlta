using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;

namespace CodeAlta.App;

internal sealed class ThreadCreationCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly Func<AgentBackendId> _getPreferredBackendId;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<bool> _getGlobalScopeSelected;
    private readonly Func<string?> _readDraftTitle;
    private readonly Func<AgentBackendId, string, IReadOnlyList<string>, WorkThreadExecutionOptions> _buildPreferredExecutionOptions;
    private readonly Action<string, string?, AgentReasoningEffort?, bool, bool> _rememberThreadPreference;
    private readonly Func<WorkThreadDescriptor, Task> _registerCreatedThreadAsync;
    private readonly Action _clearThreadTitleDraft;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public ThreadCreationCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Func<AgentBackendId> getPreferredBackendId,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<bool> getGlobalScopeSelected,
        Func<string?> readDraftTitle,
        Func<AgentBackendId, string, IReadOnlyList<string>, WorkThreadExecutionOptions> buildPreferredExecutionOptions,
        Action<string, string?, AgentReasoningEffort?, bool, bool> rememberThreadPreference,
        Func<WorkThreadDescriptor, Task> registerCreatedThreadAsync,
        Action clearThreadTitleDraft,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getPreferredBackendId);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getGlobalScopeSelected);
        ArgumentNullException.ThrowIfNull(readDraftTitle);
        ArgumentNullException.ThrowIfNull(buildPreferredExecutionOptions);
        ArgumentNullException.ThrowIfNull(rememberThreadPreference);
        ArgumentNullException.ThrowIfNull(registerCreatedThreadAsync);
        ArgumentNullException.ThrowIfNull(clearThreadTitleDraft);
        ArgumentNullException.ThrowIfNull(setStatus);

        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _getPreferredBackendId = getPreferredBackendId;
        _getSelectedProject = getSelectedProject;
        _getGlobalScopeSelected = getGlobalScopeSelected;
        _readDraftTitle = readDraftTitle;
        _buildPreferredExecutionOptions = buildPreferredExecutionOptions;
        _rememberThreadPreference = rememberThreadPreference;
        _registerCreatedThreadAsync = registerCreatedThreadAsync;
        _clearThreadTitleDraft = clearThreadTitleDraft;
        _setStatus = setStatus;
    }

    public async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync()
    {
        try
        {
            _setStatus("Creating global thread...", true, StatusTone.Info);
            var title = _readDraftTitle()?.Trim();
            var executionOptions = _buildPreferredExecutionOptions(
                _getPreferredBackendId(),
                _catalogOptions.GlobalRoot,
                []);
            var thread = await _runtimeService.CreateGlobalThreadAsync(executionOptions, title).ConfigureAwait(false);
            _rememberThreadPreference(thread.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, true, false);
            await _registerCreatedThreadAsync(thread).ConfigureAwait(false);
            _clearThreadTitleDraft();
            _setStatus(
                ShellTextFormatter.BuildReadyStatusText(thread, _getSelectedProject(), _getGlobalScopeSelected()),
                false,
                StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to create global thread: {ex.Message}", false, StatusTone.Error);
            return null;
        }
    }

    public async Task<WorkThreadDescriptor?> CreateProjectThreadAsync()
    {
        var project = _getSelectedProject();
        if (project is null)
        {
            _setStatus("Select a project before creating a project thread.", false, StatusTone.Warning);
            return null;
        }

        try
        {
            _setStatus($"Creating thread for '{project.DisplayName}'...", true, StatusTone.Info);
            var title = _readDraftTitle()?.Trim();
            var executionOptions = _buildPreferredExecutionOptions(
                _getPreferredBackendId(),
                project.ProjectPath,
                [project.ProjectPath]);
            var thread = await _runtimeService.CreateProjectThreadAsync(project, executionOptions, title).ConfigureAwait(false);
            _rememberThreadPreference(thread.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, true, false);
            await _registerCreatedThreadAsync(thread).ConfigureAwait(false);
            _clearThreadTitleDraft();
            _setStatus(
                ShellTextFormatter.BuildReadyStatusText(thread, _getSelectedProject(), _getGlobalScopeSelected()),
                false,
                StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to create project thread: {ex.Message}", false, StatusTone.Error);
            return null;
        }
    }
}