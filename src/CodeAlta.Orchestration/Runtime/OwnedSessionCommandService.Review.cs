namespace CodeAlta.Orchestration.Runtime;

/// <summary>Whether the user reviews the changes of a session before an agent commits them.</summary>
/// <param name="Status"><c>ok</c>; <c>unavailable</c> when the session is unknown; <c>busy</c> while it is being changed or deleted; <c>closed</c> when the owner is closing.</param>
/// <param name="Own">The session's own choice; null when it follows the setting.</param>
/// <param name="Effective">What applies: the session's choice, else the setting.</param>
/// <param name="Setting">The setting, <c>[review] before_commit</c>.</param>
public sealed record OwnedReviewBeforeCommit(string Status, bool? Own, bool Effective, bool Setting);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads whether the user reviews the changes of a session before an agent commits them.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The session's choice, what applies and the setting.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedReviewBeforeCommit> GetReviewBeforeCommitAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var setting = _runtime.LoadReviewBeforeCommitSetting();
        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null) return new("unavailable", null, setting, setting);
            var (own, effective) = await _runtime.GetReviewBeforeCommitAsync(session, cancellationToken).ConfigureAwait(false);
            return new("ok", own, effective, setting);
        }
        catch (ObjectDisposedException) { return new("closed", null, setting, setting); }
    }

    /// <summary>
    /// Chooses whether the user reviews the changes of a session before an agent commits them, at once: the commit
    /// guard reads it at each command, and the agent is told with its next prompt. The instructions of the session
    /// do not change (<see cref="SessionRuntimeService.SetReviewBeforeCommitAsync"/>).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reviewBeforeCommit">The choice; null follows the setting.</param>
    /// <param name="cancellationToken">Cancels the request before the choice is saved.</param>
    /// <returns>The outcome, and the state after it.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedReviewBeforeCommit> SetReviewBeforeCommitAsync(string sessionId, bool? reviewBeforeCommit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var setting = _runtime.LoadReviewBeforeCommitSetting();
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null, setting, setting);
            if (Reserved(sessionId)) return new("busy", null, setting, setting);
        }

        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null, setting, setting);
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.SetReviewBeforeCommitAsync(session, reviewBeforeCommit).ConfigureAwait(false);
            return new("ok", reviewBeforeCommit, !_runtime.NeverReviews && (reviewBeforeCommit ?? setting), setting);
        }
        catch (ObjectDisposedException) { return new("closed", null, setting, setting); }
    }
}
