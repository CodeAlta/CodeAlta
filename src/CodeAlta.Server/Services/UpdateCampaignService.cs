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

    /// <summary>Gets whether <see cref="UpdateCampaignService.Start"/> runs the loop; false leaves every step to <see cref="UpdateCampaignService.TickAsync"/> (tests).</summary>
    public bool RunLoop { get; init; } = true;

    /// <summary>Gets the limits of a campaign.</summary>
    public UpdateCampaignLimits Limits { get; init; } = new();

    /// <summary>Gets how often an automatic policy reads its feed.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Gets what updates the server itself (its self-update's <c>ApplyAsync</c>); null when it cannot.</summary>
    public Func<UpdateRequest, string, CancellationToken, Task<StagedUpdate?>>? ApplyServerUpdate { get; init; }

    /// <summary>
    /// Gets what withdraws the server's own switch that waits when a requester asked for it (a campaign a policy revokes),
    /// given the requester and why; it gives whether that switch waited and was withdrawn. Null for none.
    /// </summary>
    public Func<string, string, bool>? WithdrawServerSwitch { get; init; }

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
    // How many automatic campaigns to one version may fail (not roll back) before the policy stops trying it.
    private const int MaxAutomaticAttempts = 3;
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
    // Written under the gate only.
    private bool _serverStepsResumed;

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

    /// <summary>Starts moving the campaigns on; it does nothing when the options turn the loop off.</summary>
    public void Start()
    {
        if (_options.RunLoop) _loop = Task.Run(LoopAsync);
    }

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
        // Every creation and every change of the policy goes through the gate: the policy read here holds until the
        // campaign is stored, and no automatic campaign starts meanwhile.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _store.GetUpdatePolicyAsync(organization, cancellationToken).ConfigureAwait(false) is { } held
                && FleetUpdateDocuments.TryParsePolicy(held.Payload, out var policy, out _) && policy.Mode == FleetUpdateModes.Pinned
                && !UpdateCampaignEngine.Same(policy.PinnedVersion, manifest.TargetVersion))
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"The fleet is pinned to {policy.PinnedVersion}: change its policy first.");

            var outstanding = await _store.ListOutstandingUpdateCampaignsAsync(organization, cancellationToken).ConfigureAwait(false);
            if (outstanding.FirstOrDefault(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State)) is { } running)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"Campaign {running.Sequence} to {running.TargetVersion} runs: cancel it first, or wait for its end.");
            var last = await _store.GetLastUpdateSequenceAsync(organization, cancellationToken).ConfigureAwait(false);
            if (manifest.Sequence <= last)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, $"The manifest must be numbered after campaign {last}: read the updates again.");
            if (await _store.GetUpdateCampaignAsync(manifest.CampaignId, cancellationToken).ConfigureAwait(false) is not null)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "A campaign has this id.");

            var now = _time.GetUtcNow();
            var nodes = (await _store.ListDevicesAsync(organization, cancellationToken).ConfigureAwait(false))
                .Where(device => device.Kind == FleetDeviceKinds.Node && device.Status == DeviceStatuses.Approved && manifest.Targets(device.Id)).ToArray();
            var campaign = new UpdateCampaignRecord(manifest.CampaignId, organization, manifest.Sequence, false, target.ToString(), manifest.Feed, manifest.Rollback,
                manifest.NotBefore?.ToUniversalTime(), manifest.TimeZone, signed.Payload, signed.Signature, actor.Id,
                FleetUpdateCampaignStates.Scheduled, FleetUpdateMachineStates.Pending, null, now, now);
            var machines = nodes.Select(device => new UpdateCampaignMachineRecord(campaign.Id, device.Id, FleetUpdateMachineStates.Pending, null, null, null, now)).ToArray();
            var audit = origin.Audit("update.campaign.create", actor, campaign.Id, details: Details(campaign, machines.Length));
            if (!await _store.CreateUpdateCampaignAsync(campaign, machines, audit, cancellationToken).ConfigureAwait(false))
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "Another campaign runs or has this sequence: read the updates again.");
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
            // A node offline gets it when it connects again: the cancellation stays due until the node acknowledges it.
            foreach (var device in cancelled.Asked)
            {
                if (_presence.Get(device) is { } session) _ = SendCancelAsync(session, campaign);
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
        var revoked = new List<(UpdateCampaignRecord Campaign, IReadOnlyList<string> Asked)>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Once stored, the policy is applied whatever the caller does: the server never holds one policy and runs
            // campaigns only the one it replaced allowed.
            if (!await _store.SetUpdatePolicyAsync(record, audit, CancellationToken.None).ConfigureAwait(false))
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.UpdateConflict, "The policy must be numbered after the one held: read it again.");
            foreach (var campaign in await _store.ListOutstandingUpdateCampaignsAsync(actor.OrganizationId, CancellationToken.None).ConfigureAwait(false))
            {
                if (Disallowed(policy, campaign) is not { } why) continue;
                var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, CancellationToken.None).ConfigureAwait(false);
                // In the server's step, the server's own switch is withdrawn while it waits; one it took already ends as it ends.
                var switchWithdrawn = campaign.State == FleetUpdateCampaignStates.UpdatingServer && campaign.ServerState != FleetUpdateMachineStates.Pending
                    && WithdrawServerSwitch(campaign, why);
                if (UpdateCampaignEngine.Revoke(campaign, machines, _time.GetUtcNow(), why, switchWithdrawn) is not { } withdrawn) continue;
                await _store.SaveUpdateCampaignAsync(withdrawn.Campaign, withdrawn.Machines, origin.Audit("update.campaign.cancel", actor, campaign.Id,
                    details: $"{{\"policy\":{policy.Sequence}}}"), CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Update campaign {Sequence} to {Version} was withdrawn: {Reason}", campaign.Sequence, campaign.TargetVersion, why);
                revoked.Add((campaign, withdrawn.Asked));
                Changed(withdrawn.Campaign);
            }
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("The update policy {Sequence} of the fleet is {Mode}, set by {Device}", policy.Sequence, policy.Mode, actor.Id);
        // The nodes hold the newest policy themselves, and refuse what a policy it replaced would allow.
        foreach (var session in _presence.List(actor.OrganizationId))
        {
            if (session.Device.Kind != FleetDeviceKinds.Node) continue;
            var cancelled = revoked.Where(item => item.Asked.Contains(session.Device.Id, StringComparer.Ordinal)).Select(static item => item.Campaign).ToArray();
            _ = DeliverAsync(session, record, cancelled);
        }

        _directory.Publish(actor.OrganizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Update, "policy"));
        Nudge();
        return PolicyDto(record);
    }

    // Why a policy no longer allows a campaign, or null when it does: an automatic campaign holds only under the
    // automatic policy that started it; a pinned fleet takes no campaign to another version.
    private static string? Disallowed(FleetUpdatePolicy policy, UpdateCampaignRecord campaign)
    {
        if (campaign.Automatic)
        {
            return policy.Mode == FleetUpdateModes.Automatic && policy.Sequence == campaign.Sequence
                ? null
                : $"The update policy {policy.Sequence} ({policy.Mode}) replaced the automatic policy that started it.";
        }

        return policy.Mode == FleetUpdateModes.Pinned && !UpdateCampaignEngine.Same(policy.PinnedVersion, campaign.TargetVersion)
            ? $"The update policy {policy.Sequence} pins the fleet to {policy.PinnedVersion}."
            : null;
    }

    // Withdraws the switch the server's step of a campaign asked for, when it still waits; gives whether it did.
    private bool WithdrawServerSwitch(UpdateCampaignRecord campaign, string why)
    {
        if (_options.WithdrawServerSwitch is not { } withdraw) return false;
        try
        {
            return withdraw(ServerRequester(campaign), why);
        }
        catch (UpdateException exception)
        {
            _logger.LogWarning("Update campaign {Sequence}: the server's switch could not be withdrawn: {Reason}", campaign.Sequence, exception.Message);
            return false;
        }
    }

    // Who asks for the server's switch of a campaign, as its self-update records it (requestedBy).
    private static string ServerRequester(UpdateCampaignRecord campaign) => $"fleet campaign {campaign.Sequence} ({campaign.Id})";

    private async Task DeliverAsync(ControlSession session, UpdatePolicyRecord policy, IReadOnlyList<UpdateCampaignRecord> cancelled)
    {
        await SendPolicyAsync(session, policy).ConfigureAwait(false);
        foreach (var campaign in cancelled) await SendCancelAsync(session, campaign).ConfigureAwait(false);
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
        UpdateCampaignRecord? cancel = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaign = await _store.GetUpdateCampaignAsync(report.CampaignId, cancellationToken).ConfigureAwait(false);
            if (campaign is null || campaign.OrganizationId != device.OrganizationId) return;
            var machines = await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
            if (machines.FirstOrDefault(machine => machine.DeviceId == device.Id) is not { } machine) return;
            var applied = UpdateCampaignEngine.Apply(machine, report, _time.GetUtcNow());
            // A node that still works on a cancelled campaign did not get its cancellation: it gets it again.
            cancel = applied.CancelPending && !FleetUpdateMachineStates.IsTerminal(report.State) ? campaign : null;
            if (ReferenceEquals(applied, machine)) return;
            await _store.SaveUpdateCampaignAsync(campaign, [applied], null, cancellationToken).ConfigureAwait(false);
            if (applied.State != machine.State)
                _logger.LogInformation("Node {Device} in update campaign {Sequence}: {State} {Message}", device.Id, campaign.Sequence, applied.State, applied.Message);
            Changed(campaign);
        }
        finally
        {
            _gate.Release();
            if (cancel is not null && _presence.Get(device.Id) is { } session) _ = SendCancelAsync(session, cancel);
        }

        Nudge();
    }

    /// <summary>
    /// Sends a node that connects the update policy of the fleet, the cancellations it has not acknowledged, and the
    /// instruction of a campaign it is in the middle of, so that it reports again.
    /// </summary>
    /// <param name="session">Its control connection.</param>
    /// <returns>A task that completes once it was sent or could not be.</returns>
    public async Task SendCurrentAsync(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Device.Kind != FleetDeviceKinds.Node) return;
        try
        {
            // The policy first: the node judges what follows by the newest one.
            if (await _store.GetUpdatePolicyAsync(session.Device.OrganizationId).ConfigureAwait(false) is { } policy)
                await SendPolicyAsync(session, policy).ConfigureAwait(false);
            foreach (var cancelled in await _store.ListUpdateCancellationsAsync(session.Device.OrganizationId, session.Device.Id).ConfigureAwait(false))
            {
                if (await _store.GetUpdateCampaignAsync(cancelled).ConfigureAwait(false) is { } campaign) await SendCancelAsync(session, campaign).ConfigureAwait(false);
            }

            // Every campaign with work left, however many newer ones the history of the desktop shows.
            foreach (var campaign in await _store.ListOutstandingUpdateCampaignsAsync(session.Device.OrganizationId).ConfigureAwait(false))
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
        var feed = await ReadDueFeedAsync(organization.Id, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Under the gate, as a campaign a person starts: the automatic policy never starts a second active campaign.
            if (feed is not null) await CheckPolicyAsync(organization.Id, feed, cancellationToken).ConfigureAwait(false);
            var moving = await _store.ListOutstandingUpdateCampaignsAsync(organization.Id, cancellationToken).ConfigureAwait(false);
            if (!_serverStepsResumed && await ResumeServerStepsAsync(moving, cancellationToken).ConfigureAwait(false))
                moving = await _store.ListOutstandingUpdateCampaignsAsync(organization.Id, cancellationToken).ConfigureAwait(false);
            if (moving.Count == 0) return;
            var inventory = (await _store.ListMachinesAsync(organization.Id, cancellationToken).ConfigureAwait(false))
                .ToDictionary(static machine => machine.DeviceId, static machine => machine.ReadInventory()?.AppVersion, StringComparer.Ordinal);
            // A version that runs is confirmed only as the self-update reports it: before it started, or while its state
            // does not read, the server's step waits.
            var known = _server.TryRead(out var reported) || _options.ApplyServerUpdate is null;
            var status = reported ?? _server.Read();
            var server = new UpdateCampaignServer(status.Running, status.LastOutcome, status.Trial?.Version, known);
            // The oldest first: a newer campaign has superseded the machines both name.
            foreach (var campaign in moving.Reverse<UpdateCampaignRecord>())
                await StepAsync(campaign, inventory, server, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Once, at the first step whose self-update status is known (it starts after this service): the server's step a stop
    // interrupted before the self-update recorded its switch is taken again. Under the gate, a step asked and its
    // application happen together, so only a stop leaves one asked and not recorded. Gives whether a campaign changed.
    private async Task<bool> ResumeServerStepsAsync(IReadOnlyList<UpdateCampaignRecord> campaigns, CancellationToken cancellationToken)
    {
        FleetUpdateStatus? status = null;
        if (_options.ApplyServerUpdate is not null && !_server.TryRead(out status)) return false;
        _serverStepsResumed = true;
        // A server that does not update itself fails the step at once when it is taken again.
        status ??= _server.Read();
        var changed = false;
        foreach (var campaign in campaigns)
        {
            if (UpdateCampaignEngine.ResumeServerStep(campaign, status, _time.GetUtcNow()) is not { } resumed) continue;
            await _store.SaveUpdateCampaignAsync(resumed, [], null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Update campaign {Sequence}: a stop interrupted the server's step before its switch was recorded; it is taken again", campaign.Sequence);
            changed = true;
        }

        return changed;
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
    // newer version unless a person chose a rollback, the hash of its own package, the policy it holds now, and for an
    // automatic update the signed maintenance window), then its self-update.
    private async Task<UpdateCampaignRecord> UpdateServerAsync(UpdateCampaignRecord campaign, CancellationToken cancellationToken)
    {
        string? failure;
        DateTimeOffset? waitUntil = null;
        try
        {
            (failure, waitUntil) = await ApplyServerAsync(campaign, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The step was recorded as asked: it fails here rather than stay asked with nothing under way.
            failure = exception.Message;
        }

        var now = _time.GetUtcNow();
        if (waitUntil is { } start)
        {
            // Outside the window: the step is asked again at its next opening.
            _logger.LogInformation("Update campaign {Sequence}: CodeAlta Server waits for its maintenance window, at {Start:u}", campaign.Sequence, start);
            var waiting = campaign with
            {
                ServerState = FleetUpdateMachineStates.Pending, NotBefore = start, ServerMessage = $"The server waits for the maintenance window at {start:u}.", UpdatedAt = now,
            };
            await _store.SaveUpdateCampaignAsync(waiting, [], null, cancellationToken).ConfigureAwait(false);
            return waiting;
        }

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

    // Checks and applies the server's step: why it fails, or when it may start (outside the maintenance window of an
    // automatic update), or neither once the self-update took it. Under the gate, as every change of the policy: the
    // policy read here holds until the switch is asked for, whether the step is a first one or one resumed after a stop.
    private async Task<(string? Failure, DateTimeOffset? WaitUntil)> ApplyServerAsync(UpdateCampaignRecord campaign, CancellationToken cancellationToken)
    {
        if (_options.ApplyServerUpdate is not { } apply) return ("This server does not update itself (it does not run under its launcher).", null);
        if (ServerRuntime() is not { } runtime) return ("No package of CodeAlta Server is published for this server's OS and architecture.", null);
        if (!UpdateVersion.TryParse(_server.Read().Running, out var running)) return ("The version of this server is not known.", null);
        var held = await _store.GetUpdatePolicyAsync(campaign.OrganizationId, cancellationToken).ConfigureAwait(false);
        FleetUpdatePolicy? policy = null;
        if (held is not null && !FleetUpdateDocuments.TryParsePolicy(held.Payload, out policy, out var unreadable))
            return ($"The update policy this server holds does not read: {unreadable}.", null);
        // An automatic campaign holds only while the policy that started it does, as on the nodes.
        if (campaign.Automatic && held?.Sequence != campaign.Sequence) return ("The automatic update policy that started the campaign was replaced.", null);
        var entries = await _store.ListDeviceListAsync(campaign.OrganizationId, cancellationToken).ConfigureAwait(false);
        FleetDeviceListState devices;
        try
        {
            devices = FleetDeviceListVerifier.VerifyOrThrow(entries.Select(static entry => new FleetSignedDeviceListEntry(entry.Payload, entry.Signature)), campaign.OrganizationId);
        }
        catch (FleetDeviceListException exception)
        {
            return ($"The device list of this server does not verify: {exception.Message}", null);
        }

        // The policy held now wins, as on a node: a manifest to another version than a pinned one is refused, and an
        // automatic update waits for the signed window, whenever the nodes were done.
        var now = _time.GetUtcNow();
        var context = new UpdateInstructionContext
        {
            Devices = devices, OrganizationId = campaign.OrganizationId, Product = UpdateProduct.Server, Runtime = runtime, Running = running,
            LastPolicySequence = policy?.Sequence ?? 0, Policy = policy, Now = now,
        };
        if (!UpdateInstructions.TryVerify(Instruction(campaign), context, out var update, out var reason)) return ($"The server refused the campaign: {reason}.", null);
        // A person's schedule was the campaign's own start; the window of an automatic update holds for every machine.
        if (update.Automatic && update.NotBefore is { } start && start > now) return (null, start);
        if (update.Automatic)
        {
            // No person signed this version: it must still be the latest stable release the feed lists, read now, not when
            // the campaign started (a release withdrawn meanwhile is not installed). Its own self-update checks it again.
            var latest = await ReadLatestAsync(campaign.Feed, cancellationToken).ConfigureAwait(false);
            if (!UpdateCampaignEngine.Same(latest, campaign.TargetVersion))
                return ($"{campaign.TargetVersion} is no longer the latest stable version {campaign.Feed} lists ({latest ?? "none"}).", null);
        }

        var request = update.ToRequest() with { StableOnly = update.Automatic };
        await apply(request, ServerRequester(campaign), cancellationToken).ConfigureAwait(false);
        return (null, null);
    }

    // The feed of an automatic policy whose daily check is due, read outside the gate (a feed may take minutes to
    // answer); null when no check is due.
    private async Task<FeedRead?> ReadDueFeedAsync(string organizationId, CancellationToken cancellationToken)
    {
        var held = await _store.GetUpdatePolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (held is null || !FleetUpdateDocuments.TryParsePolicy(held.Payload, out var policy, out _) || policy.Mode != FleetUpdateModes.Automatic) return null;
        if (held.LastCheckAt is { } last && _time.GetUtcNow() - last < _options.CheckInterval) return null;
        try
        {
            return new FeedRead(held.Sequence, await ReadLatestAsync(policy.Feed ?? UpdateFeeds.NuGetOrg, cancellationToken).ConfigureAwait(false), null);
        }
        catch (UpdateException exception)
        {
            return new FeedRead(held.Sequence, null, exception.Message);
        }
    }

    // The latest stable version both products list on a feed (each with its tool package listed), or null.
    private async Task<string?> ReadLatestAsync(string feed, CancellationToken cancellationToken)
        => _options.ReadLatest is { } read
            ? await read(feed, cancellationToken).ConfigureAwait(false)
            : (await UpdateReleases.ListAsync(UpdateFeeds.Open(feed, _http), UpdateReleases.FleetProducts, false, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.ToString();

    // Under the gate: decides on what the feed listed for the policy still held, and records the check.
    private async Task CheckPolicyAsync(string organizationId, FeedRead read, CancellationToken cancellationToken)
    {
        var held = await _store.GetUpdatePolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        // A policy set while the feed was read is checked at the next step, on its own feed.
        if (held is null || held.Sequence != read.PolicySequence || !FleetUpdateDocuments.TryParsePolicy(held.Payload, out var policy, out _)
            || policy.Mode != FleetUpdateModes.Automatic)
            return;
        var now = _time.GetUtcNow();
        // Another step may have checked meanwhile: once a day.
        if (held.LastCheckAt is { } last && now - last < _options.CheckInterval) return;
        var message = read.Error ?? await CheckFeedAsync(held, policy, read.Latest, now, cancellationToken).ConfigureAwait(false);
        await _store.SetUpdatePolicyCheckAsync(organizationId, now, message, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("The automatic update policy read its feed: {Message}", message);
        _directory.Publish(organizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Update, "policy"));
    }

    private async Task<string> CheckFeedAsync(UpdatePolicyRecord held, FleetUpdatePolicy policy, string? latestText, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var feed = policy.Feed ?? UpdateFeeds.NuGetOrg;
        if (!UpdateVersion.TryParse(latestText, out var latest)) return $"{feed} lists no stable version of the fleet.";
        var organization = held.OrganizationId;
        if ((await _store.ListOutstandingUpdateCampaignsAsync(organization, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State)) is { } running)
            return $"{latest} is the latest; campaign {running.Sequence} to {running.TargetVersion} runs.";
        if (await TriedAsync(organization, latest, cancellationToken).ConfigureAwait(false) is { } tried) return tried;
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
        if (!await _store.CreateUpdateCampaignAsync(campaign, machines, new AuditEntry("update.campaign.create", AuditEntry.Success, null, null, id, null, null,
                $"{{\"automatic\":true,\"version\":\"{latest}\"}}"), cancellationToken).ConfigureAwait(false))
            return $"{latest} is the latest; another campaign runs.";
        Changed(campaign);
        Nudge();
        return $"{latest} is the latest: the fleet updates at {start:u}.";
    }

    // What the earlier automatic campaigns to a version say of another one, or null to start it: installed (done, or
    // partly done: the machines left update when they reconnect), withdrawn as bad (the server or a node rolled it back),
    // or failed MaxAutomaticAttempts times (a download, a feed or a refusal, tried again at each daily check until then).
    // A cancelled campaign does not count: a person, or a new policy, withdrew it.
    private async Task<string?> TriedAsync(string organizationId, UpdateVersion latest, CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var campaign in await _store.ListAutomaticUpdateCampaignsAsync(organizationId, latest.ToString(), cancellationToken).ConfigureAwait(false))
        {
            if (campaign.State is FleetUpdateCampaignStates.Done or FleetUpdateCampaignStates.PartlyDone) return $"{latest} is the latest; a campaign installed it already.";
            if (campaign.State != FleetUpdateCampaignStates.Failed) continue;
            if (campaign.ServerState == FleetUpdateMachineStates.RolledBack)
                return $"{latest} is the latest; it rolled back on the server, so it is not installed again automatically: start a campaign to install it.";
            if ((await _store.ListUpdateCampaignMachinesAsync(campaign.Id, cancellationToken).ConfigureAwait(false))
                .Any(static machine => machine.State == FleetUpdateMachineStates.RolledBack))
                return $"{latest} is the latest; it rolled back on a node, so it is not installed again automatically: start a campaign to install it.";
            failures++;
        }

        return failures >= MaxAutomaticAttempts
            ? $"{latest} is the latest; its automatic update failed {failures} times, so it is not tried again: start a campaign to install it."
            : null;
    }

    private async Task InstructAsync(ControlSession session, UpdateCampaignRecord campaign)
        => await SendAsync(session, FleetServerMethods.UpdateInstruction,
            JsonSerializer.SerializeToElement(Instruction(campaign), FleetServerJsonContext.Default.FleetUpdateInstruction)).ConfigureAwait(false);

    private Task SendPolicyAsync(ControlSession session, UpdatePolicyRecord policy)
        => SendAsync(session, FleetServerMethods.UpdatePolicy,
            JsonSerializer.SerializeToElement(new FleetSignedUpdateDocument(policy.Payload, policy.Signature), FleetUpdateJsonContext.Default.FleetSignedUpdateDocument));

    // The cancellation carries the signed document of the campaign: the node keeps the cancellation of a campaign it never
    // took until that document's sequence can no longer be accepted.
    private Task SendCancelAsync(ControlSession session, UpdateCampaignRecord campaign)
    {
        var instruction = Instruction(campaign);
        return SendAsync(session, FleetServerMethods.UpdateCancel,
            JsonSerializer.SerializeToElement(new FleetUpdateCancel(campaign.Id, instruction.Manifest, instruction.Policy), FleetServerJsonContext.Default.FleetUpdateCancel));
    }

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

/// <summary>What the feed of an automatic policy listed when its daily check was due.</summary>
/// <param name="PolicySequence">The sequence of the policy it was read for.</param>
/// <param name="Latest">The latest stable version of the fleet it lists, or null.</param>
/// <param name="Error">Why it could not be read, or null.</param>
internal sealed record FeedRead(long PolicySequence, string? Latest, string? Error);

/// <summary>The source-generated JSON of the audit details of the updates.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ServerUpdateJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
