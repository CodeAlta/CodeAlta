using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The identity a session works under: the one of its space, with the CLI profiles of its project over it, as they
/// were when the session attached. It holds only references (names, emails, paths, names of variables), never a secret.
/// </summary>
/// <param name="SpaceId">The space the identity is of; <see langword="null"/> for the usual settings of a host without spaces.</param>
/// <param name="Identity">The identity of the space with the profiles of the project; <see langword="null"/> when it sets nothing.</param>
/// <param name="Environment">The variables the processes of the session start with.</param>
public sealed record SessionIdentity(string? SpaceId, SpaceIdentity? Identity, AgentProcessEnvironment Environment)
{
    /// <summary>Gets the identity that changes nothing: the user's usual Git, GitHub, Jira and CLI settings.</summary>
    public static SessionIdentity Usual { get; } = new(null, null, AgentProcessEnvironment.Empty);

    /// <summary>Gets whether the identity changes nothing.</summary>
    public bool IsUsual => Identity is null || Identity.IsEmpty;

    /// <summary>
    /// Gets the names of the CLI profiles of <see cref="Identity"/> that the project of the session gives or changes;
    /// empty when they are all the space's.
    /// </summary>
    public IReadOnlyList<string> ProjectProfiles { get; init; } = [];
}

/// <summary>
/// The environments (<c>doc/fleet/secrets.md</c>, environments) the sessions of this host were admitted to, by session:
/// the host that admits a run (its protection rules held, its approval verified) records it before the session attaches,
/// and the processes of that session then get the values of that environment. Nothing is kept across a restart: a run
/// is admitted again.
/// </summary>
public sealed class SessionSecretEnvironments
{
    private readonly ConcurrentDictionary<string, string> _environments = new(StringComparer.Ordinal);

    /// <summary>Records the environment a session was admitted to.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="environment">The name of the environment.</param>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public void Admit(string sessionId, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        _environments[sessionId] = environment;
    }

    /// <summary>Gets the environment a session was admitted to.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The name of the environment, or null.</returns>
    public string? Get(string sessionId) => _environments.GetValueOrDefault(sessionId);

    /// <summary>Forgets the environment of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Whether it had one.</returns>
    public bool Remove(string sessionId) => _environments.TryRemove(sessionId, out _);
}

/// <summary>
/// Gives the identity of the sessions of a space, read from the file of the space and from the configuration of the
/// project when asked: a session reads it once, when it attaches, and keeps it while it runs. A host without spaces
/// (the terminal application) gives the usual settings to every session.
/// </summary>
public sealed class SessionIdentityResolver
{
    private readonly SpaceCatalog? _spaces;
    private readonly Func<SpaceIdentity?, SecretScope, AgentProcessEnvironment> _environment;
    private readonly Func<string, IReadOnlyList<SpaceCliProfile>> _projectProfiles;

    /// <summary>Initializes a resolver.</summary>
    /// <param name="spaces">The spaces, or <see langword="null"/> for a host whose sessions keep the usual settings.</param>
    /// <param name="environment">Builds the environment of an identity; null for <see cref="SpaceIdentityEnvironment.Create(SpaceIdentity?)"/>.</param>
    /// <param name="projectProfiles">
    /// Reads the CLI profiles a project gives its sessions, from the folder of the project; null for none, such as
    /// <see cref="CodeAltaConfigStore.LoadProjectCliProfiles(string?)"/>. It never throws.
    /// </param>
    public SessionIdentityResolver(SpaceCatalog? spaces, Func<SpaceIdentity?, AgentProcessEnvironment>? environment = null,
        Func<string, IReadOnlyList<SpaceCliProfile>>? projectProfiles = null)
        : this(spaces, environment is null ? null : (identity, _) => environment(identity), projectProfiles, null)
    {
    }

    /// <summary>
    /// Initializes a resolver whose environments are built for the work of each session (<c>doc/fleet/secrets.md</c>,
    /// scopes): its space, its project, the environment it was admitted to, and the session as the run its uses are recorded for.
    /// </summary>
    /// <param name="spaces">The spaces, or <see langword="null"/> for a host whose sessions keep the usual settings.</param>
    /// <param name="environment">Builds the environment of an identity for the work of a session; null for <see cref="SpaceIdentityEnvironment.Create(SpaceIdentity?)"/>.</param>
    /// <param name="projectProfiles">Reads the CLI profiles a project gives its sessions; null for none. It never throws.</param>
    /// <param name="environments">The environments sessions were admitted to; null for none.</param>
    public SessionIdentityResolver(SpaceCatalog? spaces, Func<SpaceIdentity?, SecretScope, AgentProcessEnvironment>? environment,
        Func<string, IReadOnlyList<SpaceCliProfile>>? projectProfiles, SessionSecretEnvironments? environments)
    {
        _spaces = spaces;
        _environment = environment ?? (static (identity, _) => SpaceIdentityEnvironment.Create(identity));
        _projectProfiles = projectProfiles ?? (static _ => []);
        Environments = environments ?? new SessionSecretEnvironments();
    }

    /// <summary>Gets the environments sessions were admitted to, whose values their processes get.</summary>
    public SessionSecretEnvironments Environments { get; }

    /// <summary>Gets a new resolver that gives every session the usual settings (its <see cref="Environments"/> its own).</summary>
    public static SessionIdentityResolver None => new(null);

    /// <summary>Gets whether the sessions of this host take the identity of their space.</summary>
    public bool IsEnabled => _spaces is not null;

    /// <summary>
    /// Gives the identity of a session from what it records: its space, else the space of its project
    /// (<see cref="SpaceCatalog.ResolveSessionSpace(string?, ProjectDescriptor?, IReadOnlyList{SpaceDescriptor})"/>),
    /// with the CLI profiles of its project over the ones of the space.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken = default)
        => ResolveAsync(recordedSpaceId, projectId, null, cancellationToken);

    /// <summary>
    /// Gives the identity of a session (<see cref="ResolveAsync(string?, string?, CancellationToken)"/>), whose environment is
    /// built for its work: its space, its project and the environment it was admitted to.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, string? sessionId, CancellationToken cancellationToken)
    {
        if (_spaces is null) return SessionIdentity.Usual;
        SpaceDescriptor space;
        IReadOnlyList<SpaceCliProfile> profiles = [];
        try
        {
            space = await _spaces.ResolveSessionSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(projectId)
                && await _spaces.Projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) is { ProjectPath: { Length: > 0 } root })
            {
                profiles = _projectProfiles(root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The catalog cannot be read now: nothing is set, as for a space without identity.
            return SessionIdentity.Usual;
        }

        return Of(space, profiles, new SecretScope(space.Id, string.IsNullOrWhiteSpace(projectId) ? null : projectId, sessionId is null ? null : Environments.Get(sessionId))
        {
            RunId = sessionId,
        });
    }

    /// <summary>Gives the identity of one space.</summary>
    /// <param name="spaceId">The identifier of the space.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when no such space exists or it sets none.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<SessionIdentity> ResolveSpaceAsync(string? spaceId, CancellationToken cancellationToken = default)
        => _spaces is null || string.IsNullOrWhiteSpace(spaceId)
            ? SessionIdentity.Usual
            : await ResolveAsync(spaceId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Gives the identity of a space as it is described, without reading the catalog.</summary>
    /// <param name="space">The space.</param>
    /// <returns>Its identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="space"/> is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space) => Of(space, []);

    /// <summary>
    /// Gives the identity of the sessions of a project in a space as they are described, without reading the catalog:
    /// the identity of the space with the CLI profiles of the project over it
    /// (<see cref="SpaceIdentity.WithProjectCli(IReadOnlyList{SpaceCliProfile}?)"/>).
    /// </summary>
    /// <param name="space">The space.</param>
    /// <param name="projectProfiles">The CLI profiles of the project; empty for none.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space, IReadOnlyList<SpaceCliProfile> projectProfiles)
        => Of(space, projectProfiles, new SecretScope(space?.Id, null, null));

    /// <summary>Gives the identity of the sessions of a project in a space for the work of a run, without reading the catalog.</summary>
    /// <param name="space">The space.</param>
    /// <param name="projectProfiles">The CLI profiles of the project; empty for none.</param>
    /// <param name="scope">The work of the run, whose values its environment gets.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space, IReadOnlyList<SpaceCliProfile> projectProfiles, SecretScope scope)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(projectProfiles);
        ArgumentNullException.ThrowIfNull(scope);
        var identity = (space.Settings.Identity ?? new SpaceIdentity()).WithProjectCli(projectProfiles);
        if (identity.IsEmpty) return new SessionIdentity(space.Id, null, AgentProcessEnvironment.Empty);
        return new SessionIdentity(space.Id, identity, _environment(identity, scope))
        {
            ProjectProfiles = [.. projectProfiles.Select(static profile => profile.Name)],
        };
    }
}
