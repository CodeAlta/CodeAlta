using CodeAlta.Agent;
using CodeAlta.Catalog;

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
/// Gives the identity of the sessions of a space, read from the file of the space and from the configuration of the
/// project when asked: a session reads it once, when it attaches, and keeps it while it runs. A host without spaces
/// (the terminal application) gives the usual settings to every session.
/// </summary>
public sealed class SessionIdentityResolver
{
    private readonly SpaceCatalog? _spaces;
    private readonly Func<SpaceIdentity?, AgentProcessEnvironment> _environment;
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
    {
        _spaces = spaces;
        _environment = environment ?? SpaceIdentityEnvironment.Create;
        _projectProfiles = projectProfiles ?? (static _ => []);
    }

    /// <summary>Gets a resolver that gives every session the usual settings.</summary>
    public static SessionIdentityResolver None { get; } = new(null);

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
    public async Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken = default)
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

        return Of(space, profiles);
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
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(projectProfiles);
        var identity = (space.Settings.Identity ?? new SpaceIdentity()).WithProjectCli(projectProfiles);
        if (identity.IsEmpty) return new SessionIdentity(space.Id, null, AgentProcessEnvironment.Empty);
        return new SessionIdentity(space.Id, identity, _environment(identity))
        {
            ProjectProfiles = [.. projectProfiles.Select(static profile => profile.Name)],
        };
    }
}
