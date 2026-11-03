using System.Collections.Immutable;

namespace CodeAlta.Desktop.SessionHosts;

/// <summary>
/// The hosts of the sessions the window shows, and which one owns a session: the host of this process for a session it
/// owns (<see cref="ISessionHost.Owns"/>), whatever a machine lists; else the host of the one machine that lists it (or
/// that a reference names); the host of this process otherwise.
/// </summary>
/// <remarks>
/// The hosts of other machines come and go while the window runs (a machine is paired or forgotten). Their set is an
/// immutable value replaced whole, so that a resolution never sees half a change; it is changed through one
/// <see cref="Membership"/> only, which the owner of the paired machines takes once (<see cref="TakeMembership"/>).
/// The RPC services of the page resolve the host of each request through the registry, so a host added later is
/// reached without changing them.
/// </remarks>
internal sealed class SessionHostRegistry
{
    private ImmutableDictionary<string, ISessionHost> _remotes = ImmutableDictionary.Create<string, ISessionHost>(StringComparer.Ordinal);
    private int _membershipTaken;

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
        var builder = ImmutableDictionary.CreateBuilder<string, ISessionHost>(StringComparer.Ordinal);
        foreach (var remote in remotes)
        {
            if (remote?.MachineId is not { Length: > 0 } machine) throw new ArgumentException("A remote host names its machine.", nameof(remotes));
            if (!builder.TryAdd(machine, remote)) throw new ArgumentException($"Two hosts name the machine '{machine}'.", nameof(remotes));
        }

        _remotes = builder.ToImmutable();
    }

    /// <summary>Gets the host of this process.</summary>
    public ISessionHost Local { get; }

    /// <summary>Gets the hosts of other machines, as they are now.</summary>
    public IReadOnlyCollection<ISessionHost> Remotes => Volatile.Read(ref _remotes).Values.ToArray();

    /// <summary>Takes the right to add and remove the hosts of other machines: only one owner may hold it.</summary>
    /// <returns>The membership of the registry.</returns>
    /// <exception cref="InvalidOperationException">The membership was taken already.</exception>
    public Membership TakeMembership()
    {
        if (Interlocked.Exchange(ref _membershipTaken, 1) != 0) throw new InvalidOperationException("The membership of the session hosts has one owner.");
        return new Membership(this);
    }

    /// <summary>
    /// Resolves the host of a session: the host of this process when it owns the session, whatever another machine lists
    /// (as the window shows its row); else the host of the one machine that lists it; the host of this process otherwise.
    /// A listing is what a machine says of itself: it never takes a session of this process, and two machines that list
    /// the same session get neither its commands nor its reads.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <returns>The host of the session.</returns>
    public ISessionHost Resolve(string sessionId)
    {
        ISessionHost? claimant = null;
        foreach (var remote in Volatile.Read(ref _remotes).Values)
        {
            if (!remote.Lists(sessionId)) continue;
            if (claimant is not null) return Local;
            claimant = remote;
        }

        return claimant is null || Local.Owns(sessionId) ? Local : claimant;
    }

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

        return Volatile.Read(ref _remotes).TryGetValue(machine, out host);
    }

    /// <summary>Gets whether a session reference names the host the registry resolves for its session.</summary>
    /// <param name="session">The session and the machine a request names for it; null for this process.</param>
    /// <returns>Whether the commands of the session go to that machine.</returns>
    public bool Routes(SessionReference session) => string.Equals(Resolve(session.SessionId).MachineId, session.MachineId, StringComparison.Ordinal);

    /// <summary>Resolves the host that admitted an operation, for the abort of that operation.</summary>
    /// <param name="operationId">The operation the abort targets.</param>
    /// <returns>The host of a machine that admitted it; the host of this process otherwise.</returns>
    public ISessionHost ResolveOperation(Guid operationId)
    {
        foreach (var remote in Volatile.Read(ref _remotes).Values)
            if (remote.Admitted(operationId)) return remote;
        return Local;
    }

    /// <summary>The right to change the hosts of other machines of a <see cref="SessionHostRegistry"/>.</summary>
    internal sealed class Membership
    {
        private readonly SessionHostRegistry _registry;

        internal Membership(SessionHostRegistry registry) => _registry = registry;

        /// <summary>Adds the host of a machine.</summary>
        /// <param name="host">The host.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        /// <exception cref="ArgumentException">The host names no machine, or the registry has a host for its machine already.</exception>
        public void Add(ISessionHost host)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (host.MachineId is not { Length: > 0 } machine) throw new ArgumentException("A remote host names its machine.", nameof(host));
            var current = Volatile.Read(ref _registry._remotes);
            if (current.ContainsKey(machine)) throw new ArgumentException($"A host names the machine '{machine}' already.", nameof(host));
            Volatile.Write(ref _registry._remotes, current.Add(machine, host));
        }

        /// <summary>Removes the host of a machine.</summary>
        /// <param name="machineId">The machine.</param>
        /// <returns>The host that was removed; null when the registry had none for that machine.</returns>
        public ISessionHost? Remove(string machineId)
        {
            ArgumentNullException.ThrowIfNull(machineId);
            var current = Volatile.Read(ref _registry._remotes);
            if (!current.TryGetValue(machineId, out var host)) return null;
            Volatile.Write(ref _registry._remotes, current.Remove(machineId));
            return host;
        }
    }
}
