namespace CodeAlta.Catalog;

/// <summary>
/// Who takes the lease of a session: the kind of process and the instance in it, written in the lease for the others to
/// read.
/// </summary>
public sealed record SessionLeaseOwner
{
    /// <summary>The kind of the headless command line, <c>altacli</c>.</summary>
    public const string CliKind = "altacli";

    /// <summary>The kind of CodeAlta Desktop.</summary>
    public const string DesktopKind = "desktop";

    /// <summary>The kind of CodeAlta TUI.</summary>
    public const string TuiKind = "tui";

    private const int MaximumLength = 128;

    /// <summary>Initializes an owner.</summary>
    /// <param name="kind"><see cref="CliKind"/>, <see cref="DesktopKind"/>, <see cref="TuiKind"/> or another short name.</param>
    /// <param name="instanceId">What tells this instance from the others of its kind, such as an id made at its start.</param>
    /// <exception cref="ArgumentException">A value is empty, longer than 128 characters or holds a control character.</exception>
    public SessionLeaseOwner(string kind, string instanceId)
    {
        Kind = Check(kind, nameof(kind));
        InstanceId = Check(instanceId, nameof(instanceId));
    }

    /// <summary>Gets the kind of process.</summary>
    public string Kind { get; }

    /// <summary>Gets the instance in that process.</summary>
    public string InstanceId { get; }

    private static string Check(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"'{value}' is not a short name without control characters.", parameterName);
        }

        return trimmed;
    }
}

/// <summary>
/// What a held lease says of its holder. Its values are <see langword="null"/> when the lease is held but its content
/// could not be read, as for a holder that has just opened it.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="OwnerKind">The kind of process (<see cref="SessionLeaseOwner.Kind"/>).</param>
/// <param name="ProcessId">The id of the process.</param>
/// <param name="ProcessStartUtc">When the process started, which tells it from a later process with the same id.</param>
/// <param name="InstanceId">The instance in that process (<see cref="SessionLeaseOwner.InstanceId"/>).</param>
/// <param name="AcquiredUtc">When the lease was taken.</param>
public sealed record SessionLeaseHolder(
    string SessionId,
    string? OwnerKind,
    int? ProcessId,
    DateTimeOffset? ProcessStartUtc,
    string? InstanceId,
    DateTimeOffset? AcquiredUtc);
