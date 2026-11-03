using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>A node as the campaign sees it now: online or not, and the version it runs.</summary>
/// <param name="Online">Whether it holds a control connection.</param>
/// <param name="Running">The version it runs, as its inventory or its last report says; null when unknown.</param>
internal sealed record UpdateCampaignNode(bool Online, string? Running);

/// <summary>The server as the campaign sees it: the version that runs and how its last switch ended.</summary>
/// <param name="Running">The version the server runs.</param>
/// <param name="LastOutcome">How its last switch ended, or null.</param>
internal sealed record UpdateCampaignServer(string Running, FleetUpdateOutcome? LastOutcome);

/// <summary>What one step of a campaign decided.</summary>
/// <param name="Campaign">The campaign after the step.</param>
/// <param name="Machines">The machines whose record changed.</param>
/// <param name="Instruct">The nodes to send the instruction to now.</param>
/// <param name="UpdateServer">Whether the server updates itself now.</param>
internal sealed record UpdateCampaignStep(UpdateCampaignRecord Campaign, IReadOnlyList<UpdateCampaignMachineRecord> Machines, IReadOnlyList<string> Instruct, bool UpdateServer)
{
    /// <summary>Gets whether anything changed.</summary>
    public bool Changed(UpdateCampaignRecord before) => Machines.Count > 0 || Instruct.Count > 0 || UpdateServer || !Equals(before, Campaign);
}

/// <summary>The limits of the campaigns.</summary>
internal sealed record UpdateCampaignLimits
{
    /// <summary>Gets how many nodes update at once.</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>
    /// Gets how long a node asked to update may stay offline before the campaign stops waiting for it: a node goes
    /// offline while it switches, and is counted pending (it updates when it reconnects) after that.
    /// </summary>
    public TimeSpan OfflineGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long the server waits for a first report before it sends the instruction again.</summary>
    public TimeSpan ResendAfter { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The state machine of an update campaign (<c>doc/fleet/deployment.md</c>, updates), without input or output: given
/// the campaign, its machines, what is online and the time, it says what changes and whom to ask. Nodes first, a bounded
/// number at a time; it waits until each node online reports the version (or failed, or rolled back) — a node offline
/// is pending, asked when it reconnects, and does not hold the campaign; then the server updates itself, last.
/// </summary>
internal sealed class UpdateCampaignEngine(UpdateCampaignLimits limits)
{
    private static readonly string[] InProgress =
        [FleetUpdateMachineStates.Instructed, FleetUpdateMachineStates.Waiting, FleetUpdateMachineStates.Downloading, FleetUpdateMachineStates.Switching];

    /// <summary>Moves a campaign on.</summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="machines">Its machines.</param>
    /// <param name="node">What is known of a node now.</param>
    /// <param name="server">What is known of the server now.</param>
    /// <param name="now">The time.</param>
    /// <returns>What changed.</returns>
    public UpdateCampaignStep Step(UpdateCampaignRecord campaign, IReadOnlyList<UpdateCampaignMachineRecord> machines, Func<string, UpdateCampaignNode> node,
        UpdateCampaignServer server, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(server);
        var changed = new Dictionary<string, UpdateCampaignMachineRecord>(StringComparer.Ordinal);
        var instruct = new List<string>();
        var current = machines.ToDictionary(static machine => machine.DeviceId, StringComparer.Ordinal);

        void Set(UpdateCampaignMachineRecord machine)
        {
            current[machine.DeviceId] = machine;
            changed[machine.DeviceId] = machine;
        }

        if (campaign.State == FleetUpdateCampaignStates.Scheduled)
        {
            if (campaign.NotBefore is { } start && now < start) return new UpdateCampaignStep(campaign, [], [], false);
            campaign = campaign with { State = FleetUpdateCampaignStates.UpdatingNodes, UpdatedAt = now };
        }

        if (campaign.State is FleetUpdateCampaignStates.UpdatingNodes or FleetUpdateCampaignStates.PartlyDone)
        {
            foreach (var machine in current.Values.ToArray())
            {
                if (FleetUpdateMachineStates.IsTerminal(machine.State)) continue;
                var view = node(machine.DeviceId);
                // A node that runs the version is done, whether it just switched or ran it already.
                if (view.Online && Same(view.Running, campaign.TargetVersion))
                {
                    Set(machine with { State = FleetUpdateMachineStates.Updated, Running = view.Running, Message = null, UpdatedAt = now });
                    continue;
                }

                if (machine.State == FleetUpdateMachineStates.Instructed && view.Online && machine.InstructedAt is { } sent && now - sent >= limits.ResendAfter)
                {
                    // No report yet: the instruction may have been lost with a connection.
                    Set(machine with { InstructedAt = now });
                    instruct.Add(machine.DeviceId);
                }
            }

            var busy = current.Values.Count(machine => InProgress.Contains(machine.State) && (node(machine.DeviceId).Online || now - LastTouched(machine) < limits.OfflineGrace));
            foreach (var machine in current.Values.Where(static machine => machine.State == FleetUpdateMachineStates.Pending).OrderBy(static machine => machine.DeviceId, StringComparer.Ordinal)
                         .ToArray())
            {
                if (busy >= limits.MaxConcurrent) break;
                if (!node(machine.DeviceId).Online) continue;
                Set(machine with { State = FleetUpdateMachineStates.Instructed, Message = null, InstructedAt = now, UpdatedAt = now });
                instruct.Add(machine.DeviceId);
                busy++;
            }

            if (campaign.State == FleetUpdateCampaignStates.PartlyDone)
            {
                if (current.Values.All(static machine => machine.State is FleetUpdateMachineStates.Updated or FleetUpdateMachineStates.Superseded))
                    campaign = campaign with { State = FleetUpdateCampaignStates.Done, UpdatedAt = now };
                return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
            }

            var waiting = current.Values.Any(machine =>
            {
                var view = node(machine.DeviceId);
                return machine.State == FleetUpdateMachineStates.Pending && view.Online
                       || InProgress.Contains(machine.State) && (view.Online || now - LastTouched(machine) < limits.OfflineGrace);
            });
            if (waiting) return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);

            var updated = current.Values.Count(static machine => machine.State == FleetUpdateMachineStates.Updated);
            var failed = current.Values.Count(static machine => machine.State is FleetUpdateMachineStates.Failed or FleetUpdateMachineStates.RolledBack);
            if (updated == 0 && failed > 0)
            {
                // A version that failed on every node that tried it does not go to the server.
                campaign = campaign with
                {
                    State = FleetUpdateCampaignStates.Failed, ServerState = FleetUpdateMachineStates.Cancelled, UpdatedAt = now,
                    ServerMessage = "Every node that tried the version failed or rolled back: the server did not update.",
                };
                return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
            }

            campaign = campaign with { State = FleetUpdateCampaignStates.UpdatingServer, UpdatedAt = now };
        }

        if (campaign.State != FleetUpdateCampaignStates.UpdatingServer) return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
        if (Same(server.Running, campaign.TargetVersion))
        {
            var allUpdated = current.Values.All(static machine => machine.State is FleetUpdateMachineStates.Updated or FleetUpdateMachineStates.Superseded);
            campaign = campaign with
            {
                ServerState = FleetUpdateMachineStates.Updated, ServerMessage = null, UpdatedAt = now,
                State = allUpdated ? FleetUpdateCampaignStates.Done : FleetUpdateCampaignStates.PartlyDone,
            };
            return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
        }

        if (campaign.ServerState is FleetUpdateMachineStates.Instructed or FleetUpdateMachineStates.Switching
            && server.LastOutcome is { } outcome && Same(outcome.Version, campaign.TargetVersion) && outcome.At >= campaign.UpdatedAt.AddSeconds(-1)
            && outcome.Outcome is "rolled_back" or "failed")
        {
            campaign = campaign with
            {
                State = FleetUpdateCampaignStates.Failed, UpdatedAt = now,
                ServerState = outcome.Outcome == "rolled_back" ? FleetUpdateMachineStates.RolledBack : FleetUpdateMachineStates.Failed,
                ServerMessage = outcome.Message ?? $"The server went back to {server.Running}.",
            };
            return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
        }

        if (campaign.ServerState == FleetUpdateMachineStates.Pending)
            return new UpdateCampaignStep(campaign with { ServerState = FleetUpdateMachineStates.Instructed, UpdatedAt = now }, [.. changed.Values], instruct, true);
        return new UpdateCampaignStep(campaign, [.. changed.Values], instruct, false);
    }

    /// <summary>Applies the report of a node to its record.</summary>
    /// <param name="machine">The record.</param>
    /// <param name="report">The report.</param>
    /// <param name="now">The time.</param>
    /// <returns>The record after it; the same one when the report changes nothing.</returns>
    public static UpdateCampaignMachineRecord Apply(UpdateCampaignMachineRecord machine, FleetUpdateReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(report);
        if (!FleetUpdateMachineStates.IsReported(report.State) || report.CampaignId != machine.CampaignId) return machine;
        // A node that reports the cancelled campaign done (cancelled, or ended otherwise) acknowledged its cancellation.
        if (machine.CancelPending && FleetUpdateMachineStates.IsTerminal(report.State))
        {
            var state = report.State == FleetUpdateMachineStates.Updated ? FleetUpdateMachineStates.Updated : machine.State;
            return machine with { State = state, CancelPending = false, Running = report.Running, Message = Trim(report.Message), UpdatedAt = now };
        }

        // A machine done with the campaign stays so; one that switched before a cancellation reached it is updated still.
        if (FleetUpdateMachineStates.IsTerminal(machine.State) && !(report.State == FleetUpdateMachineStates.Updated && machine.State == FleetUpdateMachineStates.Cancelled))
            return machine;
        if (machine.State == report.State && machine.Running == report.Running && machine.Message == report.Message) return machine;
        return machine with { State = report.State, Running = report.Running, Message = Trim(report.Message), UpdatedAt = now };
    }

    /// <summary>
    /// Cancels a campaign before the server's step: the machines not done with it are cancelled, and those that were asked
    /// already keep the cancellation due until they acknowledge it (<see cref="UpdateCampaignMachineRecord.CancelPending"/>).
    /// </summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="machines">Its machines.</param>
    /// <param name="now">The time.</param>
    /// <returns>The campaign, the machines cancelled, and those that were asked already (to tell them); null when it is too late.</returns>
    public static (UpdateCampaignRecord Campaign, IReadOnlyList<UpdateCampaignMachineRecord> Machines, IReadOnlyList<string> Asked)? Cancel(UpdateCampaignRecord campaign,
        IReadOnlyList<UpdateCampaignMachineRecord> machines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(machines);
        if (campaign.State is not (FleetUpdateCampaignStates.Scheduled or FleetUpdateCampaignStates.UpdatingNodes)) return null;
        var (cancelled, asked) = CancelMachines(machines, now, null);
        return (campaign with { State = FleetUpdateCampaignStates.Cancelled, ServerState = FleetUpdateMachineStates.Cancelled, UpdatedAt = now }, cancelled, asked);
    }

    /// <summary>
    /// Withdraws a campaign the update policy no longer allows: one before the server's step is cancelled; one partly done
    /// keeps what is done, and its machines not done with it are cancelled. The machines asked already keep the cancellation
    /// due until they acknowledge it.
    /// </summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="machines">Its machines.</param>
    /// <param name="now">The time.</param>
    /// <param name="message">Why, for the campaign and its machines.</param>
    /// <returns>The campaign, the machines cancelled, and those that were asked already; null when nothing is left to withdraw.</returns>
    public static (UpdateCampaignRecord Campaign, IReadOnlyList<UpdateCampaignMachineRecord> Machines, IReadOnlyList<string> Asked)? Revoke(UpdateCampaignRecord campaign,
        IReadOnlyList<UpdateCampaignMachineRecord> machines, DateTimeOffset now, string message)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (campaign.State is FleetUpdateCampaignStates.Scheduled or FleetUpdateCampaignStates.UpdatingNodes)
        {
            var (all, askedAll) = CancelMachines(machines, now, message);
            return (campaign with { State = FleetUpdateCampaignStates.Cancelled, ServerState = FleetUpdateMachineStates.Cancelled, ServerMessage = message, UpdatedAt = now },
                all, askedAll);
        }

        if (campaign.State != FleetUpdateCampaignStates.PartlyDone) return null;
        var (left, asked) = CancelMachines(machines, now, message);
        return left.Length == 0 ? null : (campaign with { UpdatedAt = now }, left, asked);
    }

    private static (UpdateCampaignMachineRecord[] Cancelled, string[] Asked) CancelMachines(IReadOnlyList<UpdateCampaignMachineRecord> machines, DateTimeOffset now,
        string? message)
    {
        var cancelled = machines.Where(static machine => !FleetUpdateMachineStates.IsTerminal(machine.State))
            .Select(machine => machine with
            {
                State = FleetUpdateMachineStates.Cancelled, CancelPending = InProgress.Contains(machine.State), Message = message ?? machine.Message, UpdatedAt = now,
            }).ToArray();
        return (cancelled, [.. cancelled.Where(static machine => machine.CancelPending).Select(static machine => machine.DeviceId)]);
    }

    /// <summary>Gets whether two versions are the same, once normalized.</summary>
    /// <param name="left">A version, or null.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether both read and are equal.</returns>
    public static bool Same(string? left, string? right)
        => UpdateVersion.TryParse(left, out var a) && UpdateVersion.TryParse(right, out var b) && a.Equals(b);

    private static DateTimeOffset LastTouched(UpdateCampaignMachineRecord machine)
        => machine.InstructedAt is { } sent && sent > machine.UpdatedAt ? sent : machine.UpdatedAt;

    private static string? Trim(string? message) => message is { Length: > 1000 } ? message[..1000] : message;
}
