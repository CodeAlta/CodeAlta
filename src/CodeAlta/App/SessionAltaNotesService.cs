using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Threading;

namespace CodeAlta.App;

internal sealed class SessionAltaNotesService : IAltaNotesService
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SessionRuntimeService _runtimeService;
    private readonly Func<OpenSessionState?> _getSelectedOpenSession;
    private readonly Func<string, OpenSessionState?> _findOpenSession;

    public SessionAltaNotesService(
        IUiDispatcher uiDispatcher,
        SessionRuntimeService runtimeService,
        Func<OpenSessionState?> getSelectedOpenSession,
        Func<string, OpenSessionState?> findOpenSession)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(getSelectedOpenSession);
        ArgumentNullException.ThrowIfNull(findOpenSession);

        _uiDispatcher = uiDispatcher;
        _runtimeService = runtimeService;
        _getSelectedOpenSession = getSelectedOpenSession;
        _findOpenSession = findOpenSession;
    }

    public event EventHandler<AltaNotesChangedEventArgs>? Changed;

    public string GetMarkdown(AltaCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return UiDispatch.Invoke(_uiDispatcher, () => ResolveOpenSession(caller).NotesMarkdown);
    }

    public async ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var target = UiDispatch.Invoke(_uiDispatcher, () => ResolveOpenSession(caller));
        var notesEvent = CreateNotesEvent(target, AgentNotesUpdateKind.Set, markdown);
        await _runtimeService.AppendSessionEventAsync(target.SessionView, notesEvent, cancellationToken);
        UiDispatch.Invoke(_uiDispatcher, () => target.NotesMarkdown = markdown);
        Changed?.Invoke(this, new AltaNotesChangedEventArgs(target.SessionView.SessionId, markdown, caller));
    }

    public async ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var target = UiDispatch.Invoke(_uiDispatcher, () => ResolveOpenSession(caller));
        var notesEvent = CreateNotesEvent(target, AgentNotesUpdateKind.Cleared, string.Empty);
        await _runtimeService.AppendSessionEventAsync(target.SessionView, notesEvent, cancellationToken);
        UiDispatch.Invoke(_uiDispatcher, () => target.NotesMarkdown = string.Empty);
        Changed?.Invoke(this, new AltaNotesChangedEventArgs(target.SessionView.SessionId, string.Empty, caller));
    }

    private OpenSessionState ResolveOpenSession(AltaCallerIdentity caller)
    {
        if (!string.IsNullOrWhiteSpace(caller.SourceSessionId))
        {
            return _findOpenSession(caller.SourceSessionId) ?? throw new AltaNotesSessionRequiredException();
        }

        return _getSelectedOpenSession() ?? throw new AltaNotesSessionRequiredException();
    }

    private static AgentNotesEvent CreateNotesEvent(OpenSessionState target, AgentNotesUpdateKind kind, string markdown)
    {
        var session = target.SessionView;
        var providerId = !string.IsNullOrWhiteSpace(target.ProviderId.Value)
            ? target.ProviderId
            : new ModelProviderId(session.ResolvedProviderKey);
        return new AgentNotesEvent(
            providerId,
            session.SessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            kind,
            markdown);
    }
}
