using CodeAlta.Agent;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal sealed class SessionExecutionOptionsFactory
{
    private readonly CatalogOptions _catalogOptions;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly SessionPermissionRequestCoordinator _permissionRequests;
    private readonly SessionUserInputRequestCoordinator _userInputRequests;
    private readonly Func<string?>? _preferredAgentPromptProvider;
    private readonly IServiceProvider? _altaServices;

    public SessionExecutionOptionsFactory(
        CatalogOptions catalogOptions,
        Dictionary<string, ModelProviderState> modelProviderStates,
        SessionSelectionContext sessionSelection,
        SessionPermissionRequestCoordinator permissionRequests,
        SessionUserInputRequestCoordinator userInputRequests,
        Func<string?>? preferredAgentPromptProvider = null,
        IServiceProvider? altaServices = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(permissionRequests);
        ArgumentNullException.ThrowIfNull(userInputRequests);

        _catalogOptions = catalogOptions;
        _modelProviderStates = modelProviderStates;
        _sessionSelection = sessionSelection;
        _permissionRequests = permissionRequests;
        _userInputRequests = userInputRequests;
        _preferredAgentPromptProvider = preferredAgentPromptProvider;
        _altaServices = altaServices;
    }

    public SessionExecutionOptions BuildPreferredExecutionOptions(
        ModelProviderId providerId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceSessionIdProvider = null)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        _modelProviderStates.TryGetValue(providerId.Value, out var providerState);
        var model = providerState?.SelectedModelId;
        var reasoning = providerState?.SelectedReasoningEffort;

        var sourceProjectId = projectRoots.Count == 0
            ? null
            : _sessionSelection.GetSelectedProjectId();
        return new SessionExecutionOptions
        {
            ProviderId = providerId,
            ProviderKey = providerId.Value,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = model,
            ReasoningEffort = reasoning,
            AgentPromptId = NormalizeOptionalText(_preferredAgentPromptProvider?.Invoke()),
            Tools = CreateAltaTools(
                sourceSessionIdProvider: sourceSessionIdProvider,
                sourceProjectIdProvider: () => sourceProjectId,
                workingDirectoryProvider: () => workingDirectory),
            OnPermissionRequest = CreatePermissionHandler(CreateTransientSessionKey(providerId, workingDirectory)),
            OnUserInputRequest = (request, cancellationToken) => _userInputRequests.HandleAsync(CreateTransientSessionKey(providerId, workingDirectory), request, cancellationToken),
        };
    }

    public SessionExecutionOptions BuildExecutionOptions(SessionViewDescriptor session, OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);

        var workingDirectory = ResolveWorkingDirectory(session);
        var projectRoots = ResolveProjectRoots(session);
        var providerKey = tab.ProviderId.IsEmpty
            ? session.ResolvedProviderKey
            : tab.ProviderId.Value;
        var providerId = new ModelProviderId(providerKey);
        return new SessionExecutionOptions
        {
            ProviderId = providerId,
            ProviderKey = providerKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = tab.ModelId,
            ReasoningEffort = tab.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(tab.AgentPromptId ?? session.AgentPromptId),
            Tools = CreateAltaTools(
                sourceSessionIdProvider: () => session.SessionId,
                sourceProjectIdProvider: () => session.ProjectRef,
                workingDirectoryProvider: () => ResolveWorkingDirectory(session)),
            OnPermissionRequest = CreatePermissionHandler(session.SessionId),
            OnUserInputRequest = (request, cancellationToken) => _userInputRequests.HandleAsync(session.SessionId, request, cancellationToken),
        };
    }

    public static string CreateTransientSessionKey(ModelProviderId providerId, string workingDirectory)
        => $"{providerId.Value}:{workingDirectory}";

    private AgentPermissionRequestHandler CreatePermissionHandler(string fallbackSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSessionId);

        return (request, cancellationToken) => _permissionRequests.HandleAsync(
            string.IsNullOrWhiteSpace(request.SessionId) ? fallbackSessionId : request.SessionId,
            request,
            cancellationToken);
    }

    private IReadOnlyList<AgentToolDefinition>? CreateAltaTools(
        Func<string?>? sourceSessionIdProvider,
        Func<string?>? sourceProjectIdProvider,
        Func<string?>? workingDirectoryProvider)
    {
        if (_altaServices is null)
        {
            return null;
        }

        var dispatcher = _altaServices.GetService(typeof(AltaCommandDispatcher)) as AltaCommandDispatcher
            ?? new AltaCommandDispatcher(new AltaCommandRegistry(), _altaServices);
        return
        [
            AltaSessionToolFactory.Create(
                dispatcher,
                new AltaSessionToolOptions
                {
                    SourceSessionIdProvider = sourceSessionIdProvider,
                    SourceProjectIdProvider = sourceProjectIdProvider,
                    WorkingDirectoryProvider = workingDirectoryProvider,
                    DefaultMaxOutputRecords = 200,
                    DefaultMaxOutputBytes = 64 * 1024,
                    DefaultTimeout = TimeSpan.FromSeconds(120),
                }),
        ];
    }

    private string ResolveWorkingDirectory(SessionViewDescriptor session)
    {
        return session.Kind switch
        {
            SessionViewKind.GlobalSession => _catalogOptions.GlobalRoot,
            SessionViewKind.ProjectSession when _sessionSelection.GetProjectById(session.ProjectRef) is { } project => project.ProjectPath,
            _ => session.WorkingDirectory,
        };
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IReadOnlyList<string> ResolveProjectRoots(SessionViewDescriptor session)
    {
        if (_sessionSelection.GetProjectById(session.ProjectRef) is { } project)
        {
            return [project.ProjectPath];
        }

        return [];
    }
}
