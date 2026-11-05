namespace CodeAlta.Fleet.Server;

/// <summary>
/// The server links of the machine do not read now (<see cref="FleetServerConnections.ReadConnections"/>): the budgets and the stops of
/// their servers are not known, and what they decide holds.
/// </summary>
public sealed class FleetServerLinksException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetServerLinksException"/> class.</summary>
    /// <param name="message">Why the links do not read.</param>
    public FleetServerLinksException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FleetServerLinksException"/> class.</summary>
    /// <param name="message">Why the links do not read.</param>
    /// <param name="innerException">The failure of the read.</param>
    public FleetServerLinksException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
