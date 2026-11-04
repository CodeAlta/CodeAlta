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
/// <para>
/// The guard of a session is looked up at each call, not when the session is attached: one given after the session was
/// attached applies at its next call.
/// </para>
/// <para>
/// A guard is a durable constraint of a session and of what comes from it, not of one turn: a session without a guard of
/// its own takes the one its origin gives it (<see cref="AddOrigin"/>: the automation that created it, as the session
/// records it), else the one of the session it comes from (its parent, the session that created it), up its lineage. So
/// its later turns (a queued prompt, a reminder, the result of a job), its children (<c>agent()</c>, <c>alta session
/// create</c>) and the session resumed after a restart are checked as its first turn was.
/// </para>
/// </remarks>
public sealed class SessionToolGuards
{
    // The most sessions up a lineage that are looked at: a cycle of links, or a deeper one, ends there.
    private const int MaximumDepth = 32;
    private readonly ConcurrentDictionary<string, AgentToolInterceptor> _guards = new(StringComparer.Ordinal);
    private readonly List<Func<string, CodeAlta.Catalog.AltaActorProvenance, AgentToolInterceptor?>> _origins = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Gets or sets what reads a stored session, for the lineage of the sessions whose guard comes from where they come from;
    /// null reads none: a session then takes only a guard given to it, or the one its own origin gives.
    /// </summary>
    public Func<string, CancellationToken, Task<CodeAlta.Catalog.SessionViewDescriptor?>>? ReadSession { get; set; }

    /// <summary>
    /// Adds what gives a session the guard of its origin: it is given the session and who created it, as the session records
    /// it, and answers its guard, or null when that origin gives none (an automation gives the sessions of its runs the
    /// guard of its autonomy, also once the run ended and after a restart).
    /// </summary>
    /// <param name="origin">The origin.</param>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> is null.</exception>
    public void AddOrigin(Func<string, CodeAlta.Catalog.AltaActorProvenance, AgentToolInterceptor?> origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        lock (_gate) _origins.Add(origin);
    }

    // The guard the origin of a session gives it, or null.
    private AgentToolInterceptor? OriginOf(string sessionId, CodeAlta.Catalog.AltaActorProvenance? createdBy)
    {
        if (createdBy is null) return null;
        Func<string, CodeAlta.Catalog.AltaActorProvenance, AgentToolInterceptor?>[] origins;
        lock (_gate) origins = [.. _origins];
        foreach (var origin in origins)
        {
            if (origin(sessionId, createdBy) is { } guard) return guard;
        }

        return null;
    }

    // The guard of a session that has none of its own: the one of its origin, else the one of the session it comes from, up
    // its lineage, each read from the store.
    private async ValueTask<AgentToolInterceptor?> InheritedAsync(string sessionId, CodeAlta.Catalog.AltaActorProvenance? createdBy, string? parentSessionId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionId };
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (OriginOf(sessionId, createdBy) is { } origin) return origin;
            if (string.IsNullOrWhiteSpace(parentSessionId) || !seen.Add(parentSessionId)) return null;
            if (Find(parentSessionId) is { } given) return given;
            if (ReadSession is not { } read || await read(parentSessionId, cancellationToken).ConfigureAwait(false) is not { } parent) return null;
            (sessionId, createdBy, parentSessionId) = (parent.SessionId, parent.CreatedBy, parent.ParentSessionId ?? parent.CreatedBy?.SourceSessionId);
        }

        return null;
    }

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

    /// <summary>Gets the guard every tool call of a session goes through now, or null.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The guard; null when the session has none.</returns>
    public async ValueTask<AgentToolInterceptor?> FindAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (Find(sessionId) is { } given) return given;
        if (ReadSession is not { } read || await read(sessionId, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        return await InheritedAsync(session.SessionId, session.CreatedBy, session.ParentSessionId ?? session.CreatedBy?.SourceSessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a call of a tool that a session makes otherwise than as a tool call of its own (through a command of <c>alta</c>),
    /// as its guard checks its own calls: a command is never a way around the guard of a session. The guard may wait for a
    /// person. A guard that would change the arguments of the call refuses it: only a tool call of the session runs with them.
    /// </summary>
    /// <param name="sessionId">The session that makes the call.</param>
    /// <param name="spec">The tool, as the session has it.</param>
    /// <param name="arguments">The arguments of the call.</param>
    /// <param name="workingDirectory">The folder the session works in; null for the current one.</param>
    /// <param name="cancellationToken">Cancels the check, and what it waits for.</param>
    /// <returns>Why the call is not made; null to make it, also when the session has no guard.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
    public async ValueTask<string?> CheckAsync(string sessionId, AgentToolSpec spec, System.Text.Json.JsonElement arguments, string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } guard) return null;
        var invocation = new AgentToolInvocation(new ModelProviderId("alta"), sessionId, "alta-" + Guid.NewGuid().ToString("N"), spec.Name, arguments);
        var decision = await guard.BeforeToolCallAsync(AgentToolHookCall.Create(invocation, spec, workingDirectory, AgentToolHookCapabilities.Block), cancellationToken)
            .ConfigureAwait(false);
        return decision.BlockMessage
            ?? (decision.Arguments is not null ? $"The guard of this session would change the arguments of this call of {spec.Name}: make it as a tool call of the session." : null);
    }

    /// <summary>The hooks of a session: those of the plugins, when there are, then the guard the session has at each call.</summary>
    /// <param name="scope">The session.</param>
    /// <param name="plugins">The hooks of the plugins for it; null for none.</param>
    /// <returns>The hooks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    public AgentToolInterceptor Compose(SessionToolHookScope scope, AgentToolInterceptor? plugins)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new Guarded(this, scope, plugins);
    }

    private sealed class Guarded(SessionToolGuards guards, SessionToolHookScope scope, AgentToolInterceptor? plugins) : AgentToolInterceptor
    {
        // The tools of the session, intercepted: a guard calls one first (a precondition), and that call is guarded too.
        private readonly ConcurrentDictionary<string, AgentToolDefinition> _tools = new(StringComparer.Ordinal);
        // The guard the session takes from where it comes from, found at its first call of this attachment: its lineage does not change.
        private Task<AgentToolInterceptor?>? _inherited;

        protected override void OnIntercepted(AgentToolDefinition tool) => _tools[tool.Spec.Name] = tool;

        // The guard given to the session, else the one it inherits; a lookup that failed is made again at the next call.
        private async ValueTask<AgentToolInterceptor?> GuardAsync(CancellationToken cancellationToken)
        {
            if (guards.Find(scope.SessionId) is { } given) return given;
            var inherited = Volatile.Read(ref _inherited);
            if (inherited is null || inherited.IsFaulted || inherited.IsCanceled)
            {
                var started = guards.InheritedAsync(scope.SessionId, scope.CreatedBy, scope.ParentSessionId, CancellationToken.None).AsTask();
                inherited = Interlocked.CompareExchange(ref _inherited, started, inherited) == inherited ? started : Volatile.Read(ref _inherited)!;
            }

            return await inherited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            var decision = plugins is null ? AgentToolCallDecision.Allow : await plugins.BeforeToolCallAsync(call, cancellationToken).ConfigureAwait(false);
            if (decision.BlockMessage is not null || await GuardAsync(cancellationToken).ConfigureAwait(false) is not { } guard) return decision;
            // The guard checks the call as it would run: with the arguments the plugins gave it.
            var checkedCall = (decision.Arguments is { } arguments
                ? AgentToolHookCall.Create(call.Invocation with { Arguments = arguments }, call.Spec, call.WorkingDirectory, call.Capabilities, call.RunByAgent)
                : call) with { FindTool = name => _tools.TryGetValue(name, out var tool) ? tool : null };
            var guarded = await guard.BeforeToolCallAsync(checkedCall, cancellationToken).ConfigureAwait(false);
            return guarded.BlockMessage is not null ? guarded : guarded.Arguments is not null ? guarded : decision;
        }

        public override async ValueTask<AgentToolResult> AfterToolCallAsync(AgentToolHookCall call, AgentToolResult result, CancellationToken cancellationToken)
        {
            var changed = plugins is null ? result : await plugins.AfterToolCallAsync(call, result, cancellationToken).ConfigureAwait(false);
            return await GuardAsync(cancellationToken).ConfigureAwait(false) is { } guard ? await guard.AfterToolCallAsync(call, changed, cancellationToken).ConfigureAwait(false) : changed;
        }
    }
}
