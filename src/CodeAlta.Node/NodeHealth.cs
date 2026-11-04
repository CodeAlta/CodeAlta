using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Hosting;

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

    /// <summary>How long ahead a sign-in that does not renew itself is reported before it expires.</summary>
    public static readonly TimeSpan SignInWarning = TimeSpan.FromDays(3);

    /// <summary>How often the stored sign-ins of the providers are read again.</summary>
    public static readonly TimeSpan SignInReadInterval = TimeSpan.FromMinutes(10);

    private const int MaxProblems = 16;

    private readonly NodeHost _node;
    private IReadOnlyList<FleetHealthProblem> _signIns = [];
    private long _signInsReadAt = long.MinValue;
    private int _readingSignIns;

    /// <summary>Initializes a new instance of the <see cref="NodeHealth"/> class.</summary>
    /// <param name="node">The running node.</param>
    public NodeHealth(NodeHost node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
        SignInDefinitions = () => new CodeAltaConfigStore(node.Host.CatalogOptions).LoadGlobalProviderDefinitions();
        SignInStatus = (definition, token) => ConfiguredProviderLogin.GetStatusAsync(definition, node.Host.CatalogOptions.GlobalRoot, token);
    }

    /// <summary>Gets or sets the space left under which the disk is low.</summary>
    public long MinimumFreeBytes { get; set; } = DefaultMinimumFreeBytes;

    /// <summary>Gets or sets what lists the people who asked to use the machine and wait for its owner; null for none.</summary>
    public Func<IReadOnlyList<string>>? AccessRequests { get; set; }

    /// <summary>Gets or sets what lists the enabled providers whose sign-in is checked.</summary>
    public Func<IReadOnlyList<CodeAltaProviderDocument>> SignInDefinitions { get; set; }

    /// <summary>Gets or sets what reads the stored sign-in of a provider, without the network.</summary>
    public Func<CodeAltaProviderDocument, CancellationToken, Task<ProviderLoginStatus>> SignInStatus { get; set; }

    /// <summary>
    /// Reads the stored sign-ins of the providers with an account sign-in again: one that tells when it expires and does not
    /// renew itself is a problem from <see cref="SignInWarning"/> before (or once expired), its subject the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes once they were read.</returns>
    public async Task RefreshSignInsAsync(CancellationToken cancellationToken)
    {
        var problems = new List<FleetHealthProblem>();
        IReadOnlyList<CodeAltaProviderDocument> definitions;
        try { definitions = SignInDefinitions(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return; }
        var now = DateTimeOffset.UtcNow;
        foreach (var definition in definitions)
        {
            if (!ConfiguredProviderLogin.SupportsLogin(definition.ProviderType)) continue;
            ProviderLoginStatus status;
            try { status = await SignInStatus(definition, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or System.Text.Json.JsonException) { continue; }
            if (status is { SignedIn: true, Renews: false, ExpiresAt: { } expires } && expires - now <= SignInWarning)
                problems.Add(new FleetHealthProblem(FleetHealthKinds.SignIn, definition.ProviderKey, expires.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        Volatile.Write(ref _signIns, problems);
        Volatile.Write(ref _signInsReadAt, now.UtcTicks);
    }

    // The heartbeat reads files only off its thread: the sign-ins are read again in the background when due.
    private IReadOnlyList<FleetHealthProblem> SignIns()
    {
        if (DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _signInsReadAt) >= SignInReadInterval.Ticks && Interlocked.Exchange(ref _readingSignIns, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshSignInsAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { Volatile.Write(ref _readingSignIns, 0); }
            });
        }

        return Volatile.Read(ref _signIns);
    }

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

        // A sign-in about to expire that does not renew itself, read in the background.
        problems.AddRange(SignIns());

        // A person who asked to use the machine: its owner is told, and lets them in on the page of the machine.
        foreach (var person in AccessRequests?.Invoke() ?? []) problems.Add(new FleetHealthProblem(FleetHealthKinds.AccessRequest, person));

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
