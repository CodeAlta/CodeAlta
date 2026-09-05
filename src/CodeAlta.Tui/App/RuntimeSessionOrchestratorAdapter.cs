using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal sealed class RuntimeSessionOrchestratorAdapter : ISessionOrchestrator
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly Func<string, SessionViewDescriptor?> _findSession;

    public RuntimeSessionOrchestratorAdapter(
        SessionRuntimeService runtimeService,
        Func<string, SessionViewDescriptor?> findSession)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(findSession);
        _runtimeService = runtimeService;
        _findSession = findSession;
    }

    public ValueTask<SessionCommandResult> CreateDraftAsync(CreateSessionDraftRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Completed });

    public ValueTask<SessionCommandResult> LaunchSessionAsync(LaunchSessionRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Completed });

    public async ValueTask<SessionCommandResult> SubmitPromptAsync(SubmitSessionPromptRequest request, CancellationToken cancellationToken = default)
    {
        var session = ResolveSession(request.Context);
        var runId = await _runtimeService.SendAsync(
            session,
            request.Context.ExecutionOptions ?? throw new ArgumentException("Execution options are required.", nameof(request)),
            new AgentSendOptions { Input = request.PreparedInput ?? AgentInput.Text(request.Prompt), AskId = request.AskId },
            cancellationToken);
        return new SessionCommandResult
        {
            Outcome = SessionCommandOutcomeKind.Submitted,
            Session = SessionViewDescriptorSnapshot.FromDescriptor(session),
            RunId = runId.Value,
        };
    }

    public async ValueTask<SessionCommandResult> SteerAsync(SteerSessionRequest request, CancellationToken cancellationToken = default)
    {
        var session = ResolveSession(request.Context);
        var runId = await _runtimeService.SteerAsync(
            session,
            request.Context.ExecutionOptions ?? throw new ArgumentException("Execution options are required.", nameof(request)),
            new AgentSteerOptions { Input = request.PreparedInput ?? AgentInput.Text(request.Prompt) },
            cancellationToken);
        return new SessionCommandResult
        {
            Outcome = SessionCommandOutcomeKind.Steered,
            Session = SessionViewDescriptorSnapshot.FromDescriptor(session),
            RunId = runId.Value,
        };
    }

    public async ValueTask<SessionCommandResult> AbortAsync(AbortSessionRequest request, CancellationToken cancellationToken = default)
    {
        await _runtimeService.AbortAsync(request.SessionId, cancellationToken);
        return new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Completed };
    }

    public async ValueTask<SessionCommandResult> CompactAsync(CompactSessionRequest request, CancellationToken cancellationToken = default)
    {
        var session = ResolveSession(request.Context);
        await _runtimeService.CompactAsync(
            session,
            request.Context.ExecutionOptions ?? throw new ArgumentException("Execution options are required.", nameof(request)),
            cancellationToken);
        return new SessionCommandResult
        {
            Outcome = SessionCommandOutcomeKind.Completed,
            Session = SessionViewDescriptorSnapshot.FromDescriptor(session),
        };
    }

    public ValueTask<SessionCommandResult> ActivateSkillAsync(ActivateSkillRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Completed });

    public async ValueTask<SessionCommandResult> QueuePromptAsync(QueueSessionPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = ResolveSession(request.Context);
        var item = await _runtimeService.QueuePromptAsync(session, request.Prompt, "send", submittedBy: null, cancellationToken);
        return new SessionCommandResult
        {
            Outcome = SessionCommandOutcomeKind.Queued,
            Session = SessionViewDescriptorSnapshot.FromDescriptor(session),
            Message = item.QueueItemId,
        };
    }

    public ValueTask<SessionSnapshot?> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_findSession(sessionId) is { } session
            ? new SessionSnapshot { Session = SessionViewDescriptorSnapshot.FromDescriptor(session), IsRunning = false, QueuedPromptCount = 0 }
            : null);

    public async IAsyncEnumerable<SessionOrchestratorEvent> StreamEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private SessionViewDescriptor ResolveSession(SessionCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            throw new ArgumentException("A materialized session id is required.", nameof(context));
        }

        return _findSession(context.SessionId)
            ?? throw new InvalidOperationException($"Session '{context.SessionId}' was not found.");
    }
}
