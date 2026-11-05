using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// `alta fleet policy` (doc/fleet/security-and-privacy.md, policies and the kill switch): the signed policy of a server and its
// kill switch, read only for agents. A person changes the rules or stops the work of the fleet in Settings > Fleet > Policies,
// signed with their computer's key; no command signs.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] FleetPolicyPolicies =
    [
        Read("fleet policy show"),
    ];

    private static Command CreateFleetPolicyCommand(AltaCommandContext context)
    {
        var group = Group("policy", "Read the signed policy of a CodeAlta Server: the rules of the organization for the work of the fleet, and whether its kill switch stops it.");
        string? server = null;
        var show = Leaf("show", "Say the rules of the policy this computer verified, and the stop of the kill switch while one holds.");
        show.Add("server=", "The server: its link id or its address. Defaults to the one online.", value => server = value);
        show.Add(async (_, _) => await WithFleetFloorAsync(context, async () =>
        {
            if (!context.TryGetRequired<IAltaFleetPolicy>(nameof(IAltaFleetPolicy), out var policies)) return AltaExitCodes.ServiceUnavailable;
            var policy = await policies.ReadPolicyAsync(NormalizeOptionalText(server), context.CancellationToken).ConfigureAwait(false);
            if (policy.Status != "ok") return FleetFailure(context, policy.Status, policy.Message, null);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.policy",
                version = 1,
                correlationId = context.CorrelationId,
                server = policy.Server,
                number = policy.Number,
                signedAt = policy.SignedAt,
                rules = policy.Rules,
                stop = policy.Stop is { } stop ? new { scope = stop.Scope, names = stop.Names, reason = stop.Reason } : null,
                stopsThisComputer = policy.StopsThisComputer,
                refused = policy.Refused,
                note = policy.Stop is not null
                    ? "The work of the fleet is stopped where the stop reaches: start nothing on those machines, tell the user, and wait for a person to lift it in Settings > Fleet > Policies."
                    : "The policy is read only for agents: a person changes it in Settings > Fleet > Policies.",
            });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(show);
        AddHelpText(group, "Machines verify the policy themselves and refuse what it does not allow, with the reason: read it before starting work on other machines.",
            "Example: `alta fleet policy show`.");
        return group;
    }
}
