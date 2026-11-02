namespace CodeAlta.Desktop.SessionHosts;

/// <summary>
/// The hosts of the sessions the window shows, and which one owns a session: the host of this process for a session
/// that names no machine, the host of its machine otherwise.
/// </summary>
/// <remarks>
/// The registry is immutable: it is made once with the hosts it knows. The RPC services of the page resolve the
/// host of each request through it, so a host added later (a paired node) is reached without changing them.
/// </remarks>
internal sealed class SessionHostRegistry
{
    private readonly Dictionary<string, ISessionHost> _remotes;

    /// <summary>Creates a registry of the host of this process alone.</summary>
    /// <param name="local">The host of this process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="local"/> names a machine.</exception>
    public SessionHostRegistry(ISessionHost local) : this(local, []) { }

    /// <summary>Creates a registry of the host of this process and of the hosts of other machines.</summary>
    /// <param name="local">The host of this process.</param>
    /// <param name="remotes">The hosts of other machines, one per machine.</param>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> or <paramref name="remotes"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="local"/> names a machine, a remote host names none, or two name the same one.</exception>
    public SessionHostRegistry(ISessionHost local, IEnumerable<ISessionHost> remotes)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remotes);
        if (local.MachineId is not null) throw new ArgumentException("The host of this process names no machine.", nameof(local));
        Local = local;
        _remotes = new(StringComparer.Ordinal);
        foreach (var remote in remotes)
        {
            if (remote?.MachineId is not { Length: > 0 } machine) throw new ArgumentException("A remote host names its machine.", nameof(remotes));
            if (!_remotes.TryAdd(machine, remote)) throw new ArgumentException($"Two hosts name the machine '{machine}'.", nameof(remotes));
        }
    }

    /// <summary>Gets the host of this process.</summary>
    public ISessionHost Local { get; }

    /// <summary>Gets the hosts of other machines.</summary>
    public IReadOnlyCollection<ISessionHost> Remotes => _remotes.Values;

    /// <summary>Resolves the host of a session that names no machine: the host of this process.</summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <returns>The host of this process.</returns>
    public ISessionHost Resolve(string sessionId) => Resolve(new SessionReference(sessionId));

    /// <summary>Resolves the host that owns a session.</summary>
    /// <param name="session">The session and its machine.</param>
    /// <returns>The host of the machine of the session; the host of this process when it names none.</returns>
    /// <exception cref="KeyNotFoundException">The session names a machine the registry has no host for.</exception>
    public ISessionHost Resolve(SessionReference session) => TryResolve(session, out var host)
        ? host
        : throw new KeyNotFoundException($"No host is known for the machine '{session.MachineId}'.");

    /// <summary>Resolves the host that owns a session, when the registry knows its machine.</summary>
    /// <param name="session">The session and its machine.</param>
    /// <param name="host">The host of the session, when found.</param>
    /// <returns>Whether the registry has a host for the machine of the session.</returns>
    public bool TryResolve(SessionReference session, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISessionHost? host)
    {
        if (session.MachineId is not { } machine)
        {
            host = Local;
            return true;
        }

        return _remotes.TryGetValue(machine, out host);
    }

    /// <summary>
    /// Resolves the host that admitted an operation, for the abort of that operation: the host of this process,
    /// the only one that admits commands of the page for now.
    /// </summary>
    /// <param name="operationId">The operation the abort targets.</param>
    /// <returns>The host of this process.</returns>
    public ISessionHost ResolveOperation(Guid operationId) => Local;
}
