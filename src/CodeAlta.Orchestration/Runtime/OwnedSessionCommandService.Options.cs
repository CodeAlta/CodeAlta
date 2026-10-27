using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The outcome of changing the options of an owned session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c> when the session is unknown or its attachment is not owned; <c>busy</c> while its
/// provider is being changed or it is being deleted; <c>session_in_use</c> when another process holds it; <c>closed</c>
/// when the owner is closing.
/// </param>
/// <param name="Options">The options of the session after the request when <paramref name="Status"/> is <c>ok</c>.</param>
public sealed record OwnedSessionOptionsResult(string Status, AgentSessionOptions? Options);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads the options of an owned session: those of its attachment while it is attached, else those saved with it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><c>ok</c> with the options; <c>unavailable</c> when the session is unknown; <c>closed</c> when the owner is closing.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public async Task<OwnedSessionOptionsResult> GetSessionOptionsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
        }

        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null);
            return new("ok", await _runtime.GetSessionOptionsAsync(sessionId, cancellationToken).ConfigureAwait(false));
        }
        catch (ObjectDisposedException) { return new("closed", null); }
        catch (InvalidOperationException) { return new("unavailable", null); }
    }

    /// <summary>
    /// Changes the options of an owned session, as the user chooses them: they are saved with the session, and its
    /// tools and instructions follow them from its next prompt. A run in progress goes on with what it started with.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="options">The options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the request before the options are saved.</param>
    /// <returns>The outcome and, when it is <c>ok</c>, the options of the session.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedSessionOptionsResult> SetSessionOptionsAsync(string sessionId, AgentSessionOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
            if (Reserved(sessionId)) return new("busy", null);
        }

        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null);
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.SetOwnedOptionsAsync(session, options).ConfigureAwait(false);
            return new("ok", AgentSessionOptions.Of(options));
        }
        catch (ObjectDisposedException) { return new("closed", null); }
        catch (SessionInUseException) { return new("session_in_use", null); }
        catch (InvalidOperationException) { return new("unavailable", null); }
    }
}
