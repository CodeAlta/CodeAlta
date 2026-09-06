using CodeAlta.Agent;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal sealed class SessionExecutionOptionsFactory
{
    private readonly CatalogOptions _catalogOptions;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly Func<string?, ProjectDescriptor?> _getProjectById;
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
        _getProjectById = sessionSelection.GetProjectById;
        _permissionRequests = permissionRequests;
        _userInputRequests = userInputRequests;
        _preferredAgentPromptProvider = preferredAgentPromptProvider;
        _altaServices = altaServices;
    }

    public SessionExecutionOptions BuildPreferredExecutionOptions(
        ModelProviderId providerId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        ProjectDescriptor? project,
        Func<string?>? sourceSessionIdProvider = null)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        _modelProviderStates.TryGetValue(providerId.Value, out var providerState);
        var request = SessionExecutionPolicy.CapturePreferred(
            providerId, workingDirectory, projectRoots, project,
            providerState?.SelectedModelId, providerState?.SelectedReasoningEffort, _preferredAgentPromptProvider?.Invoke());
        return BuildOptions(request, sourceSessionIdProvider);
    }

    public SessionExecutionOptions BuildExecutionOptions(SessionViewDescriptor session, OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);

        var request = SessionExecutionPolicy.CaptureSession(
            session, _getProjectById(session.ProjectRef), _catalogOptions.GlobalRoot,
            tab.ProviderId, tab.ModelId, tab.ReasoningEffort, tab.AgentPromptId);
        return BuildOptions(request, () => request.SessionId);
    }

    private SessionExecutionOptions BuildOptions(SessionExecutionRequest context, Func<string?>? sourceSessionIdProvider)
    {
        var sessionKey = context.SessionId ?? CreateTransientSessionKey(context.ProviderId, context.WorkingDirectory);
        return SessionExecutionPolicy.BuildOptions(
            context,
            CreateAltaTools(sourceSessionIdProvider, () => context.ProjectId, () => context.WorkingDirectory),
            CreatePermissionHandler(sessionKey),
            (request, cancellationToken) => _userInputRequests.HandleAsync(sessionKey, request, cancellationToken));
    }

    public static string CreateTransientSessionKey(ModelProviderId providerId, string workingDirectory)
        => $"{providerId.Value}:{workingDirectory}";

    private AgentPermissionRequestHandler CreatePermissionHandler(string fallbackSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSessionId);

        // Trusted provider callback association, not renderer authorization. Explicit provider identity
        // retains precedence over the captured draft/session fallback, including transient draft keys.
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
}
