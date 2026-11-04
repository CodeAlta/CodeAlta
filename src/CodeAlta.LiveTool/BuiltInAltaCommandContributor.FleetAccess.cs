using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// `alta fleet access` (doc/fleet/access-control.md, "Agents and access"): people, teams, roles, grants, owners and transfers
// are read only for agents. An agent proposes a change with `alta fleet access propose`; a person applies it or dismisses it
// on the People and access page, with their own device. No command here grants, transfers, changes a role or signs.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly string[] AccessProposalKinds = ["role", "team-member", "grant", "transfer", "team"];

    private static readonly AltaCommandPolicy[] FleetAccessPolicies =
    [
        Read("fleet access show"),
        Read("fleet access people"),
        Read("fleet access teams"),
        Read("fleet access grants"),
        Read("fleet access owners"),
        Read("fleet access transfers"),
        Read("fleet access review"),
        Read("fleet access proposals"),
        Read("fleet access identities"),
        Read("fleet access document"),
        // A proposal changes nothing until a person applies it; a sandboxed session makes none.
        Mutating("fleet access propose"),
    ];

    private static Command CreateFleetAccessCommand(AltaCommandContext context)
    {
        var group = Group("access", "Read people and access at a CodeAlta Server: roles, teams, grants, owners and transfers; propose a change for a person to apply.");
        group.Add(AccessLeaf(context, "show", "Say who the person of this computer is at the server, what they may do, and what changes wait for a signer.", WriteAccessShow));
        group.Add(AccessLeaf(context, "people", "List the people of the organization: their roles, status and teams.", WriteAccessPeople));
        group.Add(AccessLeaf(context, "teams", "List the teams: their maintainers, members and team machines.", WriteAccessTeams));
        group.Add(AccessLeaf(context, "grants", "List the grants the person of this computer may see.", WriteAccessGrants));
        group.Add(AccessLeaf(context, "owners", "List the machines and automations with their owners: those paused and those whose owner left first.", WriteAccessOwners));
        group.Add(AccessLeaf(context, "transfers", "List the transfers that concern the person of this computer.", WriteAccessTransfers));
        group.Add(AccessLeaf(context, "review", "List who can do what, for an access review: for owners, admins and auditors.", WriteAccessReview));
        group.Add(AccessLeaf(context, "proposals", "List the changes of access agents proposed, and whether a person applied them.", WriteAccessProposals));
        group.Add(AccessLeaf(context, "identities", "List the service identities automations of a team or of the organization run as, and whether each is on.", WriteAccessIdentities));
        group.Add(AccessLeaf(context, "document", "Say what the signed access document this computer verified gives machines: team and organization machines, the identities their automations run as, and the owners of automations.", WriteAccessDocument));
        group.Add(CreateFleetAccessProposeCommand(context));
        AddHelpText(
            group,
            "Access is read only for agents: a person changes roles, teams, grants and owners in Settings > Fleet > People and access, and signs what machines must know.",
            "Examples: `alta fleet access show`; `alta fleet access owners`; `alta fleet access review`.");
        return group;
    }

    private static Command AccessLeaf(AltaCommandContext context, string name, string description, Func<AltaCommandContext, AltaFleetAccess, int> write)
    {
        string? server = null;
        var command = Leaf(name, description);
        command.Add("server=", "The server: its link id or its address. Defaults to the one online.", value => server = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () =>
        {
            if (!context.TryGetRequired<IAltaFleetAccess>(nameof(IAltaFleetAccess), out var access)) return AltaExitCodes.ServiceUnavailable;
            var read = await access.ReadAccessAsync(NormalizeOptionalText(server), context.CancellationToken).ConfigureAwait(false);
            return read.Status != "ok" ? FleetFailure(context, read.Status, read.Message, null) : write(context, read);
        }).ConfigureAwait(false));
        return command;
    }

    private static Command CreateFleetAccessProposeCommand(AltaCommandContext context)
    {
        string? kind = null;
        string? summary = null;
        string? server = null;
        var command = Leaf("propose", "Propose a change of access for a person to apply: a role, a team member, a grant, a transfer or a team. It changes nothing by itself.");
        command.Add("<kind>", "What it changes: role, team-member, grant, transfer or team.", value => kind = value);
        command.Add("summary=", "What it does and why, in a sentence the person reads.", value => summary = value);
        command.Add("server=", "The server: its link id or its address. Defaults to the one online.", value => server = value);
        command.Add("stdin", "Read the change from stdin, as a JSON object (required).", _ => { });
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () =>
        {
            if (!context.TryGetRequired<IAltaFleetAccess>(nameof(IAltaFleetAccess), out var access)) return AltaExitCodes.ServiceUnavailable;
            var normalizedKind = NormalizeOptionalText(kind);
            if (normalizedKind is null || !AccessProposalKinds.Contains(normalizedKind, StringComparer.Ordinal))
                return InvalidAccessProposal(context, $"The kind is one of {string.Join(", ", AccessProposalKinds)}.");
            if (NormalizeOptionalText(summary) is not { } text) return InvalidAccessProposal(context, "--summary says what the change does and why.");
            var payload = (await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false)).Trim();
            if (payload.Length is 0 or > 8192 || !IsJsonObject(payload)) return InvalidAccessProposal(context, "The change is a JSON object on stdin, as the request it stands for (`alta fleet access propose --help`).");
            var proposed = await access.ProposeAccessAsync(NormalizeOptionalText(server), normalizedKind, text, payload, NormalizeOptionalText(context.Caller.SourceSessionId),
                context.CancellationToken).ConfigureAwait(false);
            if (proposed.Status != "ok") return FleetFailure(context, proposed.Status, proposed.Message, null);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.proposal",
                version = 1,
                correlationId = context.CorrelationId,
                id = proposed.Id,
                kind = proposed.Kind,
                summary = proposed.Summary,
                state = proposed.State,
                note = "A person applies or dismisses it in Settings > Fleet > People and access: tell the user.",
            });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        AddHelpText(
            command,
            "Payloads: role `{\"personId\":\"…\",\"role\":\"admin\"}`; team-member `{\"teamId\":\"…\",\"personId\":\"…\",\"role\":\"member\"}` (no role takes them out); "
            + "grant `{\"subjectKind\":\"person\",\"subjectId\":\"…\",\"capability\":\"automation.run\",\"scopeKind\":\"team\",\"scopeId\":\"…\"}`; "
            + "transfer `{\"resourceKind\":\"machine\",\"resourceId\":\"…\",\"toKind\":\"team\",\"toId\":\"…\"}`; team `{\"name\":\"Infra\"}`.",
            "Ids come from `alta fleet access people`, `teams` and `owners`.",
            "Example: `alta fleet access propose team-member --summary \"Max builds the release with the infra team\" --stdin` with `{\"teamId\":\"t1\",\"personId\":\"p3\",\"role\":\"member\"}`.");
        return command;
    }

    private static int InvalidAccessProposal(AltaCommandContext context, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "fleet.invalidProposal", AltaExitCodes.Usage, message);
        return AltaExitCodes.Usage;
    }

    private static int WriteAccessShow(AltaCommandContext context, AltaFleetAccess access)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.access",
            version = 1,
            correlationId = context.CorrelationId,
            server = access.Server,
            person = access.Me?.Name,
            personId = access.Me?.Id,
            role = access.Me?.Role,
            teams = access.Me?.Teams ?? [],
            capabilities = access.Capabilities,
            signer = access.Signer,
            advanced = access.Advanced,
            document = access.DocumentNumber,
            pending = access.Pending,
            note = access.Pending.Count > 0
                ? "Machines see these changes once a signer (an owner or an admin) signs them in Settings > Fleet > People and access."
                : "Access is read only for agents: propose a change with `alta fleet access propose`.",
        });
        return AltaExitCodes.Success;
    }

    private static int WriteAccessPeople(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var person in access.People)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.person", version = 1, correlationId = context.CorrelationId, id = person.Id, name = person.Name, role = person.Role,
                status = person.Status, teams = person.Teams, devices = person.Devices, joined = person.Devices > 0,
            });
        }

        WriteSummary(context, "alta.fleet.access.person.summary", access.People.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessTeams(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var team in access.Teams)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.team", version = 1, correlationId = context.CorrelationId, id = team.Id, name = team.Name, maintainers = team.Maintainers,
                members = team.Members, machines = team.Machines, runsMemberAutomations = team.RunsMemberAutomations,
            });
        }

        WriteSummary(context, "alta.fleet.access.team.summary", access.Teams.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessIdentities(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var identity in access.Identities)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.identity", version = 1, correlationId = context.CorrelationId, id = identity.Id, name = identity.Name, owner = identity.Owner,
                enabled = identity.Enabled,
            });
        }

        WriteSummary(context, "alta.fleet.access.identity.summary", access.Identities.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessDocument(AltaCommandContext context, AltaFleetAccess access)
    {
        var signed = access.Signed;
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.access.document", version = 1, correlationId = context.CorrelationId, number = signed?.Number ?? 0,
            teamServices = signed?.TeamServices, teamMachines = signed?.TeamMachines, organizationMachines = signed?.OrganizationMachines ?? [],
            organizationServices = signed?.OrganizationServices ?? [], pending = access.Pending,
        });
        foreach (var automation in signed?.Automations ?? [])
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.automation", version = 1, correlationId = context.CorrelationId, id = automation.Id, owner = automation.Owner, paused = automation.Paused,
            });
        }

        WriteSummary(context, "alta.fleet.access.automation.summary", signed?.Automations.Count ?? 0, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessGrants(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var grant in access.Grants)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.grant", version = 1, correlationId = context.CorrelationId, id = grant.Id, subject = grant.Subject, capability = grant.Capability,
                scope = grant.Scope, status = grant.Status, expiresAt = grant.ExpiresAt, missingKeys = grant.MissingKeys,
            });
        }

        WriteSummary(context, "alta.fleet.access.grant.summary", access.Grants.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessOwners(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var owner in access.Owners.OrderByDescending(static owner => owner.OwnerSuspended).ThenByDescending(static owner => owner.Paused))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.owner", version = 1, correlationId = context.CorrelationId, kind = owner.Kind, id = owner.Id, name = owner.Name, owner = owner.Owner,
                paused = owner.Paused, ownerSuspended = owner.OwnerSuspended,
            });
        }

        WriteSummary(context, "alta.fleet.access.owner.summary", access.Owners.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessTransfers(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var transfer in access.Transfers)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.transfer", version = 1, correlationId = context.CorrelationId, id = transfer.Id, resource = transfer.Resource, from = transfer.From,
                to = transfer.To, state = transfer.State, reason = transfer.Reason,
            });
        }

        WriteSummary(context, "alta.fleet.access.transfer.summary", access.Transfers.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessReview(AltaCommandContext context, AltaFleetAccess access)
    {
        if (access.Review is not { } lines) return FleetFailure(context, "forbidden", "Who can do what is read by owners, admins and auditors.", null);
        foreach (var line in lines)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.line", version = 1, correlationId = context.CorrelationId, principal = line.Principal, capability = line.Capability, scope = line.Scope,
                source = line.Source,
            });
        }

        WriteSummary(context, "alta.fleet.access.line.summary", lines.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int WriteAccessProposals(AltaCommandContext context, AltaFleetAccess access)
    {
        foreach (var proposal in access.Proposals)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.access.proposal", version = 1, correlationId = context.CorrelationId, id = proposal.Id, kind = proposal.Kind, summary = proposal.Summary,
                state = proposal.State,
            });
        }

        WriteSummary(context, "alta.fleet.access.proposal.summary", access.Proposals.Count, truncated: false);
        return AltaExitCodes.Success;
    }
}
