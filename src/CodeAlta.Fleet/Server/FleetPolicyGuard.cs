using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>A stop of the kill switch that reaches a machine: who signed it, at which version, and why.</summary>
/// <param name="Server">The address of the server whose organization signed it.</param>
/// <param name="Stop">The stop.</param>
/// <param name="Number">The version of the policy that holds it.</param>
/// <param name="Since">When its signer made it; null when unknown.</param>
public sealed record FleetPolicyHalt(string Server, FleetPolicyStop Stop, long Number, DateTimeOffset? Since)
{
    /// <summary>Gets what a refused request says, for people.</summary>
    /// <param name="machine">The name of the machine.</param>
    /// <returns>The sentence.</returns>
    public string Describe(string machine)
        => $"The work of the fleet is stopped on {machine} by the policy of {Server}{(Stop.Reason is { Length: > 0 } reason ? $": {reason}" : string.Empty)}. "
            + "It goes on once an owner lifts the stop.";
}

/// <summary>The policy of one server as a machine verified it, with what it needs to apply it.</summary>
/// <param name="Server">The address of the server.</param>
/// <param name="Policy">The policy verified; null before the first read.</param>
/// <param name="Access">The access document verified, which says the team of a team machine; null when none.</param>
/// <param name="LinksSpace">Whether a space of the machine is linked to the server.</param>
public sealed record FleetPolicySource(string Server, FleetPolicyState? Policy, FleetAccessState? Access, Func<string?, bool> LinksSpace);

/// <summary>
/// What the signed policies of the servers of a machine decide on it (<c>doc/fleet/security-and-privacy.md</c>, policies and
/// the kill switch), as the machine verified them: whether a stop reaches it, and the rules that apply to the work of the
/// fleet in a space, those of the server the space is linked to. Read live, at each request; the policies kept on disk hold
/// while their servers are unreachable.
/// </summary>
public sealed class FleetPolicyGuard
{
    private readonly Func<string> _deviceId;
    private readonly Func<IEnumerable<FleetPolicySource>> _sources;

    /// <summary>Initializes a new instance of the <see cref="FleetPolicyGuard"/> class over the connectors of the servers of a machine.</summary>
    /// <param name="deviceId">The device of this machine.</param>
    /// <param name="connectors">Gives the connectors of the servers this machine joined.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetPolicyGuard(string deviceId, Func<IEnumerable<FleetConnector>> connectors)
        : this(deviceId, Sources(connectors))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FleetPolicyGuard"/> class over the connectors of the servers of a
    /// machine whose device is read only once a policy has a stop to check against it: a machine with no server makes no
    /// device key for its guard (<c>doc/fleet/security-and-privacy.md</c>, S1).
    /// </summary>
    /// <param name="deviceId">Gives the device of this machine, when a stop is checked.</param>
    /// <param name="connectors">Gives the connectors of the servers this machine joined.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetPolicyGuard(Func<string> deviceId, Func<IEnumerable<FleetConnector>> connectors)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _deviceId = deviceId;
        _sources = Sources(connectors);
    }

    /// <summary>Initializes a new instance of the <see cref="FleetPolicyGuard"/> class over policies as they were verified.</summary>
    /// <param name="deviceId">The device of this machine.</param>
    /// <param name="sources">Gives the policy of each server, with its access document and its spaces.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetPolicyGuard(string deviceId, Func<IEnumerable<FleetPolicySource>> sources)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(sources);
        _deviceId = () => deviceId;
        _sources = sources;
    }

    /// <summary>Gets the first stop that reaches this machine; null while its work goes on.</summary>
    public FleetPolicyHalt? Halt
    {
        get
        {
            foreach (var source in _sources())
            {
                if (source.Policy is { Stop: { } stop } policy && policy.Stops(_deviceId(), source.Access))
                    return new FleetPolicyHalt(source.Server, stop, policy.Number, policy.Time);
            }

            return null;
        }
    }

    /// <summary>Gets the rules that apply to the work of the fleet in a space: those of the policy of its server; null for none.</summary>
    /// <param name="spaceId">The space; null for the default one.</param>
    /// <returns>The rules, or null.</returns>
    public FleetPolicyRules? RulesFor(string? spaceId)
    {
        foreach (var source in _sources())
        {
            if (source.LinksSpace(spaceId) && source.Policy is { Number: > 0 } policy) return policy.Rules;
        }

        return null;
    }

    private static Func<IEnumerable<FleetPolicySource>> Sources(Func<IEnumerable<FleetConnector>> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        return () => connectors().Select(static connector => new FleetPolicySource(FleetServerAddress.Format(connector.ServerAddress), connector.Policy?.Current,
            connector.Access?.Current, connector.LinksSpace));
    }

    /// <summary>Says why a provider or a model breaks rules; null when they hold.</summary>
    /// <param name="rules">The rules.</param>
    /// <param name="providerType">The kind of the provider.</param>
    /// <param name="providerName">The name of the provider, for people.</param>
    /// <param name="modelId">The model, or null when not chosen yet.</param>
    /// <returns>The refusal, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    public static string? Refusal(FleetPolicyRules rules, string? providerType, string providerName, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.AllowsProvider(providerType))
            return $"The policy of the organization allows only these kinds of providers: {string.Join(", ", rules.Providers!)}; '{providerName}' is {providerType ?? "of no known kind"}.";
        if (!rules.AllowsModel(modelId)) return $"The policy of the organization allows only these models: {string.Join(", ", rules.Models!)}; not '{modelId}'.";
        return null;
    }

    /// <summary>Gets the narrower of two permission modes, by the order of <see cref="FleetPolicyDocuments.PermissionModes"/>; null when both are.</summary>
    /// <param name="left">A mode, or null for no limit.</param>
    /// <param name="right">Another mode, or null for no limit.</param>
    /// <returns>The narrower one.</returns>
    public static string? NarrowerMode(string? left, string? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        int Rank(string mode) => FleetPolicyDocuments.PermissionModes.IndexOf(mode) is var index and >= 0 ? index : 0;
        return Rank(left) <= Rank(right) ? left : right;
    }
}

file static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (string.Equals(list[index], value, StringComparison.Ordinal)) return index;
        }

        return -1;
    }
}
