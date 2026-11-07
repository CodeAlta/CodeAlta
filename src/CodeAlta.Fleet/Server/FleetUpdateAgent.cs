using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Fleet.Server;

/// <summary>What a <see cref="FleetUpdateAgent"/> reads and drives.</summary>
public sealed record FleetUpdateAgentOptions
{
    /// <summary>Gets the file the agent keeps the instruction it accepted in (<c>fleet/update-campaign.json</c>), so that it survives a restart.</summary>
    public required string StatePath { get; init; }

    /// <summary>Gets the device of this node.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Gets the version that runs.</summary>
    public required UpdateVersion Running { get; init; }

    /// <summary>Gets the product that updates itself; the node by default.</summary>
    public UpdateProduct Product { get; init; } = UpdateProduct.Node;

    /// <summary>Gets the runtime of this node; null for this process's.</summary>
    public string? Runtime { get; init; }

    /// <summary>Gets what reads the owner's settings of the updates of this machine (<c>update.json</c>), read again for each instruction.</summary>
    public required Func<UpdateSourcePolicy> Policy { get; init; }

    /// <summary>Gets what downloads, checks and stages a version, then asks the node to switch when idle (the self-update's <c>ApplyAsync</c>).</summary>
    public required Func<UpdateRequest, string, CancellationToken, Task<StagedUpdate?>> Apply { get; init; }

    /// <summary>Gets what reads a feed for the latest version this node may take (the self-update's check), for an automatic campaign.</summary>
    public required Func<UpdateRequest, CancellationToken, Task<UpdateCheck>> Check { get; init; }

    /// <summary>
    /// Gets what reads where the self-update stands; it throws <see cref="UpdateException"/> when its state cannot be read,
    /// and nothing is taken as confirmed then (an unreadable state is not a state without a trial).
    /// </summary>
    public required Func<FleetUpdateStatus> Status { get; init; }

    /// <summary>
    /// Gets what withdraws the switch a campaign asked for, when it still waits (a cancelled campaign): given who asked
    /// (the <c>requestedBy</c> the agent gave <see cref="Apply"/>) and why; it must leave a switch someone else asked for
    /// (<c>altanode update</c>) as it is. Null for none.
    /// </summary>
    public Action<string, string>? CancelSwitch { get; init; }

    /// <summary>Gets what finds the connector of a server link, to report to it; null for those the instructions came with only.</summary>
    public Func<string, FleetConnector?>? Connector { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets how often the agent looks at its instruction.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets what the agent logs.</summary>
    public Action<string> Log { get; init; } = static _ => { };

    /// <summary>Gets what sees each report the agent sends to a server, with the link of that server; tests read it.</summary>
    internal Action<string, FleetUpdateReport>? Said { get; init; }

    /// <summary>Gets what sends a report to the server of a link in the place of its connector; tests set it.</summary>
    internal Func<string, FleetUpdateReport, CancellationToken, Task>? Send { get; init; }
}

/// <summary>
/// The node's side of the coordinated update of the fleet (<c>doc/fleet/deployment.md</c>, updates). It verifies what a
/// server asks (<see cref="UpdateInstructions"/>: a manifest a controller of the anchored device list signed, newer, for
/// this machine and its runtime; the owner's <c>update.json</c> wins), keeps the instruction in a file, waits for its
/// time even across a restart, then runs the node's self-update, which switches when the node is idle. It reports where
/// it stands to the server that asked, when it changes and on each connection.
/// </summary>
public sealed class FleetUpdateAgent : IAsyncDisposable
{
    private readonly FleetUpdateAgentOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FleetConnector> _connectors = new(StringComparer.Ordinal);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private FleetUpdateAgentState _state = new();
    // Why the checkpoint kept in StatePath does not read; while it does not, the node takes no update of the fleet and
    // writes nothing over the file, which the owner fixes or deletes.
    private string? _unreadable;
    // Whether the state in memory is ahead of the checkpoint (a write failed): a cancellation or a policy holds in memory,
    // but until a write succeeds (retried at each look) the node acknowledges nothing, takes no instruction and runs no
    // work of the fleet, since a restart would bring back what the file holds.
    private bool _unsaved;
    // Whether a write succeeded again after a failure: what was held back is said at the next look.
    private bool _resay;
    // The cancellations received and not kept, by server and campaign, with whether they hold in memory (a write failed)
    // or not at all (the checkpoint does not read, or holds no more): no final state of those campaigns is said, a refusal
    // included, since the server takes any final state as the acknowledgement of the cancellation. Those held in memory
    // are kept by the next write that succeeds; the others once a cancellation sent again is acknowledged. A restart
    // forgets them: the server sends a cancellation it has no acknowledgement of again at each connection, before any
    // instruction.
    private readonly Dictionary<(string LinkId, string CampaignId), bool> _unkept = [];
    // Whether more cancellations than MaxUnkept went unkept: no final state is said until the node restarts.
    private bool _unkeptOverflow;
    private const int MaxUnkept = 1024;
    private bool _loaded;
    // Whether the campaign this node follows was read from the checkpoint and its server has not sent it again since the
    // start: nothing of it runs until then. A write that failed before a restart (a newer policy, a cancellation held in
    // memory) leaves the checkpoint behind, and nothing on disk can say so (that write failed too); the server sends its
    // policy and the cancellations not acknowledged at each connection, before the instruction of the campaign the node
    // follows, so that instruction, once the state is kept, says the checkpoint is current.
    private bool _resumed;
    private Task _loop = Task.CompletedTask;
    private Task? _applying;
    // The last report sent or to send: each waits for the one before (Say).
    private Task _sending = Task.CompletedTask;
    // What stops the download that runs, and its campaign; null when none runs.
    private CancellationTokenSource? _applyStop;
    private string? _applyCampaign;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="FleetUpdateAgent"/> class and reads the instruction it kept.</summary>
    /// <param name="options">What it reads and drives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public FleetUpdateAgent(FleetUpdateAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        lock (_gate) Readable();
        if (_unreadable is { } problem) _options.Log($"The update of the fleet is off: {problem}");
        Reconcile(options.Time.GetUtcNow());
    }

    /// <summary>Gets the campaign this node follows, its version and where it stands; null before the first.</summary>
    public FleetUpdateReport? Current
    {
        get
        {
            lock (_gate) return ReportOf(_state);
        }
    }

    /// <summary>Starts waiting for the time of the instruction kept, and moving it on.</summary>
    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Gets where this node stands in the campaign of a server, for its presence and its connections.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <returns>The report, or null when no campaign of that server reached this node.</returns>
    public FleetUpdateReport? Report(string linkId)
    {
        lock (_gate)
        {
            // The campaign that runs first; otherwise the last word of this node to that server: how its last campaign
            // ended (kept once another fleet's campaign took over), or the last instruction it refused. What is not kept
            // yet is not said (MaySay).
            var active = _state.LinkId == linkId ? ReportOf(_state) : null;
            var newest = active is not null && !FleetUpdateMachineStates.IsTerminal(active.State) ? active : null;
            var link = _state.Links.GetValueOrDefault(linkId);
            foreach (var report in newest is null ? new[] { active, link?.Receipt, link?.Refusal } : [])
            {
                if (report is not null && (newest is null || report.At >= newest.At)) newest = report;
            }

            return newest is not null && MaySay(linkId, newest) ? newest : null;
        }
    }

    // What this node keeps of a campaign of a server it accepted: the one it follows, or the receipt of the last one.
    private FleetUpdateReport? ReceiptOf(string linkId, string campaignId)
    {
        if (_state.LinkId == linkId && _state.CampaignId == campaignId) return ReportOf(_state);
        return _state.Links.GetValueOrDefault(linkId)?.Receipt is { } receipt && receipt.CampaignId == campaignId ? receipt : null;
    }

    // One fleet at a time: a campaign of another server waits until the one this node follows ends, so that no server is
    // left waiting for a node that silently went to another fleet.
    private string? OtherFleet(string linkId)
        => _state.LinkId is { } other && other != linkId && _state.CampaignId is { } campaign && !FleetUpdateMachineStates.IsTerminal(_state.State)
            ? $"this machine follows campaign {campaign} of another server; it takes an update of this fleet once that one ends"
            : null;

    /// <summary>Takes an instruction a server sent: verifies it against the device list of that server, read again (or the one last verified).</summary>
    /// <param name="connector">The connector of the server.</param>
    /// <param name="link">The link of the server.</param>
    /// <param name="instruction">The instruction.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once it is accepted or refused.</returns>
    public Task AcceptAsync(FleetConnector connector, FleetServerLink link, FleetUpdateInstruction instruction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        return AcceptAsync(connector, link.Id, link.OrganizationId, instruction, cancellationToken);
    }

    /// <summary>Takes an instruction a server sent: verifies it against the device list of that server, read again (or the one last verified).</summary>
    /// <param name="connector">The connector of the server.</param>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="organizationId">The organization of the server, as the link knows it; null for the one of its device list.</param>
    /// <param name="instruction">The instruction.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once it is accepted or refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task AcceptAsync(FleetConnector connector, string linkId, string? organizationId, FleetUpdateInstruction instruction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(linkId);
        ArgumentNullException.ThrowIfNull(instruction);
        lock (_gate) _connectors[linkId] = connector;
        await AcceptAsync(linkId, organizationId, instruction, token => ReadDevicesAsync(connector, token), cancellationToken).ConfigureAwait(false);
    }

    // The device list of the server, read again, or the one last verified when the server does not answer; null when none.
    private static async Task<FleetDeviceListState?> ReadDevicesAsync(FleetConnector connector, CancellationToken cancellationToken)
    {
        try
        {
            return await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return connector.Devices.Current;
        }
    }

    /// <summary>Takes an instruction, with the device list read by <paramref name="devices"/>; tests call it directly.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="organizationId">The organization of the server; null for the one of its device list.</param>
    /// <param name="instruction">The instruction.</param>
    /// <param name="devices">Reads the device list of the server; throws <see cref="FleetDeviceListException"/> when it does not verify.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once it is accepted or refused.</returns>
    internal async Task AcceptAsync(string linkId, string? organizationId, FleetUpdateInstruction instruction, Func<CancellationToken, Task<FleetDeviceListState?>> devices,
        CancellationToken cancellationToken)
    {
        string? conflict;
        FleetUpdateReport? receipt;
        lock (_gate)
        {
            // Without the checkpoint, a replayed manifest would pass again: nothing is taken until it reads.
            conflict = Readable() ? null : _unreadable;
            // The same campaign sent again, on a reconnection or as a replay: the server reads where it stands (once it is
            // kept), and nothing starts again, whatever was refused meanwhile.
            receipt = conflict is null ? ReceiptOf(linkId, instruction.CampaignId) : null;
            if (receipt is null) conflict ??= CancelledBefore(linkId, instruction.CampaignId) ?? (Persisted() ? null : UnsavedReason) ?? OtherFleet(linkId);
            else if (_resumed && _state.LinkId == linkId && _state.CampaignId == instruction.CampaignId && Persisted())
            {
                _resumed = false;
                _wake.Writer.TryWrite(true);
            }
        }

        if (receipt is not null)
        {
            Say(linkId, receipt);
            return;
        }

        if (conflict is not null)
        {
            Refuse(linkId, instruction, conflict);
            return;
        }

        FleetDeviceListState? list;
        try
        {
            list = await devices(cancellationToken).ConfigureAwait(false);
        }
        catch (FleetDeviceListException exception)
        {
            Refuse(linkId, instruction, $"the device list of the server does not verify ({exception.Reason})");
            return;
        }

        if (list is not { } devicesState)
        {
            Refuse(linkId, instruction, "the device list of the server could not be read");
            return;
        }

        UpdateSourcePolicy policy;
        try
        {
            policy = _options.Policy();
        }
        catch (UpdateException exception)
        {
            Refuse(linkId, instruction, exception.Message);
            return;
        }

        if (!policy.AcceptsFleet)
        {
            Refuse(linkId, instruction, "the owner of this machine turned off the updates of the fleet (update.json)");
            return;
        }

        var runtime = _options.Runtime ?? UpdateProduct.CurrentRuntime();
        if (runtime is null)
        {
            Refuse(linkId, instruction, "no package is published for this OS and architecture");
            return;
        }

        FleetUpdateAgentLink sequences;
        FleetUpdatePolicy? kept;
        lock (_gate)
        {
            sequences = _state.Links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink();
            kept = KeptPolicy(linkId);
        }

        var now = _options.Time.GetUtcNow();
        var context = new UpdateInstructionContext
        {
            Devices = devicesState, OrganizationId = organizationId ?? devicesState.OrganizationId, DeviceId = _options.DeviceId, Product = _options.Product, Runtime = runtime,
            Running = _options.Running, LastSequence = sequences.Sequence, LastCampaignId = sequences.CampaignId, LastPolicySequence = sequences.PolicySequence, Policy = kept,
            Now = now,
        };
        if (!UpdateInstructions.TryVerify(instruction, context, out var update, out var reason))
        {
            Refuse(linkId, instruction, reason);
            return;
        }

        try
        {
            policy.Resolve(update.Feed, UpdateOrigin.Remote);
        }
        catch (UpdateException exception)
        {
            Refuse(linkId, instruction, exception.Message);
            return;
        }

        FleetUpdateAgentState next;
        StopWork? replaced = null;
        lock (_gate)
        {
            // A policy, a campaign or a cancellation that came meanwhile wins: the instruction is checked again against it
            // by its next send.
            conflict = !Readable() ? _unreadable
                : !Persisted() ? UnsavedReason
                : CancelledBefore(linkId, instruction.CampaignId)
                  ?? (!(_state.Links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink()).SameAuthority(sequences) ? "the update policy or a campaign of the fleet changed meanwhile"
                      : OtherFleet(linkId));
            if (conflict is null)
            {
                var links = new Dictionary<string, FleetUpdateAgentLink>(_state.Links, StringComparer.Ordinal);
                // The campaign replaced keeps its receipt, for its server and against its replay; one that still ran is
                // superseded, and its work stops.
                if (_state.LinkId is { } previousLink && ReportOf(_state) is { } previous)
                {
                    if (!FleetUpdateMachineStates.IsTerminal(previous.State)) previous = previous with { State = FleetUpdateMachineStates.Superseded };
                    links[previousLink] = (links.GetValueOrDefault(previousLink) ?? new FleetUpdateAgentLink()) with { Receipt = previous };
                }
                var current = links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink();
                // An automatic update brings its policy, the latest this node knows once verified. The cancellations of
                // campaigns whose signed document can no longer be accepted are no longer needed.
                links[linkId] = (update.Automatic
                    ? current with { PolicySequence = update.Sequence, Policy = instruction.Policy }
                    : current with { Sequence = update.Sequence, CampaignId = update.CampaignId }).Pruned();
                next = Accepted(links);
                // The checkpoint is on disk before anything runs: one that is not kept would let this manifest, or an
                // older one, through again after a restart.
                if (TrySave(next) is { } failure)
                {
                    conflict = failure;
                }
                else
                {
                    replaced = WorkOf(_state);
                    _state = next;
                    _unsaved = false;
                    _resumed = false;
                }
            }
            else
            {
                next = _state;
            }
        }

        if (conflict is not null)
        {
            Refuse(linkId, instruction, conflict);
            return;
        }

        replaced?.Run(_options);

        _options.Log($"The update to {update.Version} of campaign {update.CampaignId} was accepted"
            + (update.NotBefore is { } start && start > now ? $"; it waits until {start:u}." : "."));
        Publish(next);
        _wake.Writer.TryWrite(true);
        return;

        FleetUpdateAgentState Accepted(Dictionary<string, FleetUpdateAgentLink> links)
            => new()
            {
                LinkId = linkId, Instruction = instruction, CampaignId = update.CampaignId, Version = update.Version.ToString(), Feed = update.Feed,
                ExpectedSha512 = update.ExpectedSha512, NotBefore = update.NotBefore, Automatic = update.Automatic, State = FleetUpdateMachineStates.Waiting, At = now,
                Links = links,
            };
    }

    /// <summary>
    /// Takes the update policy of the fleet a server sent (<see cref="FleetServerMethods.UpdatePolicy"/>): verifies it
    /// against the device list of that server and keeps it when it is newer than the one kept; what it no longer allows
    /// stops.
    /// </summary>
    /// <param name="connector">The connector of the server.</param>
    /// <param name="link">The link of the server.</param>
    /// <param name="policy">The signed policy.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once it is kept or ignored.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public Task AcceptPolicyAsync(FleetConnector connector, FleetServerLink link, FleetSignedUpdateDocument policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate) _connectors[link.Id] = connector;
        return AcceptPolicyAsync(link.Id, link.OrganizationId, policy, token => ReadDevicesAsync(connector, token), cancellationToken);
    }

    /// <summary>Takes the update policy of the fleet, with the device list read by <paramref name="devices"/>; tests call it directly.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="organizationId">The organization of the server; null for the one of its device list.</param>
    /// <param name="signed">The signed policy.</param>
    /// <param name="devices">Reads the device list of the server.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once it is kept or ignored.</returns>
    internal async Task AcceptPolicyAsync(string linkId, string? organizationId, FleetSignedUpdateDocument signed, Func<CancellationToken, Task<FleetDeviceListState?>> devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (!FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var policy, out var reason))
        {
            _options.Log($"The update policy of the fleet was ignored: {reason}.");
            return;
        }

        FleetDeviceListState? list;
        try
        {
            list = await devices(cancellationToken).ConfigureAwait(false);
        }
        catch (FleetDeviceListException exception)
        {
            _options.Log($"The update policy {policy.Sequence} of the fleet was ignored: the device list of the server does not verify ({exception.Reason}).");
            return;
        }

        if (list is null
            || !FleetUpdateDocuments.TryVerifySigner(signed, FleetUpdateDocuments.GetPolicySignedBytes(signed.Payload), policy.OrganizationId, policy.SignerDeviceId, list,
                organizationId ?? list.OrganizationId, out reason))
        {
            _options.Log($"The update policy {policy.Sequence} of the fleet was ignored: {reason ?? "the device list of the server could not be read"}.");
            return;
        }

        FleetUpdateAgentState next;
        string? stopped;
        StopWork? work = null;
        lock (_gate)
        {
            if (!Readable())
            {
                _options.Log($"The update policy {policy.Sequence} of the fleet was ignored: {_unreadable}.");
                return;
            }

            var link = _state.Links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink();
            if (policy.Sequence < link.PolicySequence || policy.Sequence == link.PolicySequence && link.Policy is not null)
            {
                if (link.Policy?.Payload != signed.Payload)
                    _options.Log($"The update policy {policy.Sequence} of the fleet was ignored: this node keeps policy {link.PolicySequence}.");
                return;
            }

            var links = new Dictionary<string, FleetUpdateAgentLink>(_state.Links, StringComparer.Ordinal)
            {
                [linkId] = (link with { Policy = signed, PolicySequence = policy.Sequence }).Pruned(),
            };
            next = _state with { Links = links };
            // What the new policy no longer allows stops: an automatic update under a manual or pinned policy, another
            // version than the one a policy pins.
            stopped = _state.LinkId == linkId ? Disallowed(_state, policy) : null;
            if (stopped is not null)
            {
                work = WorkOf(next);
                next = Stopped(next, stopped);
            }

            // A newer policy only takes away: it applies in memory even when it cannot be kept on disk, and until it is
            // kept the node takes no instruction and runs no work of the fleet (the older policy would come back with a
            // restart), and acknowledges no campaign it stopped.
            Commit(next);
        }

        work?.Run(_options);
        _options.Log($"The update policy {policy.Sequence} of the fleet is {policy.Mode}." + (stopped is null ? "" : $" Campaign {next.CampaignId} stops: {stopped}."));
        if (stopped is not null) Publish(next);
    }

    // Why a policy no longer allows the campaign this node follows, or null when it does (or the campaign ended).
    private static string? Disallowed(FleetUpdateAgentState state, FleetUpdatePolicy policy)
    {
        if (state.CampaignId is null || FleetUpdateMachineStates.IsTerminal(state.State) || !UpdateVersion.TryParse(state.Version, out var target)) return null;
        if (state.Automatic)
        {
            if (policy.Mode != FleetUpdateModes.Automatic) return $"policy {policy.Sequence} of the fleet is {policy.Mode}: it no longer updates by itself";
            if (!string.Equals(policy.Feed ?? UpdateFeeds.NuGetOrg, state.Feed, StringComparison.Ordinal)) return $"policy {policy.Sequence} of the fleet follows another feed";
            return null;
        }

        return policy.Mode == FleetUpdateModes.Pinned && !(UpdateVersion.TryParse(policy.PinnedVersion, out var pin) && pin.Equals(target))
            ? $"policy {policy.Sequence} pins the fleet to {policy.PinnedVersion}"
            : null;
    }

    // The policy last accepted from a server, as kept once verified; null for none.
    private FleetUpdatePolicy? KeptPolicy(string linkId)
        => _state.Links.GetValueOrDefault(linkId)?.Policy is { } kept && FleetUpdateDocuments.TryParsePolicy(kept.Payload, out var policy, out _) ? policy : null;

    /// <summary>Gets who asks for the switch of a campaign, as the pending switch records it (<c>requestedBy</c>).</summary>
    /// <param name="campaignId">The campaign.</param>
    /// <returns>The requester.</returns>
    internal static string Requester(string campaignId) => $"fleet campaign {campaignId}";

    private static FleetUpdatePolicy? PolicyOf(FleetUpdateInstruction? instruction)
        => instruction?.Policy is { } signed && FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var policy, out _) ? policy : null;

    // The campaign this node follows, stopped (a cancellation, a policy that no longer allows it). Under the gate; the
    // work it had started stops with WorkOf, once the gate is released.
    private FleetUpdateAgentState Stopped(FleetUpdateAgentState state, string? message)
        => state with { State = FleetUpdateMachineStates.Cancelled, Message = message, At = _options.Time.GetUtcNow() };

    // What stops the work a campaign started: its download, then the switch it asked for (only that one). Under the gate.
    private StopWork? WorkOf(FleetUpdateAgentState state)
        => state.CampaignId is { } campaign && state.State is FleetUpdateMachineStates.Downloading or FleetUpdateMachineStates.Switching
            ? new StopWork(_applyCampaign == campaign ? _applyStop : null, Requester(campaign), $"Campaign {campaign} was cancelled.")
            : null;

    /// <summary>
    /// Stops the work of a campaign: its download first, so that it publishes no switch once the switch it asked for is
    /// withdrawn (the self-update publishes under the lock of its state, unless cancelled); then that switch only, never
    /// one someone else asked for meanwhile.
    /// </summary>
    private sealed record StopWork(CancellationTokenSource? Download, string Requester, string Reason)
    {
        public void Run(FleetUpdateAgentOptions options)
        {
            try { Download?.Cancel(); }
            catch (ObjectDisposedException) { /* The download ended. */ }
            try { options.CancelSwitch?.Invoke(Requester, Reason); }
            catch (UpdateException exception) { options.Log($"The switch of {Requester} could not be withdrawn: {exception.Message}"); }
        }
    }

    /// <summary>Takes the cancellation of a campaign: an update that waits is dropped, a switch that waits is withdrawn.</summary>
    /// <param name="link">The link of the server.</param>
    /// <param name="cancel">The cancellation.</param>
    /// <param name="cancellationToken">Cancels the read of the device list, which checks the document the cancellation carries.</param>
    /// <returns>A task that completes once it is taken.</returns>
    public async Task CancelAsync(FleetServerLink link, FleetUpdateCancel cancel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(cancel);
        FleetDeviceListState? devices = null;
        FleetConnector? connector = null;
        bool verify;
        lock (_gate)
        {
            // Only the cancellation of a campaign never taken needs its document: the work of one that runs stops at once.
            verify = (cancel.Manifest is not null || cancel.Policy is not null) && _loaded && ReceiptOf(link.Id, cancel.CampaignId) is null
                && (_state.Links.GetValueOrDefault(link.Id)?.CancelledSigned.All(cancelled => cancelled.CampaignId != cancel.CampaignId) ?? true);
            if (verify) connector = _connectors.GetValueOrDefault(link.Id);
        }

        if (verify)
        {
            connector ??= _options.Connector?.Invoke(link.Id);
            try
            {
                devices = connector is null ? null : await ReadDevicesAsync(connector, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetDeviceListException)
            {
                // Not verified: the cancellation is kept for good, within its bound.
            }
        }

        CancelAndReport(link.Id, link.OrganizationId, cancel, devices);
    }

    /// <summary>Takes the cancellation of a campaign, without checking the document it carries.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="cancel">The cancellation.</param>
    public void Cancel(string linkId, FleetUpdateCancel cancel) => CancelAndReport(linkId, cancel);

    /// <summary>Takes the cancellation of a campaign without checking the document it carries; tests call it directly.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="cancel">The cancellation.</param>
    /// <returns>The report sent to the server, or null (see the other overload).</returns>
    internal FleetUpdateReport? CancelAndReport(string linkId, FleetUpdateCancel cancel) => CancelAndReport(linkId, null, cancel, null);

    /// <summary>
    /// Takes the cancellation of a campaign, and acknowledges it to its server with a final state: cancelled, or how the
    /// campaign ended before (sent again until the server hears it, a cancellation finds it done), or cancelled for a
    /// campaign this node never took. The cancellation is kept on disk before it is acknowledged, also for a campaign
    /// this node has not taken (its instruction may come after it): that campaign never starts here. Tests call it directly.
    /// </summary>
    /// <remarks>
    /// A cancellation is not signed, so what the node keeps of it is bounded, and never forgotten once acknowledged. A
    /// campaign this node accepted needs nothing more: the sequence of its manifest is behind the ones accepted, and its
    /// receipt answers a replay (an automatic campaign's id is not signed: a server may send its policy under any id, and
    /// only a newer policy stops that). A campaign never taken is kept with the sequence of its signed manifest or
    /// automatic policy when the cancellation carries one that verifies, until a newer manifest or policy of that server
    /// is accepted (none is needed when one was already); otherwise for good. Past the bounds of
    /// <see cref="FleetUpdateAgentLink"/>, the cancellation is neither kept nor acknowledged: the server sends it again.
    /// </remarks>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="organizationId">The organization of the server; null for the one of its device list.</param>
    /// <param name="cancel">The cancellation.</param>
    /// <param name="devices">The device list of the server, to check the document the cancellation carries; null for none.</param>
    /// <returns>
    /// The report sent to the server; null while the checkpoint does not read or the cancellation could not be kept (the
    /// server sends it again: the work of the campaign stops meanwhile all the same).
    /// </returns>
    internal FleetUpdateReport? CancelAndReport(string linkId, string? organizationId, FleetUpdateCancel cancel, FleetDeviceListState? devices)
    {
        ArgumentNullException.ThrowIfNull(linkId);
        ArgumentNullException.ThrowIfNull(cancel);
        var signed = Verified(cancel, devices, organizationId);
        FleetUpdateAgentState next;
        StopWork? work = null;
        FleetUpdateReport? done = null;
        bool active, kept, full = false;
        lock (_gate)
        {
            if (!Readable())
            {
                Unkept(linkId, cancel.CampaignId, inMemory: false);
                return null;
            }

            next = _state;
            active = _state.LinkId == linkId && _state.CampaignId == cancel.CampaignId && !FleetUpdateMachineStates.IsTerminal(_state.State);
            if (active)
            {
                // Its work stops at once, even when the cancellation cannot be kept.
                work = WorkOf(_state);
                next = Stopped(next, null);
                Commit(next);
            }
            else
            {
                var receipt = ReceiptOf(linkId, cancel.CampaignId);
                var link = _state.Links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink();
                done = receipt is not null && FleetUpdateMachineStates.IsTerminal(receipt.State) ? receipt
                    : link.Refusal is { } refusal && refusal.CampaignId == cancel.CampaignId ? refusal
                    : new FleetUpdateReport(cancel.CampaignId, FleetUpdateMachineStates.Cancelled, "", _options.Running.ToString(), "This machine had not taken the campaign.",
                        _options.Time.GetUtcNow());
                // Sent again until the server hears it: kept once.
                if (receipt is null)
                {
                    var cancelled = link.WithCancelled(cancel.CampaignId, signed);
                    full = cancelled is null;
                    if (cancelled is not null && !ReferenceEquals(cancelled, link))
                    {
                        next = _state with { Links = new Dictionary<string, FleetUpdateAgentLink>(_state.Links, StringComparer.Ordinal) { [linkId] = cancelled } };
                        Commit(next);
                    }
                }
            }

            kept = !full && Persisted();
            if (kept) _unkept.Remove((linkId, cancel.CampaignId));
            else Unkept(linkId, cancel.CampaignId, inMemory: !full);
        }

        work?.Run(_options);
        if (active) _options.Log($"Campaign {cancel.CampaignId} was cancelled.");
        if (full)
        {
            _options.Log($"The cancellation of campaign {cancel.CampaignId} is not kept, nor acknowledged: this node keeps as many cancellations of campaigns it never "
                + "took from this server as it may, and forgets none of them.");
            return null;
        }

        if (!kept)
        {
            // No acknowledgement: the server keeps the cancellation due and sends it again.
            _options.Log($"The cancellation of campaign {cancel.CampaignId} is acknowledged once it is kept in {_options.StatePath}.");
            return null;
        }

        if (done is not null)
        {
            Say(linkId, done);
            return done;
        }

        Publish(next);
        return ReportOf(next);
    }

    // Why an instruction of a campaign this node kept the cancellation of is refused, or null.
    private string? CancelledBefore(string linkId, string campaignId)
        => _state.Links.GetValueOrDefault(linkId)?.HasCancelled(campaignId) == true ? $"campaign {campaignId} was cancelled before this machine took it" : null;

    // The sequence a controller signed for a cancelled campaign, from the document the cancellation carries, once it
    // verifies against the device list of the server (as its instruction would); null otherwise.
    private static FleetUpdateCancelledCampaign? Verified(FleetUpdateCancel cancel, FleetDeviceListState? devices, string? organizationId)
    {
        if (devices is null) return null;
        var organization = organizationId ?? devices.OrganizationId;
        if (cancel.Manifest is { } manifest)
        {
            return FleetUpdateDocuments.TryParseManifest(manifest.Payload, out var parsed, out _) && parsed.CampaignId == cancel.CampaignId
                && FleetUpdateDocuments.TryVerifySigner(manifest, FleetUpdateDocuments.GetManifestSignedBytes(manifest.Payload), parsed.OrganizationId, parsed.SignerDeviceId, devices,
                    organization, out _)
                ? new FleetUpdateCancelledCampaign(cancel.CampaignId, parsed.Sequence, Automatic: false)
                : null;
        }

        return cancel.Policy is { } policy && FleetUpdateDocuments.TryParsePolicy(policy.Payload, out var automatic, out _) && automatic.Mode == FleetUpdateModes.Automatic
            && FleetUpdateDocuments.TryVerifySigner(policy, FleetUpdateDocuments.GetPolicySignedBytes(policy.Payload), automatic.OrganizationId, automatic.SignerDeviceId, devices,
                organization, out _)
            ? new FleetUpdateCancelledCampaign(cancel.CampaignId, automatic.Sequence, Automatic: true)
            : null;
    }

    /// <summary>Gets the campaigns of a server whose cancellation this node keeps; tests read it.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <returns>The campaigns.</returns>
    internal IReadOnlyList<string> CancellationsOf(string linkId)
    {
        lock (_gate)
        {
            return _state.Links.GetValueOrDefault(linkId) is { } link ? [.. link.Cancelled, .. link.CancelledSigned.Select(static cancelled => cancelled.CampaignId)] : [];
        }
    }

    /// <summary>Looks at the instruction once: notes a switch that ended, or starts the update when its time came. The loop calls it; tests call it directly.</summary>
    /// <returns>The update started, or null.</returns>
    internal Task? Tick()
    {
        var now = _options.Time.GetUtcNow();
        Reconcile(now);
        SayAgain();
        FleetUpdateAgentState state;
        lock (_gate)
        {
            // Nothing runs from a state that is not kept (a policy or a cancellation held in memory only): the write is
            // tried again first. Nor from a checkpoint read at the start before its server is heard again (_resumed).
            if (!Persisted() || _resumed) return null;
            state = _state;
            if (state.State != FleetUpdateMachineStates.Waiting || state.CampaignId is null || _applying is { IsCompleted: false }) return null;
            if (state.NotBefore is { } start && now < start) return null;
            if (state.Automatic)
            {
                // No person chose the time: the signed maintenance window of the latest policy decides, also for an
                // update that missed its window while the node was off.
                if ((KeptPolicy(state.LinkId!) ?? PolicyOf(state.Instruction)) is not { } policy) return null;
                var opening = FleetUpdateDocuments.WindowOpening(policy, now);
                if (opening > now)
                {
                    Commit(state with { NotBefore = opening });
                    return null;
                }
            }

            state = state with { State = FleetUpdateMachineStates.Downloading, Message = null, At = now };
            Commit(state);
            // The download of this campaign stops with the node, and with the campaign.
            var download = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            (_applyStop, _applyCampaign) = (download, state.CampaignId);
            var started = state;
            _applying = Task.Run(async () =>
            {
                try
                {
                    await ApplyAsync(started, download.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_applyStop == download) (_applyStop, _applyCampaign) = (null, null);
                    }

                    download.Dispose();
                }
            });
        }

        Publish(state);
        return _applying;
    }

    private async Task ApplyAsync(FleetUpdateAgentState state, CancellationToken cancellationToken)
    {
        string outcome;
        string? message = null;
        try
        {
            var request = new UpdateRequest
            {
                Version = state.Version, Source = state.Feed, ExpectedSha512 = state.ExpectedSha512, Origin = UpdateOrigin.Remote,
            };
            if (state.Automatic)
            {
                // No person signed this version: it must be the latest stable version the feed lists for this machine.
                var check = await _options.Check(new UpdateRequest { Source = state.Feed, StableOnly = true, Origin = UpdateOrigin.Remote }, cancellationToken).ConfigureAwait(false);
                if (check.Latest?.ToString() != state.Version)
                    throw new UpdateException(UpdateErrorCodes.NotFound, $"The latest stable version {state.Feed} lists is {check.Latest?.ToString() ?? "none"}, not {state.Version}.");
            }

            var staged = await _options.Apply(request, Requester(state.CampaignId!), cancellationToken).ConfigureAwait(false);
            // A version that runs already is updated only once it confirmed; one on trial still may roll back, and a state
            // that does not read confirms nothing (the next look decides).
            outcome = staged is null && UpdateVersion.TryParse(state.Version, out var target) && TryStatus(out var status) && Confirmed(target, status)
                ? FleetUpdateMachineStates.Updated
                : FleetUpdateMachineStates.Switching;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The node stops: the update starts again with the next one.
            lock (_gate)
            {
                if (_state.CampaignId == state.CampaignId && _state.State == FleetUpdateMachineStates.Downloading) _state = _state with { State = FleetUpdateMachineStates.Waiting };
            }

            return;
        }
        catch (UpdateException exception)
        {
            // The owner's settings, or a switch the owner asked for on the machine, win.
            outcome = exception.Code is UpdateErrorCodes.FleetRefused or UpdateErrorCodes.SourceNotAllowed or UpdateErrorCodes.InvalidSource or UpdateErrorCodes.SwitchWaits
                ? FleetUpdateMachineStates.Refused
                : FleetUpdateMachineStates.Failed;
            message = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            outcome = FleetUpdateMachineStates.Failed;
            message = exception.Message;
        }

        FleetUpdateAgentState next;
        var superseded = false;
        lock (_gate)
        {
            // A cancellation or a newer campaign that came meanwhile wins: a switch this one published just before is
            // withdrawn (only that one).
            if (_state.CampaignId != state.CampaignId || _state.State != FleetUpdateMachineStates.Downloading)
            {
                superseded = true;
                next = _state;
            }
            else
            {
                next = _state with { State = outcome, Message = message, At = _options.Time.GetUtcNow() };
                Commit(next);
            }
        }

        if (superseded)
        {
            if (outcome == FleetUpdateMachineStates.Switching) new StopWork(null, Requester(state.CampaignId!), $"Campaign {state.CampaignId} was cancelled.").Run(_options);
            return;
        }

        _options.Log($"Campaign {state.CampaignId}: {outcome}{(message is null ? "" : ": " + message)}");
        Publish(next);
    }

    // Where the self-update stands, or false when its state does not read.
    private bool TryStatus([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FleetUpdateStatus? status)
    {
        try
        {
            status = _options.Status();
            return true;
        }
        catch (UpdateException)
        {
            status = null;
            return false;
        }
    }

    // Whether a version runs and confirmed that it works: it runs, and no trial of it waits for its confirmation (read
    // from a state that reads: see TryStatus).
    private bool Confirmed(UpdateVersion target, FleetUpdateStatus status)
        => target.Equals(_options.Running) && !(status.Trial is { } trial && UpdateVersion.TryParse(trial.Version, out var onTrial) && onTrial.Equals(target));

    // How the last switch to a version ended, once a state was reached: rolled back or failed; null otherwise.
    private static FleetUpdateOutcome? EndedBadly(FleetUpdateStatus status, UpdateVersion target, DateTimeOffset since)
        => status.LastOutcome is { } last && UpdateVersion.TryParse(last.Version, out var ended) && ended.Equals(target) && last.At >= since.AddSeconds(-1)
           && last.Outcome is UpdateOutcomes.RolledBack or UpdateOutcomes.Failed
            ? last
            : null;

    // What a restart, a switch or a rollback changed: a version that runs and confirmed is updated (one on trial is not
    // yet); one rolled back is rolled back, even after it was reported updated.
    private void Reconcile(DateTimeOffset now)
    {
        FleetUpdateAgentState next;
        lock (_gate)
        {
            var state = _state;
            if (state.CampaignId is null || state.Version is null) return;
            if (FleetUpdateMachineStates.IsTerminal(state.State) && state.State is not (FleetUpdateMachineStates.Cancelled or FleetUpdateMachineStates.Updated)) return;
            if (!UpdateVersion.TryParse(state.Version, out var target)) return;
            if (!TryStatus(out var status)) return;
            string? outcome = null;
            string? message = null;
            if (state.State == FleetUpdateMachineStates.Updated)
            {
                if (EndedBadly(status, target, state.At) is not { Outcome: UpdateOutcomes.RolledBack } rolledBack) return;
                outcome = FleetUpdateMachineStates.RolledBack;
                message = rolledBack.Message;
            }
            else if (target.Equals(_options.Running))
            {
                // On trial, it may still roll back: the campaign hears of it once it confirmed.
                if (!Confirmed(target, status)) return;
                outcome = FleetUpdateMachineStates.Updated;
            }
            else if (state.State == FleetUpdateMachineStates.Cancelled)
            {
                return;
            }
            else if (state.State is FleetUpdateMachineStates.Switching or FleetUpdateMachineStates.Downloading)
            {
                if (EndedBadly(status, target, state.At) is { } last)
                {
                    outcome = last.Outcome == UpdateOutcomes.RolledBack ? FleetUpdateMachineStates.RolledBack : FleetUpdateMachineStates.Failed;
                    message = last.Message;
                }
                else if (state.State == FleetUpdateMachineStates.Switching && status.Pending is null && status.Trial is null)
                {
                    outcome = FleetUpdateMachineStates.Failed;
                    message = "The switch was withdrawn on this machine.";
                }
                else if (state.State == FleetUpdateMachineStates.Switching && status.Pending is { RequestedBy: { } by } other && by != Requester(state.CampaignId))
                {
                    // The owner asked for another switch (altanode update): it replaced the one of this campaign.
                    outcome = FleetUpdateMachineStates.Failed;
                    message = $"The switch was replaced on this machine by one to {other.Version} ({by}).";
                }
                else if (state.State == FleetUpdateMachineStates.Downloading && _applying is null)
                {
                    // A download a stop interrupted starts again.
                    outcome = FleetUpdateMachineStates.Waiting;
                }
            }

            if (outcome is null || outcome == state.State) return;
            next = state with { State = outcome, Message = message, At = now };
            Commit(next);
        }

        _options.Log($"Campaign {next.CampaignId}: {next.State}{(next.Message is null ? "" : ": " + next.Message)}");
        Publish(next);
    }

    private void Refuse(string linkId, FleetUpdateInstruction instruction, string reason)
    {
        _options.Log($"The update of campaign {instruction.CampaignId} was refused: {reason}.");
        var report = new FleetUpdateReport(instruction.CampaignId, FleetUpdateMachineStates.Refused, instruction.Version ?? "", _options.Running.ToString(), reason,
            _options.Time.GetUtcNow());
        lock (_gate)
        {
            // A refusal never replaces a campaign this node accepted, even one that ended (its receipt answers a replay):
            // it is kept apart, for the server that asked, which reads it again on its next connection.
            var links = new Dictionary<string, FleetUpdateAgentLink>(_state.Links, StringComparer.Ordinal);
            links[linkId] = (links.GetValueOrDefault(linkId) ?? new FleetUpdateAgentLink()) with { Refusal = report };
            Commit(_state with { Links = links });
        }

        Say(linkId, report);
    }

    // Reports where the campaign of a state stands, once that state is kept: a final state acknowledges a cancellation.
    private void Publish(FleetUpdateAgentState state)
    {
        if (state.LinkId is not null && ReportOf(state) is { } report) Say(state.LinkId, report);
    }

    // Every report to a server goes through here (and Report, for its connections), once MaySay allows it.
    private void Say(string linkId, FleetUpdateReport report)
    {
        lock (_gate)
        {
            // The campaign this node follows is said as it stands now: a report read before a later change (the download
            // published after the switch it led to) must not reach the server after the report of that change.
            if (_state.LinkId == linkId && _state.CampaignId == report.CampaignId && ReportOf(_state) is { } current) report = current;
            if (!MaySay(linkId, report)) return;
            // One report at a time, in the order they are said, each once the server answered: the server handles the
            // requests of a connection concurrently, and records the last report it handles.
            _sending = _sending.ContinueWith(_ => SendAsync(linkId, report), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default)
                .Unwrap();
        }

        _options.Said?.Invoke(linkId, report);
    }

    // Once a write succeeded again, each server hears where this node stands for it, held back meanwhile.
    private void SayAgain()
    {
        string[] links;
        lock (_gate)
        {
            if (!_resay || !Persisted()) return;
            _resay = false;
            links = [.. _state.Links.Keys.Append(_state.LinkId ?? "").Where(static id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        }

        foreach (var linkId in links)
        {
            if (Report(linkId) is { } report) Say(linkId, report);
        }
    }

    private async Task SendAsync(string linkId, FleetUpdateReport report)
    {
        try
        {
            if (_options.Send is { } send)
            {
                await send(linkId, report, _stop.Token).ConfigureAwait(false);
                return;
            }

            FleetConnector? connector;
            lock (_gate) connector = _connectors.GetValueOrDefault(linkId);
            connector ??= _options.Connector?.Invoke(linkId);
            // Offline, the report goes with the next connection.
            if (connector is not null) await connector.ReportUpdateAsync(report, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The node stops: its next connection says where it stands.
        }
    }

    private FleetUpdateReport? ReportOf(FleetUpdateAgentState state)
        => state.CampaignId is null || state.State is null
            ? null
            : new FleetUpdateReport(state.CampaignId, state.State, state.Version ?? "", _options.Running.ToString(), state.Message, state.At);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // The update runs beside the loop; Dispose waits for it.
                _ = Tick();
            }
            catch (UpdateException exception)
            {
                _options.Log($"The update of the fleet could not read its state: {exception.Message}");
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var wake = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
                await Task.WhenAny(wake, Task.Delay(_options.Interval, _options.Time, linked.Token)).ConfigureAwait(false);
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

    // Whether the checkpoint reads, read again while it does not (the owner may have fixed or deleted it); under the gate.
    private bool Readable()
    {
        if (_loaded) return true;
        string? problem;
        FleetUpdateAgentState? loaded = null;
        try
        {
            loaded = JsonSerializer.Deserialize(File.ReadAllBytes(_options.StatePath), FleetUpdateAgentJsonContext.Default.FleetUpdateAgentState);
            problem = loaded is null ? "it holds no state" : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // None yet: a node that never took an update of the fleet.
            loaded = new FleetUpdateAgentState();
            problem = null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            problem = exception.Message;
        }

        if (problem is not null)
        {
            _unreadable = $"{_options.StatePath} cannot be read ({problem}): this node takes no update of the fleet until it can. Fix the file, or delete it to reset "
                + "what the node keeps of the fleet's updates (it then takes, as a new node, any campaign a controller signed for a version newer than the one it runs)";
            return false;
        }

        _unreadable = null;
        _state = loaded!;
        _unsaved = false;
        _resumed = loaded!.CampaignId is not null && !FleetUpdateMachineStates.IsTerminal(loaded.State);
        _loaded = true;
        return true;
    }

    // Makes a state the one in memory and keeps it; a failure is logged, and leaves the state unsaved (see _unsaved).
    // Under the gate.
    private void Commit(FleetUpdateAgentState state)
    {
        _state = state;
        if (TrySave(state) is { } failure)
        {
            if (!_unsaved) _options.Log($"The update of the fleet: {failure}; it takes no update of the fleet until it can.");
            _unsaved = true;
        }
        else if (_unsaved)
        {
            Kept();
        }
    }

    // Whether the state in memory is kept, written again first when an earlier write failed. Under the gate.
    private bool Persisted()
    {
        if (!_unsaved) return true;
        if (TrySave(_state) is not null) return false;
        Kept();
        _options.Log("The update of the fleet: its state is kept again.");
        return true;
    }

    // A write succeeded after a failure: what the memory held is kept, the cancellations it held included, and what was
    // held back is said again. Under the gate.
    private void Kept()
    {
        _unsaved = false;
        _resay = true;
        foreach (var (key, inMemory) in _unkept.ToArray())
        {
            if (inMemory) _unkept.Remove(key);
        }
    }

    // Notes a cancellation received and not kept. Under the gate.
    private void Unkept(string linkId, string campaignId, bool inMemory)
    {
        var key = (linkId, campaignId);
        if (_unkept.TryGetValue(key, out var held)) _unkept[key] = held && inMemory;
        else if (_unkept.Count < MaxUnkept) _unkept[key] = inMemory;
        else _unkeptOverflow = true;
    }

    /// <summary>
    /// Whether a report may be said to its server: never a final state (a refusal included: the server takes any as the
    /// acknowledgement of a cancellation) of a campaign whose cancellation this node received and has not kept; otherwise
    /// once the state is kept, or, while the memory is ahead of the checkpoint, a refusal, which acknowledges nothing that
    /// memory holds alone and tells the server why nothing is taken (a checkpoint that does not read or cannot be
    /// written). Under the gate.
    /// </summary>
    private bool MaySay(string linkId, FleetUpdateReport report)
    {
        var persisted = Persisted();
        if (FleetUpdateMachineStates.IsTerminal(report.State) && (_unkeptOverflow || _unkept.ContainsKey((linkId, report.CampaignId)))) return false;
        return persisted || report.State == FleetUpdateMachineStates.Refused;
    }

    private string UnsavedReason => $"the update of the fleet could not be kept in {_options.StatePath}: this node takes no update of the fleet until it can";

    // Keeps a state; returns why it could not be kept, or null. Nothing is written over a checkpoint that does not read.
    private string? TrySave(FleetUpdateAgentState state)
    {
        if (_unreadable is { } unreadable) return unreadable;
        var staging = _options.StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.StatePath)!);
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(state, FleetUpdateAgentJsonContext.Default.FleetUpdateAgentState));
            StagedFileReplacement.MoveOver(staging, _options.StatePath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staging); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return $"the update could not be kept in {_options.StatePath}: {exception.Message}";
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        Task? applying;
        lock (_gate) applying = _applying;
        if (applying is not null)
        {
            try { await applying.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        Task sending;
        lock (_gate) sending = _sending;
        await sending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }
}

/// <summary>The sequences a node accepted from one server, against replays.</summary>
internal sealed record FleetUpdateAgentLink
{
    /// <summary>Gets the sequence of the last manifest accepted.</summary>
    public long Sequence { get; init; }

    /// <summary>Gets the campaign of that manifest.</summary>
    public string? CampaignId { get; init; }

    /// <summary>Gets the sequence of the last update policy accepted, of any mode.</summary>
    public long PolicySequence { get; init; }

    /// <summary>Gets the last update policy accepted, as signed (<see cref="FleetServerMethods.UpdatePolicy"/>, or the one of an automatic campaign); null before.</summary>
    public FleetSignedUpdateDocument? Policy { get; init; }

    /// <summary>Gets whether what authorizes an instruction (the sequences and the policy) is the same as another's.</summary>
    /// <param name="other">The other.</param>
    /// <returns>Whether they are the same.</returns>
    public bool SameAuthority(FleetUpdateAgentLink other)
        => Sequence == other.Sequence && CampaignId == other.CampaignId && PolicySequence == other.PolicySequence && Equals(Policy, other.Policy);

    /// <summary>Gets how the last campaign of this server ended, once another campaign replaced it; null before.</summary>
    public FleetUpdateReport? Receipt { get; init; }

    /// <summary>Gets the last instruction of this server the node refused; null before the first.</summary>
    public FleetUpdateReport? Refusal { get; init; }

    /// <summary>
    /// Gets the campaigns of this server, never taken, whose cancellation the node acknowledged without a signed document
    /// that verifies (<see cref="FleetUpdateCancel"/>): nothing says when their instruction could no longer be accepted, so
    /// they are kept for good, at most <see cref="MaxCancelled"/> with the automatic ones of <see cref="CancelledSigned"/>.
    /// Kept before the cancellation is acknowledged, so that none of them starts, even one whose instruction comes after it.
    /// </summary>
    public IReadOnlyList<string> Cancelled { get; init; } = [];

    /// <summary>
    /// Gets the campaigns of this server, never taken, whose cancellation came with the signed manifest or automatic policy
    /// of the campaign, verified: each is kept until that sequence can no longer be accepted (<see cref="Consumed"/>).
    /// </summary>
    public IReadOnlyList<FleetUpdateCancelledCampaign> CancelledSigned { get; init; } = [];

    /// <summary>
    /// The most cancellations kept for one server without a signed manifest: those of <see cref="Cancelled"/>, and those
    /// of automatic campaigns (a server names an automatic campaign as it likes: the policy it carries does not sign its id).
    /// </summary>
    public const int MaxCancelled = 64;

    /// <summary>The most cancellations kept for one server with the signed manifest of their campaign.</summary>
    public const int MaxCancelledManifests = 256;

    /// <summary>Gets whether the node keeps the cancellation of a campaign.</summary>
    /// <param name="campaignId">The campaign.</param>
    /// <returns>Whether it does.</returns>
    public bool HasCancelled(string campaignId)
        => Cancelled.Contains(campaignId, StringComparer.Ordinal) || CancelledSigned.Any(cancelled => cancelled.CampaignId == campaignId);

    /// <summary>Gets whether the signed document of a campaign can no longer be accepted from this server: a newer one was.</summary>
    /// <param name="cancelled">The campaign.</param>
    /// <returns>Whether its sequence is behind the ones accepted.</returns>
    public bool Consumed(FleetUpdateCancelledCampaign cancelled)
    {
        ArgumentNullException.ThrowIfNull(cancelled);
        return cancelled.Automatic ? cancelled.Sequence < PolicySequence : cancelled.Sequence <= Sequence;
    }

    /// <summary>
    /// Gets this link with the cancellation of a campaign kept: with the sequence of its signed document when it verified,
    /// or for good. None is ever dropped to make room: past the bounds the cancellation is not kept.
    /// </summary>
    /// <param name="campaignId">The campaign.</param>
    /// <param name="signed">What its verified document says; null when it came without one, or one that does not verify.</param>
    /// <returns>The link (this one when nothing changes), or null when there is no room for it.</returns>
    public FleetUpdateAgentLink? WithCancelled(string campaignId, FleetUpdateCancelledCampaign? signed)
    {
        if (signed is null) return HasCancelled(campaignId) ? this : Weak(this) < MaxCancelled ? this with { Cancelled = [.. Cancelled, campaignId] } : null;
        if (CancelledSigned.Any(cancelled => cancelled.CampaignId == campaignId)) return this;
        // A cancellation kept for good, now with its sequence, moves (a campaign id names one manifest).
        var link = this with { Cancelled = [.. Cancelled.Where(id => id != campaignId)] };
        if (link.Consumed(signed)) return link;
        var room = signed.Automatic ? Weak(link) < MaxCancelled : link.CancelledSigned.Count(static cancelled => !cancelled.Automatic) < MaxCancelledManifests;
        return room ? link with { CancelledSigned = [.. link.CancelledSigned, signed] } : null;

        static int Weak(FleetUpdateAgentLink link) => link.Cancelled.Count + link.CancelledSigned.Count(static cancelled => cancelled.Automatic);
    }

    /// <summary>Gets this link without the cancellations whose signed document can no longer be accepted.</summary>
    /// <returns>The link; this one when it keeps none of them.</returns>
    public FleetUpdateAgentLink Pruned() => CancelledSigned.Any(Consumed) ? this with { CancelledSigned = [.. CancelledSigned.Where(cancelled => !Consumed(cancelled))] } : this;
}

/// <summary>A campaign whose cancellation a node keeps with the sequence a controller signed for it.</summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="Sequence">The sequence of its manifest, or of its automatic policy.</param>
/// <param name="Automatic">Whether it is an automatic campaign.</param>
internal sealed record FleetUpdateCancelledCampaign(string CampaignId, long Sequence, bool Automatic);

/// <summary>What the agent keeps: the instruction it accepted, where it stands, and the sequences of each server.</summary>
internal sealed record FleetUpdateAgentState
{
    public string? LinkId { get; init; }

    public FleetUpdateInstruction? Instruction { get; init; }

    public string? CampaignId { get; init; }

    public string? Version { get; init; }

    public string? Feed { get; init; }

    public string? ExpectedSha512 { get; init; }

    public DateTimeOffset? NotBefore { get; init; }

    public bool Automatic { get; init; }

    public string? State { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset At { get; init; }

    public Dictionary<string, FleetUpdateAgentLink> Links { get; init; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetUpdateAgentState))]
internal sealed partial class FleetUpdateAgentJsonContext : JsonSerializerContext;
