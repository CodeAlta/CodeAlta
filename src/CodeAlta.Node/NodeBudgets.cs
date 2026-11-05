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
        _connections = connections;
        // Read again at each decision of the gate: links that do not read throw, and the run it decides for does not start.
        Gate = new FleetBudgetGate(() => [.. connections.ReadConnections().Where(static connection => connection.Connector is not null)
            .Select(connection => new FleetBudgetLink(connection.Link.Id, connection.Connector!.Devices, connection.Connector.MetadataKeys,
                Path.Combine(connections.Store.LinkFolder(connection.Link.Id), FileName))
            {
                Access = connection.Connector.Access, LinksSpace = connection.Connector.LinksSpace,
            })], new FleetBudgetLocalSpend(sessions, statistics));
    }

    private readonly FleetServerConnections _connections;

    /// <summary>Gets the gate.</summary>
    public FleetBudgetGate Gate { get; }

    /// <summary>Gets or sets how long a run waits for the first read of the links (their connectors, with the budgets they keep).</summary>
    public TimeSpan LoadWait { get; set; } = TimeSpan.FromSeconds(10);

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
        try
        {
            // The links of the node do not read, now or at their last reload: their budgets and stops are not known, and no run starts
            // as if none held it. The file is read here, not only by the reload, which may not have seen it yet.
            _connections.ReadConnections();
            if (!_connections.Loaded.IsCompleted)
            {
                // Not read once yet (a start): the connectors, and the budgets they keep, exist once it is.
                await Task.WhenAny(_connections.Loaded, Task.Delay(LoadWait, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_connections.Loaded.IsCompleted)
                    return Refused(_connections.LoadProblem ?? "The server links of this machine are not read yet");
            }

            var identity = run.Project is { } project ? await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false) : null;
            return await Gate.AdmitAsync(new FleetBudgetRun(run.RunId, run.Title, run.SpaceId, identity)
            {
                Nonce = run.Nonce, PersonId = run.PersonId, PersonOrganizationId = run.PersonOrganizationId,
            }, held, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerLinksException exception)
        {
            return Refused(exception.Message);
        }

        static FleetBudgetAdmission Refused(string problem) => new($"{problem}: the run does not start.", []);
    }
}
