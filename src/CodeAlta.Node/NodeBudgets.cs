using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Node;

/// <summary>
/// The budgets of a node (<c>doc/fleet/observability.md</c>, budgets and alerts): the gate every run passes before it starts,
/// over the servers the node joined, with what the node spent counted from its own statistics; the budgets and allowances of each
/// server are kept in its link folder, so a node offline or restarted spends what it holds and no more.
/// </summary>
internal sealed class NodeBudgets
{
    /// <summary>The name of the file of the budgets in the folder of a link.</summary>
    public const string FileName = "budgets.json";

    private readonly RepositoryLocationCache _repositories = new();

    /// <summary>Initializes a new instance of the <see cref="NodeBudgets"/> class.</summary>
    /// <param name="connections">The servers the node joined.</param>
    /// <param name="sessions">The sessions of the node, with their space and project.</param>
    /// <param name="statistics">The statistics the node counts.</param>
    public NodeBudgets(FleetServerConnections connections, IFleetPublicationSource sessions, IFleetStatisticsSource statistics)
    {
        ArgumentNullException.ThrowIfNull(connections);
        Gate = new FleetBudgetGate(() => [.. connections.Connections.Where(static connection => connection.Connector is not null)
            .Select(connection => new FleetBudgetLink(connection.Link.Id, connection.Connector!.Devices, connection.Connector.MetadataKeys,
                Path.Combine(connections.Store.LinkFolder(connection.Link.Id), FileName)))], new FleetBudgetLocalSpend(sessions, statistics));
    }

    /// <summary>Gets the gate.</summary>
    public FleetBudgetGate Gate { get; }

    /// <summary>Wakes the runs that wait: a server said that budgets or holds changed.</summary>
    public void Changed() => Gate.Changed();

    /// <summary>Admits a run, or tells why it may not start; it may wait for the owner when a budget holds its runs.</summary>
    /// <param name="run">The run.</param>
    /// <param name="held">What is told once the run waits; null for nothing.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The admission.</returns>
    public async Task<FleetBudgetAdmission> AdmitAsync(FleetNodeBudgetRun run, Action? held, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var identity = run.Project is { } project ? await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false) : null;
        return await Gate.AdmitAsync(new FleetBudgetRun(run.RunId, run.Title, run.SpaceId, identity), held, cancellationToken).ConfigureAwait(false);
    }
}
