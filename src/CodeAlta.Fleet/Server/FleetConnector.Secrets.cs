namespace CodeAlta.Fleet.Server;

// The values a server holds for this machine: the server says when they changed, and the machine reads its copies again.
public sealed partial class FleetConnectorOptions
{
    /// <summary>
    /// Gets what is told that a value this machine holds or may hold changed at the server, an environment of its person
    /// changed, or an approval of one of its runs was given (<see cref="Protocol.Api.FleetSecretMethods.Changed"/>): it reads
    /// its copies again (<see cref="FleetSecretSync"/>), off the reader of the connection. Null ignores it.
    /// </summary>
    public Action? SecretsChanged { get; init; }
}
