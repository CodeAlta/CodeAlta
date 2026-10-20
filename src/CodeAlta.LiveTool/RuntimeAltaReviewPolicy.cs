using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.LiveTool;

/// <summary>Tells <c>alta review status</c> whether a session reviews before commit, from the runtime of the host.</summary>
public sealed class RuntimeAltaReviewPolicy : IAltaReviewPolicy
{
    private readonly SessionRuntimeService _runtime;

    /// <summary>Creates the policy over a runtime.</summary>
    /// <param name="runtime">The runtime of the sessions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public RuntimeAltaReviewPolicy(SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc />
    public AltaReviewBeforeCommit GetBeforeCommit(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var own = _runtime.GetOwnReviewBeforeCommit(sessionId);
        return new(_runtime.GetEffectiveReviewBeforeCommit(sessionId), own is null ? "settings" : "session");
    }
}
