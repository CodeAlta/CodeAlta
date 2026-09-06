using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.LiveTool;

/// <summary>Adapts durable runtime notes to the live tool. No open view is required or retained.</summary>
public sealed class RuntimeAltaNotesService : IAltaNotesService
{
    private readonly SessionRuntimeService _runtime;
    private readonly Func<string?> _currentSessionId;

    /// <summary>Creates an adapter with no current-session fallback.</summary>
    /// <param name="runtime">The shared runtime owning the configured journal root.</param>
    /// <exception cref="ArgumentNullException">The runtime is null.</exception>
    public RuntimeAltaNotesService(SessionRuntimeService runtime) : this(runtime, static () => null) { }

    /// <summary>Creates an adapter with a narrow synchronous identity snapshot for host callers.</summary>
    /// <param name="runtime">The shared runtime.</param>
    /// <param name="currentSessionId">Captures only an identifier, never storage or a frontend view. Used only when the caller has no explicit identifier.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public RuntimeAltaNotesService(SessionRuntimeService runtime, Func<string?> currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(currentSessionId);
        _runtime = runtime;
        _currentSessionId = currentSessionId;
    }

    /// <inheritdoc />
    public event EventHandler<AltaNotesChangedEventArgs>? Changed;

    /// <inheritdoc />
    public AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var sessionId = !string.IsNullOrWhiteSpace(caller.SourceSessionId) ? caller.SourceSessionId : _currentSessionId();
        return string.IsNullOrWhiteSpace(sessionId)
            ? throw new AltaNotesSessionRequiredException()
            : caller with { SourceSessionId = sessionId };
    }

    /// <inheritdoc />
    public async ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = CaptureCaller(caller);
        try
        {
            // CaptureCaller guarantees a nonempty source identifier.
            return await _runtime.GetNotesMarkdownAsync(captured.SourceSessionId!, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionNotesSessionNotFoundException)
        {
            throw new AltaNotesSessionRequiredException();
        }
    }

    /// <inheritdoc />
    public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
        => UpdateAsync(markdown, caller, AgentNotesUpdateKind.Set, cancellationToken);

    /// <inheritdoc />
    public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
        => UpdateAsync(string.Empty, caller, AgentNotesUpdateKind.Cleared, cancellationToken);

    private async ValueTask UpdateAsync(string markdown, AltaCallerIdentity caller, AgentNotesUpdateKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = CaptureCaller(caller);
        try
        {
            // CaptureCaller guarantees a nonempty source identifier.
            await _runtime.UpdateNotesAsync(captured.SourceSessionId!, markdown, kind,
                notes => Changed?.Invoke(this, new AltaNotesChangedEventArgs(notes.SessionId, notes.Markdown, captured)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SessionNotesSessionNotFoundException)
        {
            throw new AltaNotesSessionRequiredException();
        }
    }
}
