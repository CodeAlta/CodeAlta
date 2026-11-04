using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The tool guards of the host, by session: a part of the host (the automations) gives a session a guard of its own,
/// which every tool call of that session goes through after the hooks of the plugins and before the guards, the
/// permission prompt and the sandbox of the session (<c>doc/fleet/orchestration.md</c>, "Autonomy and approvals": what a
/// run may do without a person is enforced on the machine, never by its prompt).
/// </summary>
/// <remarks>
/// The guard of a session is looked up at each call, not when the session is attached: one given after the session was
/// attached applies at its next call, and one taken away stops applying.
/// </remarks>
public sealed class SessionToolGuards
{
    private readonly ConcurrentDictionary<string, AgentToolInterceptor> _guards = new(StringComparer.Ordinal);

    /// <summary>Gives a session a guard, in place of the one it had.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="guard">The guard.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="guard"/> is null.</exception>
    public void Set(string sessionId, AgentToolInterceptor guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(guard);
        _guards[sessionId] = guard;
    }

    /// <summary>Takes the guard of a session away, when it is still the one given.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="guard">The guard that was given.</param>
    /// <returns>Whether it was taken away.</returns>
    public bool Remove(string sessionId, AgentToolInterceptor guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return !string.IsNullOrWhiteSpace(sessionId) && _guards.TryRemove(new KeyValuePair<string, AgentToolInterceptor>(sessionId, guard));
    }

    /// <summary>Gets the guard of a session, or null.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The guard; null when the session has none.</returns>
    public AgentToolInterceptor? Find(string sessionId) => !string.IsNullOrWhiteSpace(sessionId) && _guards.TryGetValue(sessionId, out var guard) ? guard : null;

    /// <summary>The hooks of a session: those of the plugins, when there are, then the guard the session has at each call.</summary>
    /// <param name="scope">The session.</param>
    /// <param name="plugins">The hooks of the plugins for it; null for none.</param>
    /// <returns>The hooks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    public AgentToolInterceptor Compose(SessionToolHookScope scope, AgentToolInterceptor? plugins)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new Guarded(this, scope.SessionId, plugins);
    }

    private sealed class Guarded(SessionToolGuards guards, string sessionId, AgentToolInterceptor? plugins) : AgentToolInterceptor
    {
        public override async ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            var decision = plugins is null ? AgentToolCallDecision.Allow : await plugins.BeforeToolCallAsync(call, cancellationToken).ConfigureAwait(false);
            if (decision.BlockMessage is not null || guards.Find(sessionId) is not { } guard) return decision;
            // The guard checks the call as it would run: with the arguments the plugins gave it.
            var checkedCall = decision.Arguments is { } arguments
                ? AgentToolHookCall.Create(call.Invocation with { Arguments = arguments }, call.Spec, call.WorkingDirectory, call.Capabilities, call.RunByAgent)
                : call;
            var guarded = await guard.BeforeToolCallAsync(checkedCall, cancellationToken).ConfigureAwait(false);
            return guarded.BlockMessage is not null ? guarded : guarded.Arguments is not null ? guarded : decision;
        }

        public override async ValueTask<AgentToolResult> AfterToolCallAsync(AgentToolHookCall call, AgentToolResult result, CancellationToken cancellationToken)
        {
            var changed = plugins is null ? result : await plugins.AfterToolCallAsync(call, result, cancellationToken).ConfigureAwait(false);
            return guards.Find(sessionId) is { } guard ? await guard.AfterToolCallAsync(call, changed, cancellationToken).ConfigureAwait(false) : changed;
        }
    }
}
