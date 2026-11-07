using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Gets or sets what makes the tool hooks of a session (those of the plugins, <see cref="PluginOrchestrationBridge.CreateToolInterceptor"/>):
    /// they run before and after each tool call of the session, before its guards, its permission prompt and its sandbox.
    /// Read when a session is attached; <see langword="null"/>, the default, gives sessions no tool hook.
    /// </summary>
    public Func<SessionToolHookScope, AgentToolInterceptor?>? ToolInterceptorOf { get; set; }

    /// <summary>
    /// Gets or sets the guards the host gives sessions (<see cref="SessionToolGuards"/>), which also check what a session calls
    /// through a command of <c>alta</c> rather than as a tool call of its own; <see langword="null"/> for none.
    /// </summary>
    public SessionToolGuards? ToolGuards { get; set; }

    /// <summary>
    /// Gets or sets what refuses a provider to a session, checked at every attachment of the session (its first one, a change of
    /// provider, a model tier or a fallback that resolves to another provider, a child that starts): it is given the session and
    /// the key of the provider it would run with, and answers why it may not, or <see langword="null"/>. A node refuses the
    /// sessions of an unattended run, and those they start, a provider that does not use account credentials. A refusal fails
    /// the attachment. <see langword="null"/>, the default, refuses nothing.
    /// </summary>
    public Func<SessionViewDescriptor, string, CancellationToken, ValueTask<string?>>? ProviderGate { get; set; }

    /// <summary>
    /// Adds a check to <see cref="ProviderGate"/>, after those already there: the first that refuses gives the reason. Several
    /// parts of a host refuse providers for reasons of their own (the runs of automations, the work of other people).
    /// </summary>
    /// <param name="gate">The check: the session and the key of the provider; why it may not, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is null.</exception>
    public void AddProviderGate(Func<SessionViewDescriptor, string, CancellationToken, ValueTask<string?>> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (ProviderGate is not { } before)
        {
            ProviderGate = gate;
            return;
        }

        ProviderGate = async (session, providerKey, cancellationToken)
            => await before(session, providerKey, cancellationToken).ConfigureAwait(false) ?? await gate(session, providerKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells whether a session or a session of its lineage matches a condition: the session itself first, then its parent (or the
    /// session that created it), up its lineage, read from the store.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="match">The condition.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether one matches, up to the start of the lineage (a session without a parent, or a parent this machine does not have).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SessionLineageException">The lineage is deeper than <see cref="SessionToolGuards.MaximumLineageDepth"/>, or its
    /// links make a cycle.</exception>
    public async Task<bool> AnyInLineageAsync(SessionViewDescriptor session, Func<SessionViewDescriptor, bool> match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(match);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = session; current is not null;)
        {
            if (!seen.Add(current.SessionId) || seen.Count > SessionToolGuards.MaximumLineageDepth) throw new SessionLineageException(session.SessionId);
            if (match(current)) return true;
            if ((NormalizeOptionalText(current.ParentSessionId) ?? NormalizeOptionalText(current.CreatedBy?.SourceSessionId)) is not { } parent) return false;
            current = await TryGetRecoverableSessionAsync(parent, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Gets or sets what gives the patterns over tool names the host requires a session to do without, read at every preparation
    /// of an attachment of the session: they are added to those set on it (<see cref="SetDisabledToolPatterns"/>), which this
    /// runtime holds in memory only, so that a session resumed after a restart of the process, and every session it starts, does
    /// without them again. A node gives the tools the signed policy of an organization turns off for the work of the fleet. A
    /// pattern that is not valid fails the attachment. <see langword="null"/>, the default, requires nothing.
    /// </summary>
    public Func<SessionViewDescriptor, CancellationToken, ValueTask<IReadOnlyList<string>?>>? ToolPatternGate { get; set; }

    // Adds the patterns the host requires of a session to those it holds, before its attachment is prepared.
    private async ValueTask RequireHostToolPatternsAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        if (ToolPatternGate is not { } gate || await gate(session, cancellationToken).ConfigureAwait(false) is not { Count: > 0 } required) return;
        var held = GetDisabledToolPatterns(session.SessionId);
        if (required.All(pattern => held.Contains(pattern.Trim(), StringComparer.OrdinalIgnoreCase))) return;
        try
        {
            SetDisabledToolPatterns(session.SessionId, [.. held.Union(required, StringComparer.OrdinalIgnoreCase)]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The tools this session must do without do not read: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Finds who created a session or the nearest session it comes from that records a creator matching a condition: the
    /// session itself first, then its parent (or the session that created it), up its lineage, read from the store.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="match">The condition, such as a creator of the automation kind.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The creator found; null when no session of the lineage records one that matches, up to its start (a session
    /// without a parent, or a parent this machine does not have).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SessionLineageException">The lineage is deeper than <see cref="SessionToolGuards.MaximumLineageDepth"/>, or
    /// its links make a cycle: whether a session of it matches cannot be told.</exception>
    public async Task<AltaActorProvenance?> FindLineageCreatorAsync(SessionViewDescriptor session, Func<AltaActorProvenance, bool> match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(match);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = session; current is not null;)
        {
            if (!seen.Add(current.SessionId) || seen.Count > SessionToolGuards.MaximumLineageDepth) throw new SessionLineageException(session.SessionId);
            if (current.CreatedBy is { } createdBy && match(createdBy)) return createdBy;
            if ((NormalizeOptionalText(current.ParentSessionId) ?? NormalizeOptionalText(current.CreatedBy?.SourceSessionId)) is not { } parent) return null;
            current = await TryGetRecoverableSessionAsync(parent, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async ValueTask<AgentToolInterceptor?> CreateToolInterceptorAsync(SessionViewDescriptor session, ProjectDescriptor? project,
        string? workingDirectory, CancellationToken cancellationToken)
    {
        if (ToolInterceptorOf is not { } create) return null;
        string? spaceId;
        try
        {
            spaceId = (await ResolveSpaceAsync(session.SpaceId, project, cancellationToken).ConfigureAwait(false)).Id;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Spaces that cannot be read limit no plugin, as for the other contributions of plugins.
            spaceId = null;
        }

        return create(new SessionToolHookScope(session.SessionId, project?.Id, workingDirectory, spaceId)
        {
            ParentSessionId = NormalizeOptionalText(session.ParentSessionId) ?? NormalizeOptionalText(session.CreatedBy?.SourceSessionId),
            CreatedBy = session.CreatedBy,
        });
    }
}
