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
    /// Finds who created a session or the nearest session it comes from that records a creator matching a condition: the
    /// session itself first, then its parent (or the session that created it), up its lineage, read from the store.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="match">The condition, such as a creator of the automation kind.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The creator found; null when no session of the lineage records one that matches.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task<AltaActorProvenance?> FindLineageCreatorAsync(SessionViewDescriptor session, Func<AltaActorProvenance, bool> match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(match);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = session; current is not null && seen.Add(current.SessionId) && seen.Count <= 32;)
        {
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
