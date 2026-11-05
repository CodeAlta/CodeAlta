using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// What a node checks before a run that names an environment starts (<c>doc/fleet/secrets.md</c>, environments protect
/// themselves): the environment is one of the owner of the machine, as a controller device of that person signed it; the
/// machine has one of its labels; the run is of an automation it allows; and, when it requires one, an approver approved
/// this run, with a signature of a controller device of that approver bound to this environment and version, this machine,
/// this run and the nonce this node chose, and valid now. An approval is taken once. The node decides: the server only
/// relays the request and the signed approval.
/// </summary>
internal sealed class NodeEnvironmentGate
{
    /// <summary>How long a run waits for its approval by default.</summary>
    public static readonly TimeSpan DefaultApprovalWait = TimeSpan.FromHours(1);

    private const int MaximumUsed = 2048;

    private readonly NodeFleetSecrets _secrets;
    private readonly string _usedPath;
    private readonly string _fleetRoot;
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="NodeEnvironmentGate"/> class.</summary>
    /// <param name="secrets">The values the servers give the node, with the environments they verified.</param>
    /// <param name="catalog">The catalog of the profile: the labels of the machine and the approvals it took are in its fleet folder.</param>
    public NodeEnvironmentGate(NodeFleetSecrets secrets, CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(catalog);
        _secrets = secrets;
        _usedPath = Path.Combine(catalog.FleetRoot, "approvals-used.json");
        _fleetRoot = catalog.FleetRoot;
    }

    /// <summary>Gets or sets how long a run waits for its approval.</summary>
    public TimeSpan ApprovalWait { get; set; } = DefaultApprovalWait;

    /// <summary>Gets or sets how often a run that waits reads its approval again when the server says nothing.</summary>
    public TimeSpan ApprovalPoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Admits a run to an environment, or tells why not.</summary>
    /// <param name="environment">The name of the environment.</param>
    /// <param name="runId">The run.</param>
    /// <param name="automationId">The automation of the run; null for a session.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What admitted the run, or why it may not start.</returns>
    /// <remarks>
    /// The approval asked names the run, never what it is about: a title is descriptive metadata the server never reads
    /// (<c>doc/fleet/security-and-privacy.md</c>, what the server sees).
    /// </remarks>
    public async Task<EnvironmentAdmissionResult> AdmitAsync(string environment, string runId, string? automationId, CancellationToken cancellationToken)
    {
        var problem = await AdmitCoreAsync(environment, runId, automationId, cancellationToken).ConfigureAwait(false);
        return problem.Admission is { } admission ? new EnvironmentAdmissionResult(null, admission) : EnvironmentAdmissionResult.Refused(problem.Problem!);
    }

    private async Task<(string? Problem, EnvironmentAdmission? Admission)> AdmitCoreAsync(string environment, string runId, string? automationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        // The newest rules and values before the run.
        await _secrets.SyncAsync(cancellationToken).ConfigureAwait(false);
        if (!_secrets.Environments().TryGetValue(environment, out var found))
            return (_secrets.IsAmbiguous(environment)
                ? $"Several environments are named '{environment}': a run is admitted to none of them by that name."
                : $"The environment '{environment}' is not an environment of the owner of this machine that a controller device signed.", null);
        var (definition, sync) = found;
        if (definition.Automations.Count > 0 && (automationId is null || !definition.Automations.Contains(automationId, StringComparer.Ordinal)))
            return ($"The environment '{environment}' is open to some automations only, not to this run.", null);
        if (definition.MachineLabels.Count > 0 && !definition.MachineLabels.Intersect(FleetMachineLabels.Read(_fleetRoot), StringComparer.OrdinalIgnoreCase).Any())
            return ($"The environment '{environment}' runs on machines labeled {string.Join(", ", definition.MachineLabels)}: this machine is not.", null);
        if (definition.Approvers.Count == 0)
            return (null, new EnvironmentAdmission(environment, definition.EnvironmentId, definition.Number, null) { ScopeId = FleetSecretSync.ScopeOf(definition) });

        // The nonce carries this machine's signature of what it asks: an approver signs nothing the server made up or changed.
        var nonce = sync.ApprovalNonce(definition, runId, automationId);
        FleetApprovalDto asked;
        try
        {
            asked = await sync.RequestApprovalAsync(definition.EnvironmentId, new RequestFleetApprovalRequest(runId, nonce, definition.Number, automationId, Title: null), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException)
        {
            return ($"The approval of the run could not be asked: {exception.Message}", null);
        }

        Log($"The run {runId} waits for the approval of the environment '{environment}'.");
        var deadline = DateTimeOffset.UtcNow + ApprovalWait;
        while (true)
        {
            var signal = _secrets.ApprovalSignal;
            FleetApprovalDto read;
            try
            {
                read = await sync.GetApprovalAsync(asked.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                read = asked;
            }

            if (read.Approval is { } signed)
            {
                FleetDeviceListState list;
                try
                {
                    list = await sync.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FleetServerException or HttpRequestException or FleetDeviceListException)
                {
                    return ($"The approval could not be checked against the device list: {exception.Message}", null);
                }

                if (!FleetEnvironmentDocuments.TryVerifyApproval(signed, definition, list, sync.MachineId, runId, nonce, DateTimeOffset.UtcNow, out var approval, out var reason))
                    return ($"The approval of the run was refused: {reason}.", null);
                if (!TakeOnce(approval.ApprovalId)) return ("The approval of the run was used already.", null);
                Log($"The run {runId} was approved for the environment '{environment}' by {approval.ApproverPersonId}.");
                return (null, new EnvironmentAdmission(environment, definition.EnvironmentId, definition.Number, approval.ApprovalId) { ScopeId = FleetSecretSync.ScopeOf(definition) });
            }

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return ($"No approver of the environment '{environment}' approved the run in time.", null);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(left < ApprovalPoll ? left : ApprovalPoll);
            try { await signal.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    // An approval is taken once, also across restarts of the node.
    private bool TakeOnce(string approvalId)
    {
        lock (_gate)
        {
            List<string> used;
            try { used = JsonSerializer.Deserialize(File.ReadAllBytes(_usedPath), NodeEnvironmentJsonContext.Default.ListString) ?? []; }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException) { used = []; }
            if (used.Contains(approvalId, StringComparer.Ordinal)) return false;
            used.Add(approvalId);
            if (used.Count > MaximumUsed) used.RemoveRange(0, used.Count - MaximumUsed);
            Directory.CreateDirectory(Path.GetDirectoryName(_usedPath)!);
            var staging = _usedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(used, NodeEnvironmentJsonContext.Default.ListString));
            File.Move(staging, _usedPath, overwrite: true);
            return true;
        }
    }

    private static void Log(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node.Secrets").Info(message);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class NodeEnvironmentJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
