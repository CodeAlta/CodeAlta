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

    /// <summary>
    /// Gets what is told that the budgets, allowances or holds of this device's person changed at the server, or that the
    /// person has a new notification (<see cref="Protocol.Api.FleetBudgetMethods"/>): a machine reads its budgets again and a
    /// held run its hold; a desktop reads its notifications. Null ignores it.
    /// </summary>
    public Action? BudgetsChanged { get; init; }

    /// <summary>
    /// Gets what reads the problems of the machine that its heartbeat reports (<see cref="Protocol.Api.FleetHealthKinds.Reported"/>):
    /// the server turns those that last into notifications for its owner. Null reports nothing (a desktop).
    /// </summary>
    public Func<IReadOnlyList<Protocol.Api.FleetHealthProblem>>? Health { get; init; }
}
