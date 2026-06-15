using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;

namespace CodeAlta.App;

internal sealed class SessionCreationCoordinator
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly Func<ModelProviderId> _getPreferredProviderId;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<ShellSelection> _getSelection;
    private readonly Func<string?> _readDraftTitle;
    private readonly Func<ProjectDescriptor, Task<ProjectDescriptor>> _ensureProjectPersistedAsync;
    private readonly Action<ProjectDescriptor> _upsertProject;
    private readonly Func<ModelProviderId, string, IReadOnlyList<string>, Func<string?>?, SessionExecutionOptions> _buildPreferredExecutionOptions;
    private readonly Action<string, string?, AgentReasoningEffort?, bool> _rememberSessionPreference;
    private readonly Func<SessionViewDescriptor, Task> _registerCreatedSessionAsync;
    private readonly Action _clearSessionTitleDraft;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public SessionCreationCoordinator(
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Func<ModelProviderId> getPreferredProviderId,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<ShellSelection> getSelection,
        Func<string?> readDraftTitle,
        Func<ProjectDescriptor, Task<ProjectDescriptor>> ensureProjectPersistedAsync,
        Action<ProjectDescriptor> upsertProject,
        Func<ModelProviderId, string, IReadOnlyList<string>, Func<string?>?, SessionExecutionOptions> buildPreferredExecutionOptions,
        Action<string, string?, AgentReasoningEffort?, bool> rememberSessionPreference,
        Func<SessionViewDescriptor, Task> registerCreatedSessionAsync,
        Action clearSessionTitleDraft,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getPreferredProviderId);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getSelection);
        ArgumentNullException.ThrowIfNull(readDraftTitle);
        ArgumentNullException.ThrowIfNull(ensureProjectPersistedAsync);
        ArgumentNullException.ThrowIfNull(upsertProject);
        ArgumentNullException.ThrowIfNull(buildPreferredExecutionOptions);
        ArgumentNullException.ThrowIfNull(rememberSessionPreference);
        ArgumentNullException.ThrowIfNull(registerCreatedSessionAsync);
        ArgumentNullException.ThrowIfNull(clearSessionTitleDraft);
        ArgumentNullException.ThrowIfNull(setStatus);

        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _getPreferredProviderId = getPreferredProviderId;
        _getSelectedProject = getSelectedProject;
        _getSelection = getSelection;
        _readDraftTitle = readDraftTitle;
        _ensureProjectPersistedAsync = ensureProjectPersistedAsync;
        _upsertProject = upsertProject;
        _buildPreferredExecutionOptions = buildPreferredExecutionOptions;
        _rememberSessionPreference = rememberSessionPreference;
        _registerCreatedSessionAsync = registerCreatedSessionAsync;
        _clearSessionTitleDraft = clearSessionTitleDraft;
        _setStatus = setStatus;
    }

    public async Task<SessionViewDescriptor?> CreateGlobalSessionAsync(string? titleOverride = null)
    {
        try
        {
            _setStatus(SR.T("Creating global session..."), true, StatusTone.Info);
            var title = ResolveTitle(titleOverride);
            string? createdSessionId = null;
            var executionOptions = _buildPreferredExecutionOptions(
                _getPreferredProviderId(),
                _catalogOptions.GlobalRoot,
                [],
                () => createdSessionId);
            var session = await _runtimeService.CreateGlobalSessionAsync(executionOptions, title);
            createdSessionId = session.SessionId;
            _rememberSessionPreference(session.SessionId, executionOptions.Model, executionOptions.ReasoningEffort, false);
            await _registerCreatedSessionAsync(session);
            _clearSessionTitleDraft();
            _setStatus(
                ShellTextFormatter.BuildReadyStatusText(session, _getSelectedProject(), IsGlobalDraftSelected()),
                false,
                StatusTone.Ready);
            return session;
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to create global session: {0}", ex.Message), false, StatusTone.Error);
            return null;
        }
    }

    public async Task<SessionViewDescriptor?> CreateProjectSessionAsync(string? titleOverride = null)
    {
        var project = _getSelectedProject();
        if (project is null)
        {
            _setStatus(SR.T("Select a project before creating a project session."), false, StatusTone.Warning);
            return null;
        }

        try
        {
            _setStatus(SR.T("Creating session for '{0}'...", project.DisplayName), true, StatusTone.Info);
            var title = ResolveTitle(titleOverride);
            string? createdSessionId = null;
            var executionOptions = _buildPreferredExecutionOptions(
                _getPreferredProviderId(),
                project.ProjectPath,
                [project.ProjectPath],
                () => createdSessionId);
            var session = await _runtimeService.CreateProjectSessionAsync(project, executionOptions, title);
            createdSessionId = session.SessionId;
            project = await _ensureProjectPersistedAsync(project);
            _upsertProject(project);
            _rememberSessionPreference(session.SessionId, executionOptions.Model, executionOptions.ReasoningEffort, false);
            await _registerCreatedSessionAsync(session);
            _clearSessionTitleDraft();
            _setStatus(
                ShellTextFormatter.BuildReadyStatusText(session, _getSelectedProject(), IsGlobalDraftSelected()),
                false,
                StatusTone.Ready);
            return session;
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to create project session: {0}", ex.Message), false, StatusTone.Error);
            return null;
        }
    }

    private string? ResolveTitle(string? titleOverride)
    {
        var draftTitle = _readDraftTitle()?.Trim();
        if (!string.IsNullOrWhiteSpace(draftTitle))
        {
            return draftTitle;
        }

        return string.IsNullOrWhiteSpace(titleOverride) ? null : titleOverride.Trim();
    }

    private bool IsGlobalDraftSelected()
        => _getSelection().Target is WorkspaceTarget.Draft { IsGlobal: true };
}
