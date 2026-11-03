using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Control;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>What an <see cref="UpdateCampaignService"/> drives besides the store and the connections.</summary>
internal sealed record UpdateCampaignOptions
{
    /// <summary>Gets how often the campaigns move on without an event.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the limits of a campaign.</summary>
    public UpdateCampaignLimits Limits { get; init; } = new();

    /// <summary>Gets how often an automatic policy reads its feed.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Gets what updates the server itself (its self-update's <c>ApplyAsync</c>); null when it cannot.</summary>
    public Func<UpdateRequest, string, CancellationToken, Task<StagedUpdate?>>? ApplyServerUpdate { get; init; }

    /// <summary>Gets the runtime of the server, whose package hash a manifest must sign; null for this process's.</summary>
    public string? ServerRuntime { get; init; }

    /// <summary>Gets what reads the latest stable version every product lists on a feed; null for the feed itself.</summary>
    public Func<string, CancellationToken, Task<string?>>? ReadLatest { get; init; }
}

/// <summary>
/// The coordinated update of the fleet (<c>doc/fleet/deployment.md</c>, updates): an owner's controller device signs a
/// campaign; the server stores it, asks its nodes a bounded number at a time over their control connection, waits until
/// each node online runs the version (or failed, or rolled back), then updates itself, last, after checking the same
/// signature. A node offline is asked when it reconnects. The update policy of the fleet (manual, pinned, automatic)
/// is signed the same way; an automatic one reads its feed daily and starts a campaign at the maintenance window.
/// </summary>
internal sealed class UpdateCampaignService : IAsyncDisposable
{
    private const int RecentCampaigns = 20;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly ICoordinatorStore _store;
    private readonly PresenceRegistry _presence;
    private readonly DirectoryHub _directory;
    private readonly ServerUpdateStatusSource _server;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly UpdateCampaignOptions _options;
    private readonly UpdateCampaignEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="UpdateCampaignService"/> class; <see cref="Start"/> runs it.</summary>
    public UpdateCampaignService(ICoordinatorStore store, PresenceRegistry presence, DirectoryHub directory, ServerUpdateStatusSource server, TimeProvider time,
        ILogger logger, UpdateCampaignOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _presence = presence;
        _directory = directory;
        _server = server;
        _time = time;
        _logger = logger;
        _options = options;
        _engine = new UpdateCampaignEngine(options.Limits);
    }

    /// <summary>Starts moving the campaigns on.</summary>
    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Moves the campaigns on soon (a device connected, a report came).</summary>
    public void Nudge() => _wake.Writer.TryWrite(true);

    /// <summary>Gets the updates of the fleet: the server's versions, the policy and the recent campaigns; for a controller device.</summary>
    public async Task<UpdatesDto> GetAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        RequireController(actor);
        var organization = actor.OrganizationId;
        var campaigns = await _store.ListUpdateCampaignsAsync(organization, RecentCampaigns, cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(organization, cancellationToken).ConfigureAwait(false);
        var views = new List<UpdateCampaignDto>(campaigns.Count);
        foreach (var campaign in campaigns) views.Add(await ToDtoAsync(campaign, names, cancellationToken).ConfigureAwait(false));
        var policy = await _store.GetUpdatePolicyAsync(organization, cancellationToken).ConfigureAwait(false);
        var next = await _store.GetLastUpdateSequenceAsync(organization, cancellationToken).ConfigureAwait(false) + 1;
        return new UpdatesDto(_server.Read(), PolicyDto(policy), views, next);
    }

    /// <summary>Gets a campaign; for a controller device.</summary>
    public async Task<UpdateCampaignDto> GetCampaignAsync(DeviceRecord actor, string campaignId, CancellationToken cancellationToken)
    {
        RequireController(actor);
        var campaign = await FindAsync(actor, campaignId, cancellationToken).ConfigureAwait(false);
        return await ToDtoAsync(campaign, await NamesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a campaign with a manifest the calling owner's controller device signed.</summary>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, the manifest does not hold, or another campaign runs.</exception>
    public async Task<UpdateCampaignDto> CreateAsync(DeviceRecord actor, CreateUpdateCampaignRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await RequireOwnerControllerAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.Manifest is not { Payload.Length: > 0, Signature.Length: > 0 } signed) throw Invalid("The request needs a manifest signed by the calling device.");
        if (!FleetUpdateDocuments.TryParseManifest(signed.Payload, out var manifest, out var reason)) throw Invalid($"The manifest does not hold: {reason}.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) || !key.Verify(FleetUpdateDocuments.GetManifestSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The signature of the manifest does not verify with the key of the calling device.");
        if (!string.Equals(manifest.SignerDeviceId, actor.Id, StringComparison.Ordinal)) throw Invalid("The manifest must name the device that signs it.");
        if (!string.Equals(manifest.OrganizationId, actor.OrganizationId, StringComparison.Ordinal)) throw Invalid("The manifest must name the organization of the server.");
        if (!UpdateVersion.TryParse(manifest.TargetVersion, out var target)) throw Invalid($"'{manifest.TargetVersion}' is not a version.");
        var runtime = ServerRuntime();
        if (runtime is not null && manifest.HashFor(UpdateProduct.Server.PackageId, runtime) is null)
            throw Invalid($"The manifest signs no hash of {UpdateProduct.Server.RuntimePackageId(runtime)}, the package of this server.");

        var organization = actor.OrganizationId;
        if (await _store.GetUpdatePolicyAsync(organization, cancellationToken).ConfigureAwait(false) is { } held
            && FleetUpdateDocuments.TryParsePolicy(held.Payload, out var policy, out _) && policy.Mode == FleetUpdateModes.Pinned
            && !UpdateCampaignEngine.Same(policy.PinnedVersion, manifest.TargetVersion))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"The fleet is pinned to {policy.PinnedVersion}: change its policy first.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recent = await _store.ListUpdateCampaignsAsync(organization, RecentCampaigns, cancellationToken).ConfigureAwait(false);
            if (recent.FirstOrDefault(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State)) is { } running)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"Campaign {running.Sequence} to {running.TargetVersion} runs: cancel it first, or wait for its end.");
            var last = await _store.GetLastUpdateSequenceAsync(organization, cancellationToken).ConfigureAwait(false);
            if (manifest.Sequence <= last)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"The manifest must be numbered after campaign {last}: read the updates again.");
            if (recent.Any(campaign => campaign.Id == manifest.CampaignId)) throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "A campaign has this id.");

            var now = _time.GetUtcNow();
            var nodes = (await _store.ListDevicesAsync(organization, cancellationToken).ConfigureAwait(false))
                .Where(device => device.Kind == FleetDeviceKinds.Node && device.Status == DeviceStatuses.Approved && manifest.Targets(device.Id)).ToArray();
            var campaign = new UpdateCampaignRecord(manifest.CampaignId, organization, manifest.Sequence, false, target.ToString(), manifest.Feed, manifest.Rollback,
                manifest.NotBefore?.ToUniversalTime(), manifest.TimeZone, signed.Payload, signed.Signature, actor.Id,
                FleetUpdateCampaignStates.Scheduled, FleetUpdateMachineStates.Pending, null, now, now);
            var machines = nodes.Select(device => new UpdateCampaignMachineRecord(campaign.Id, device.Id, FleetUpdateMachineStates.Pending, null, null, null, now)).ToArray();
            var audit = origin.Audit("update.campaign.create", actor, campaign.Id, details: Details(campaign, machines.Length));
            if (!await _store.CreateUpdateCampaignAsync(campaign, machines, audit, cancellationToken).ConfigureAwait(false))
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "Another campaign has this sequence: read the updates again.");
            _logger.LogInformation("Update campaign {Sequence} to {Version} from {Feed} for {Count} node(s), {When}, signed by {Device}", campaign.Sequence, campaign.TargetVersion,
                campaign.Feed, machines.Length, campaign.NotBefore is { } start ? $"at {start:O}" : "now", actor.Id);
            Changed(campaign);
            Nudge();
            return await ToDtoAsync(campaign, await NamesAsync(organization, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cancels a campaign before the server's step; for an owner's controller device.</summary>
    /// <exception cref="CoordinatorException">The caller may not, no campaign has this id, or it is too late.</exception>
    public async Task<UpdateCampaignDto> CancelAsync(DeviceRecord actor, string campaignId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await RequireOwnerControllerAsync(actor, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaign = await FindAsync(actor, campaignId, cancellationToken).ConfigureAwait(false);
            var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
            if (UpdateCampaignEngine.Cancel(campaign, machines, _time.GetUtcNow()) is not { } cancelled)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"The campaign is {campaign.State}: it can be cancelled only before the server's step.");
            await _store.SaveUpdateCampaignAsync(cancelled.Campaign, cancelled.Machines, origin.Audit("update.campaign.cancel", actor, campaign.Id), cancellationToken)
                .ConfigureAwait(false);
            foreach (var device in cancelled.Asked)
            {
                if (_presence.Get(device) is { } session)
                    _ = SendAsync(session, FleetServerMethods.UpdateCancel, JsonSerializer.SerializeToElement(new FleetUpdateCancel(campaign.Id), FleetServerJsonContext.Default.FleetUpdateCancel));
            }

            _logger.LogInformation("Update campaign {Sequence} was cancelled by {Device}", campaign.Sequence, actor.Id);
            Changed(cancelled.Campaign);
            return await ToDtoAsync(cancelled.Campaign, await NamesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sets the update policy with one the calling owner's controller device signed, numbered after the one held.</summary>
    /// <exception cref="CoordinatorException">The caller may not, the policy does not hold, or it is not newer.</exception>
    public async Task<UpdatePolicyDto> SetPolicyAsync(DeviceRecord actor, SetUpdatePolicyRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await RequireOwnerControllerAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.Policy is not { Payload.Length: > 0, Signature.Length: > 0 } signed) throw Invalid("The request needs a policy signed by the calling device.");
        if (!FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var policy, out var reason)) throw Invalid($"The policy does not hold: {reason}.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) || !key.Verify(FleetUpdateDocuments.GetPolicySignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The signature of the policy does not verify with the key of the calling device.");
        if (!string.Equals(policy.SignerDeviceId, actor.Id, StringComparison.Ordinal)) throw Invalid("The policy must name the device that signs it.");
        if (!string.Equals(policy.OrganizationId, actor.OrganizationId, StringComparison.Ordinal)) throw Invalid("The policy must name the organization of the server.");
        if (policy.PinnedVersion is { } pinned && !UpdateVersion.TryParse(pinned, out _)) throw Invalid($"'{pinned}' is not a version.");
        if (policy.Feed is { } feed)
        {
            try { UpdateFeeds.Normalize(feed); }
            catch (UpdateException exception) { throw Invalid(exception.Message); }
        }

        var record = new UpdatePolicyRecord(actor.OrganizationId, policy.Sequence, signed.Payload, signed.Signature, actor.Id, null, null, _time.GetUtcNow());
        var audit = origin.Audit("update.policy.set", actor, actor.OrganizationId,
            details: $"{{\"seq\":{policy.Sequence},\"mode\":\"{policy.Mode}\"}}");
        if (!await _store.SetUpdatePolicyAsync(record, audit, cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "The policy must be numbered after the one held: read it again.");
        _logger.LogInformation("The update policy {Sequence} of the fleet is {Mode}, set by {Device}", policy.Sequence, policy.Mode, actor.Id);
        _directory.Publish(actor.OrganizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Update, "policy"));
        Nudge();
        return PolicyDto(record);
    }

    /// <summary>Records where a node stands in a campaign.</summary>
    /// <param name="device">The node.</param>
    /// <param name="report">Its report.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once it is recorded.</returns>
    public async Task ReportAsync(DeviceRecord device, FleetUpdateReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrEmpty(report.CampaignId) || report.CampaignId.Length > 64 || !FleetUpdateMachineStates.IsReported(report.State)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaign = await _store.GetUpdateCampaignAsync(report.CampaignId, cancellationToken).ConfigureAwait(false);
            if (campaign is null || campaign.OrganizationId != device.OrganizationId) return;
            var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
            if (machines.FirstOrDefault(machine => machine.DeviceId == device.Id) is not { } machine) return;
            var applied = UpdateCampaignEngine.Apply(machine, report, _time.GetUtcNow());
            if (ReferenceEquals(applied, machine)) return;
            await _store.SaveUpdateCampaignAsync(campaign, [applied], null, cancellationToken).ConfigureAwait(false);
            if (applied.State != machine.State)
                _logger.LogInformation("Node {Device} in update campaign {Sequence}: {State} {Message}", device.Id, campaign.Sequence, applied.State, applied.Message);
            Changed(campaign);
        }
        finally
        {
            _gate.Release();
        }

        Nudge();
    }

    /// <summary>Sends a node that connects the instruction of a campaign it is in the middle of, so that it reports again.</summary>
    /// <param name="session">Its control connection.</param>
    /// <returns>A task that completes once it was sent or could not be.</returns>
    public async Task SendCurrentAsync(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Device.Kind != FleetDeviceKinds.Node) return;
        try
        {
            foreach (var campaign in await _store.ListUpdateCampaignsAsync(session.Device.OrganizationId, RecentCampaigns).ConfigureAwait(false))
            {
                if (campaign.State is not (FleetUpdateCampaignStates.UpdatingNodes or FleetUpdateCampaignStates.UpdatingServer or FleetUpdateCampaignStates.PartlyDone)) continue;
                var machine = (await _store.ListUpdateCampaignMachinesAsync(campaign.Id).ConfigureAwait(false)).FirstOrDefault(item => item.DeviceId == session.Device.Id);
                if (machine is null || FleetUpdateMachineStates.IsTerminal(machine.State)) continue;
                if (machine.State != FleetUpdateMachineStates.Pending) await InstructAsync(session, campaign).ConfigureAwait(false);
                break;
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogDebug("The update instruction was not sent to {Device}: {Reason}", session.Device.Id, exception.Message);
        }

        Nudge();
    }

    /// <summary>Moves every campaign on once, and checks the feed of an automatic policy when due. The loop calls it; tests call it directly.</summary>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>A task that completes once the step is done.</returns>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (await _store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false) is not { } organization) return;
        await CheckPolicyAsync(organization.Id, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaigns = await _store.ListUpdateCampaignsAsync(organization.Id, RecentCampaigns, cancellationToken).ConfigureAwait(false);
            var moving = campaigns.Where(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State) || campaign.State == FleetUpdateCampaignStates.PartlyDone).ToArray();
            if (moving.Length == 0) return;
            var inventory = (await _store.ListMachinesAsync(organization.Id, cancellationToken).ConfigureAwait(false))
                .ToDictionary(static machine => machine.DeviceId, static machine => machine.ReadInventory()?.AppVersion, StringComparer.Ordinal);
            var status = _server.Read();
            var server = new UpdateCampaignServer(status.Running, status.LastOutcome);
            // The oldest first: a newer campaign has superseded the machines both name.
            foreach (var campaign in moving.Reverse())
                await StepAsync(campaign, inventory, server, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StepAsync(UpdateCampaignRecord campaign, Dictionary<string, string?> inventory, UpdateCampaignServer server, CancellationToken cancellationToken)
    {
        var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var step = _engine.Step(campaign, machines, NodeView, server, now);
        if (!step.Changed(campaign)) return;
        var next = step.Campaign;
        await _store.SaveUpdateCampaignAsync(next, step.Machines, null, cancellationToken).ConfigureAwait(false);
        if (next.State != campaign.State) _logger.LogInformation("Update campaign {Sequence} to {Version}: {State}", next.Sequence, next.TargetVersion, next.State);
        foreach (var device in step.Instruct)
        {
            if (_presence.Get(device) is { } session) await InstructAsync(session, next).ConfigureAwait(false);
        }

        if (step.UpdateServer) next = await UpdateServerAsync(next, cancellationToken).ConfigureAwait(false);
        Changed(next);

        UpdateCampaignNode NodeView(string deviceId)
        {
            var session = _presence.Get(deviceId);
            var running = session?.GetPresence().Update?.Running;
            if (string.IsNullOrEmpty(running)) inventory.TryGetValue(deviceId, out running);
            return new UpdateCampaignNode(session is not null, running);
        }
    }

    // The server's own step: the same checks as a node (the signature of a controller of the device list it holds, a
    // newer version unless a person chose a rollback, the hash of its own package), then its self-update.
    private async Task<UpdateCampaignRecord> UpdateServerAsync(UpdateCampaignRecord campaign, CancellationToken cancellationToken)
    {
        string? failure;
        try
        {
            failure = await ApplyServerAsync(campaign, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException exception)
        {
            failure = exception.Message;
        }

        var now = _time.GetUtcNow();
        if (failure is null)
        {
            _logger.LogInformation("Update campaign {Sequence}: the nodes are done; CodeAlta Server switches to {Version}", campaign.Sequence, campaign.TargetVersion);
            return campaign;
        }

        _logger.LogWarning("Update campaign {Sequence}: the server did not update: {Reason}", campaign.Sequence, failure);
        var failed = campaign with { State = FleetUpdateCampaignStates.Failed, ServerState = FleetUpdateMachineStates.Failed, ServerMessage = failure, UpdatedAt = now };
        await _store.SaveUpdateCampaignAsync(failed, [], null, cancellationToken).ConfigureAwait(false);
        return failed;
    }

    private async Task<string?> ApplyServerAsync(UpdateCampaignRecord campaign, CancellationToken cancellationToken)
    {
        if (_options.ApplyServerUpdate is not { } apply) return "This server does not update itself (it does not run under its launcher).";
        if (ServerRuntime() is not { } runtime) return "No package of CodeAlta Server is published for this server's OS and architecture.";
        if (!UpdateVersion.TryParse(_server.Read().Running, out var running)) return "The version of this server is not known.";
        var entries = await _store.ListDeviceListAsync(campaign.OrganizationId, cancellationToken).ConfigureAwait(false);
        FleetDeviceListState devices;
        try
        {
            devices = FleetDeviceListVerifier.VerifyOrThrow(entries.Select(static entry => new FleetSignedDeviceListEntry(entry.Payload, entry.Signature)), campaign.OrganizationId);
        }
        catch (FleetDeviceListException exception)
        {
            return $"The device list of this server does not verify: {exception.Message}";
        }

        var context = new UpdateInstructionContext
        {
            Devices = devices, OrganizationId = campaign.OrganizationId, Product = UpdateProduct.Server, Runtime = runtime, Running = running,
        };
        if (!UpdateInstructions.TryVerify(Instruction(campaign), context, out var update, out var reason)) return $"The server refused the campaign: {reason}.";
        var request = update.ToRequest() with { StableOnly = update.Automatic };
        await apply(request, $"fleet campaign {campaign.Sequence} ({campaign.Id})", cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task CheckPolicyAsync(string organizationId, CancellationToken cancellationToken)
    {
        var held = await _store.GetUpdatePolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (held is null || !FleetUpdateDocuments.TryParsePolicy(held.Payload, out var policy, out _) || policy.Mode != FleetUpdateModes.Automatic) return;
        var now = _time.GetUtcNow();
        if (held.LastCheckAt is { } last && now - last < _options.CheckInterval) return;
        string message;
        try
        {
            message = await CheckFeedAsync(held, policy, now, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException exception)
        {
            message = exception.Message;
        }

        await _store.SetUpdatePolicyCheckAsync(organizationId, now, message, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("The automatic update policy read its feed: {Message}", message);
        _directory.Publish(organizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Update, "policy"));
    }

    private async Task<string> CheckFeedAsync(UpdatePolicyRecord held, FleetUpdatePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var feed = policy.Feed ?? UpdateFeeds.NuGetOrg;
        var latestText = _options.ReadLatest is { } read
            ? await read(feed, cancellationToken).ConfigureAwait(false)
            : (await UpdateReleases.ListAsync(UpdateFeeds.Open(feed, _http), UpdateReleases.FleetProducts, false, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.ToString();
        if (!UpdateVersion.TryParse(latestText, out var latest)) return $"{feed} lists no stable version of the fleet.";
        var organization = held.OrganizationId;
        var campaigns = await _store.ListUpdateCampaignsAsync(organization, RecentCampaigns, cancellationToken).ConfigureAwait(false);
        if (campaigns.FirstOrDefault(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State)) is { } running)
            return $"{latest} is the latest; campaign {running.Sequence} to {running.TargetVersion} runs.";
        if (campaigns.Any(campaign => campaign.Automatic && UpdateCampaignEngine.Same(campaign.TargetVersion, latest.ToString())
                                      && campaign.State is not FleetUpdateCampaignStates.Cancelled))
            return $"{latest} is the latest; a campaign installed it already.";
        var nodes = (await _store.ListDevicesAsync(organization, cancellationToken).ConfigureAwait(false))
            .Where(static device => device.Kind == FleetDeviceKinds.Node && device.Status == DeviceStatuses.Approved).ToArray();
        var versions = (await _store.ListMachinesAsync(organization, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static machine => machine.DeviceId, static machine => machine.ReadInventory()?.AppVersion, StringComparer.Ordinal);
        var behind = nodes.Count(node => !versions.TryGetValue(node.Id, out var version) || !UpdateVersion.TryParse(version, out var parsed) || parsed < latest);
        var serverBehind = !UpdateVersion.TryParse(_server.Read().Running, out var serverVersion) || serverVersion < latest;
        if (behind == 0 && !serverBehind) return $"{latest} is the latest; the fleet runs it.";

        var start = FleetUpdateDocuments.NextWindow(policy, now);
        var id = "auto-" + Guid.NewGuid().ToString("N")[..12];
        var campaign = new UpdateCampaignRecord(id, organization, policy.Sequence, true, latest.ToString(), feed, false, start == now ? null : start, policy.TimeZone,
            held.Payload, held.Signature, null, FleetUpdateCampaignStates.Scheduled, FleetUpdateMachineStates.Pending, null, now, now);
        var machines = nodes.Select(node => new UpdateCampaignMachineRecord(id, node.Id, FleetUpdateMachineStates.Pending, null, null, null, now)).ToArray();
        await _store.CreateUpdateCampaignAsync(campaign, machines, new AuditEntry("update.campaign.create", AuditEntry.Success, null, null, id, null, null,
            $"{{\"automatic\":true,\"version\":\"{latest}\"}}"), cancellationToken).ConfigureAwait(false);
        Changed(campaign);
        Nudge();
        return $"{latest} is the latest: the fleet updates at {start:u}.";
    }

    private async Task InstructAsync(ControlSession session, UpdateCampaignRecord campaign)
        => await SendAsync(session, FleetServerMethods.UpdateInstruction,
            JsonSerializer.SerializeToElement(Instruction(campaign), FleetServerJsonContext.Default.FleetUpdateInstruction)).ConfigureAwait(false);

    private static FleetUpdateInstruction Instruction(UpdateCampaignRecord campaign)
        => campaign.Automatic
            ? new FleetUpdateInstruction(campaign.Id, null, new FleetSignedUpdateDocument(campaign.Payload, campaign.Signature), campaign.TargetVersion)
            : new FleetUpdateInstruction(campaign.Id, new FleetSignedUpdateDocument(campaign.Payload, campaign.Signature));

    private async Task SendAsync(ControlSession session, string name, JsonElement payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout);
            await session.Connection.SendEventAsync(new FleetEvent(name, payload), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            // The node gets it again when it connects, or when the campaign sends it again.
            _logger.LogDebug("{Event} was not sent to {Device}: {Reason}", name, session.Device.Id, exception.Message);
        }
    }

    private void Changed(UpdateCampaignRecord campaign)
        => _directory.Publish(campaign.OrganizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Update, campaign.Id));

    private async Task<UpdateCampaignDto> ToDtoAsync(UpdateCampaignRecord campaign, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
        return new UpdateCampaignDto(campaign.Id, campaign.Sequence, campaign.State, campaign.TargetVersion, campaign.Feed, campaign.Rollback, campaign.NotBefore,
            campaign.TimeZone, campaign.Automatic, campaign.CreatedByDeviceId, campaign.CreatedAt, campaign.UpdatedAt, campaign.ServerState, campaign.ServerMessage,
            [.. machines.Select(machine => new UpdateCampaignMachineDto(machine.DeviceId, names.GetValueOrDefault(machine.DeviceId, machine.DeviceId), machine.State,
                machine.Running, machine.Message, _presence.IsOnline(machine.DeviceId), machine.UpdatedAt))]);
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(string organizationId, CancellationToken cancellationToken)
        => (await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, static device => device.Name,
            StringComparer.Ordinal);

    private async Task<UpdateCampaignRecord> FindAsync(DeviceRecord actor, string campaignId, CancellationToken cancellationToken)
    {
        var campaign = string.IsNullOrEmpty(campaignId) || campaignId.Length > 64 ? null : await _store.GetUpdateCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);
        return campaign is not null && campaign.OrganizationId == actor.OrganizationId
            ? campaign
            : throw CoordinatorException.NotFound(CoordinatorErrorCodes.UpdateNotFound, "No campaign has this id.");
    }

    private static UpdatePolicyDto PolicyDto(UpdatePolicyRecord? record)
    {
        if (record is null) return new UpdatePolicyDto(null, FleetUpdateModes.Manual, null, null, null);
        var mode = FleetUpdateDocuments.TryParsePolicy(record.Payload, out var policy, out _) ? policy.Mode : FleetUpdateModes.Manual;
        return new UpdatePolicyDto(new FleetSignedUpdateDocument(record.Payload, record.Signature), mode, record.LastCheckAt, record.LastCheckMessage, record.UpdatedAt);
    }

    private string? ServerRuntime() => _options.ServerRuntime ?? UpdateProduct.CurrentRuntime();

    private static void RequireController(DeviceRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device reads the updates of the fleet.");
    }

    private async Task RequireOwnerControllerAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        RequireController(actor);
        var person = actor.OwnerPersonId is null ? null : await _store.GetPersonAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        if (person is null || person.Role != PersonRoles.Owner)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only an owner updates the fleet or sets its update policy.");
    }

    private static string Details(UpdateCampaignRecord campaign, int machines)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["seq"] = campaign.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), ["version"] = campaign.TargetVersion, ["feed"] = campaign.Feed,
            ["machines"] = machines.ToString(System.Globalization.CultureInfo.InvariantCulture), ["notBefore"] = campaign.NotBefore?.ToString("O") ?? "now",
            ["rollback"] = campaign.Rollback ? "true" : "false",
        }, ServerUpdateJsonContext.Default.DictionaryStringString);

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.UpdateInvalid, message);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await TickAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException or HttpRequestException
                or ObjectDisposedException)
            {
                _logger.LogWarning("The update campaigns did not move on: {Reason}", exception.Message);
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var wake = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
                await Task.WhenAny(wake, Task.Delay(_options.Interval, _time, linked.Token)).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);
                _wake.Reader.TryRead(out _);
                _stop.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The server stops it first, then its container disposes it again.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>The source-generated JSON of the audit details of the updates.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ServerUpdateJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
