namespace CodeAlta.Agent.Runtime;

// Takes a request slot of the provider around each call of the executor it wraps: for a provider whose requests
// CodeAlta sends, one call is one model request with its stream (the session runs the tools between the calls); for an
// agent that runs its own loop (Claude Code, Codex CLI, ACP), one call is one turn. The session keeps the executor
// itself for what it asks of it besides the turns.
internal sealed class ProviderRequestGatedTurnExecutor(IModelProviderTurnExecutor inner, ProviderRequestGate gate) : IModelProviderTurnExecutor
{
    public ProviderRequestGate Gate => gate;

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(request, onUpdate, onSessionUpdate: null, cancellationToken);

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        return ExecuteCoreAsync(request, onUpdate, onSessionUpdate, cancellationToken);
    }

    private async Task<AgentTurnResponse> ExecuteCoreAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? onSessionUpdate,
        CancellationToken cancellationToken)
    {
        using var slot = await AcquireAsync(onSessionUpdate, cancellationToken).ConfigureAwait(false);
        return onSessionUpdate is null
            ? await inner.ExecuteTurnAsync(request, onUpdate, cancellationToken).ConfigureAwait(false)
            : await inner.ExecuteTurnAsync(request, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IDisposable> AcquireAsync(
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? onSessionUpdate,
        CancellationToken cancellationToken)
    {
        if (onSessionUpdate is null)
        {
            return await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }

        var notice = new AgentWaitNotice(AgentWaitKind.ProviderRequest, gate.Name, onSessionUpdate);
        try
        {
            return await gate.AcquireAsync(notice.ReportAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await notice.EndAsync().ConfigureAwait(false);
        }
    }
}
