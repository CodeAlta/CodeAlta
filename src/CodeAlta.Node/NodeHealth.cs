using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Automations;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Node;

/// <summary>
/// The problems a node reports in its heartbeat (<c>doc/fleet/observability.md</c>, health checks): little disk space left
/// where it keeps its state, a provider whose sign-in failed, an automation whose last runs failed, its own policy refusing
/// runs. The server turns those that last into a notification for the owner, once per problem; offline and a version behind
/// the server sees for itself. Names and numbers, never content.
/// </summary>
internal sealed class NodeHealth
{
    /// <summary>The space left under which the disk is low by default: 2 GiB.</summary>
    public const long DefaultMinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>How many runs of an automation in a row must fail before it is failing.</summary>
    public const int FailedRunsInARow = 2;

    /// <summary>How long a refusal of the policy of the node stays a problem.</summary>
    public static readonly TimeSpan PolicyRefusalWindow = TimeSpan.FromHours(1);

    private const int MaxProblems = 16;

    private readonly NodeHost _node;

    /// <summary>Initializes a new instance of the <see cref="NodeHealth"/> class.</summary>
    /// <param name="node">The running node.</param>
    public NodeHealth(NodeHost node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
    }

    /// <summary>Gets or sets the space left under which the disk is low.</summary>
    public long MinimumFreeBytes { get; set; } = DefaultMinimumFreeBytes;

    /// <summary>Reads the problems of the node now.</summary>
    /// <returns>The problems; empty when it has none.</returns>
    public IReadOnlyList<FleetHealthProblem> Read()
    {
        var problems = new List<FleetHealthProblem>();
        if (Disk() is { } disk) problems.Add(disk);

        // A provider of the node whose sign-in or start failed: its sessions would not run.
        foreach (var state in _node.Host.ModelProviderInitializationService.CurrentStates)
        {
            if (state.Descriptor.IsEnabled && state.Availability == ModelProviderAvailability.Failed)
                problems.Add(new FleetHealthProblem(FleetHealthKinds.SignInFailed, state.ProviderId.Value, state.ErrorCategory));
        }

        if (_node.Automations is { } automations)
        {
            foreach (var entry in automations.Engine.Snapshot.Entries)
            {
                var ended = automations.Engine.Runs(entry.Id, FailedRunsInARow + 2).Where(static run => run.Status is not (AutomationRun.Running or AutomationRun.Skipped))
                    .Take(FailedRunsInARow).ToArray();
                if (ended.Length == FailedRunsInARow && ended.All(static run => run.Status == AutomationRun.Failed))
                    problems.Add(new FleetHealthProblem(FleetHealthKinds.AutomationFailing, entry.Id));
            }

            if (automations.LastPolicyRefusal is { } refused && DateTimeOffset.UtcNow - refused < PolicyRefusalWindow)
                problems.Add(new FleetHealthProblem(FleetHealthKinds.PolicyRefusing, "automations"));
        }

        return [.. problems.Take(MaxProblems)];
    }

    // The disk of the state root: low under the minimum, or under a twentieth of its size.
    private FleetHealthProblem? Disk()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_node.Host.CatalogOptions.StateRoot));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            var free = drive.AvailableFreeSpace;
            if (free >= MinimumFreeBytes && free >= drive.TotalSize / 20) return null;
            return new FleetHealthProblem(FleetHealthKinds.DiskLow, null, (free / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
