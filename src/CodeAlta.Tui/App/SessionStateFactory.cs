using CodeAlta.Agent;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.App;

internal interface ISessionTimelineSurface
{
    Rectangle? GetTimelineBounds();
}

internal interface ISessionPromptDraftService
{
    string? LoadPromptDraft(string sessionId);

    void DeletePromptDraft(string sessionId);
}

internal interface ISessionModelProviderPreferenceService
{
    void ApplySessionPreference(OpenSessionState session);

    void RememberSessionPreference(string sessionId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow);
}

internal interface ISessionProjectRootResolver
{
    ProjectDescriptor? GetSelectedProject();

    ProjectDescriptor? GetProjectById(string? projectId);

    string? ResolveProjectRoot(SessionViewDescriptor session);
}

internal sealed class SessionTimelineSurface : ISessionTimelineSurface
{
    private readonly Func<Rectangle?> _getTimelineBounds;

    public SessionTimelineSurface(Func<Rectangle?> getTimelineBounds)
    {
        ArgumentNullException.ThrowIfNull(getTimelineBounds);
        _getTimelineBounds = getTimelineBounds;
    }

    public Rectangle? GetTimelineBounds()
        => _getTimelineBounds();
}

internal sealed class SessionPromptDraftService : ISessionPromptDraftService
{
    private readonly Func<string, string?> _loadPromptDraft;
    private readonly Action<string> _deletePromptDraft;

    public SessionPromptDraftService(Func<string, string?> loadPromptDraft, Action<string> deletePromptDraft)
    {
        ArgumentNullException.ThrowIfNull(loadPromptDraft);
        ArgumentNullException.ThrowIfNull(deletePromptDraft);
        _loadPromptDraft = loadPromptDraft;
        _deletePromptDraft = deletePromptDraft;
    }

    public string? LoadPromptDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _loadPromptDraft(sessionId);
    }

    public void DeletePromptDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _deletePromptDraft(sessionId);
    }
}

internal sealed class SessionModelProviderPreferenceService : ISessionModelProviderPreferenceService
{
    private readonly Action<OpenSessionState> _applySessionPreference;
    private readonly Action<string, string?, AgentReasoningEffort?, bool> _rememberSessionPreference;

    public SessionModelProviderPreferenceService(
        Action<OpenSessionState> applySessionPreference,
        Action<string, string?, AgentReasoningEffort?, bool> rememberSessionPreference)
    {
        ArgumentNullException.ThrowIfNull(applySessionPreference);
        ArgumentNullException.ThrowIfNull(rememberSessionPreference);
        _applySessionPreference = applySessionPreference;
        _rememberSessionPreference = rememberSessionPreference;
    }

    public void ApplySessionPreference(OpenSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _applySessionPreference(session);
    }

    public void RememberSessionPreference(string sessionId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _rememberSessionPreference(sessionId, modelId, reasoningEffort, persistNow);
    }
}

internal sealed class SessionProjectRootResolver : ISessionProjectRootResolver
{
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<string?, ProjectDescriptor?> _getProjectById;

    public SessionProjectRootResolver(
        Func<ProjectDescriptor?> getSelectedProject,
        Func<string?, ProjectDescriptor?> getProjectById)
    {
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getProjectById);
        _getSelectedProject = getSelectedProject;
        _getProjectById = getProjectById;
    }

    public ProjectDescriptor? GetSelectedProject()
        => _getSelectedProject();

    public ProjectDescriptor? GetProjectById(string? projectId)
        => _getProjectById(projectId);

    public string? ResolveProjectRoot(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return PromptReferenceProjectRootResolver.Resolve(session, GetProjectById, GetSelectedProject);
    }
}

internal sealed class SessionStateFactory
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ISessionTimelineSurface _timelineSurface;
    private readonly ISessionPromptDraftService _promptDrafts;
    private readonly ISessionModelProviderPreferenceService _modelProviderPreferences;
    private readonly ISessionProjectRootResolver _projectRootResolver;

    public SessionStateFactory(
        IUiDispatcher uiDispatcher,
        ISessionTimelineSurface timelineSurface,
        ISessionPromptDraftService promptDrafts,
        ISessionModelProviderPreferenceService modelProviderPreferences,
        ISessionProjectRootResolver projectRootResolver)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(timelineSurface);
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(modelProviderPreferences);
        ArgumentNullException.ThrowIfNull(projectRootResolver);

        _uiDispatcher = uiDispatcher;
        _timelineSurface = timelineSurface;
        _promptDrafts = promptDrafts;
        _modelProviderPreferences = modelProviderPreferences;
        _projectRootResolver = projectRootResolver;
    }

    public OpenSessionState CreateOpenSession(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var timeline = new SessionTimelinePresenter(
            _uiDispatcher,
            _timelineSurface.GetTimelineBounds,
            _projectRootResolver.ResolveProjectRoot(session));
        var state = new OpenSessionState(session, timeline)
        {
            ProviderId = new ModelProviderId(session.ResolvedProviderKey),
            StatusMessage = ShellTextFormatter.BuildReadyStatusText(session, _projectRootResolver.GetSelectedProject(), globalScopeSelected: false),
        };
        state.Session.PromptDraftText = _promptDrafts.LoadPromptDraft(session.SessionId) ?? string.Empty;
        state.ViewModel.Title = session.Title;

        _modelProviderPreferences.ApplySessionPreference(state);
        _modelProviderPreferences.RememberSessionPreference(session.SessionId, state.ModelId, state.ReasoningEffort, false);

        return state;
    }

    public void UpdateOpenSession(OpenSessionState state, SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(session);

        state.SessionView = session;
        state.Timeline.SetLocalFileRootPath(_projectRootResolver.ResolveProjectRoot(session));
        state.ViewModel.SessionId = session.SessionId;
        state.ViewModel.Title = session.Title;
    }
}
