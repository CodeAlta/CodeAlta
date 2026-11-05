using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Fleet;

// People and access for `alta fleet access` (doc/fleet/access-control.md, agents and access): reads, and proposals a person
// applies on the People and access page. Nothing here changes access.
internal sealed partial class DesktopAltaFleet : IAltaFleetAccess
{
    /// <inheritdoc />
    public async Task<AltaFleetAccess> ReadAccessAsync(string? server, CancellationToken cancellationToken)
    {
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null);
        try
        {
            var read = await servers.ReadAccessAsync(connection.Link.Id, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AccessReviewRowDto>? review = null;
            if (read.Self.Capabilities.Contains("access.review", StringComparer.Ordinal))
                review = (await servers.AccessCallAsync(connection.Link.Id, static (client, token) => client.GetAccessReviewAsync(token), cancellationToken).ConfigureAwait(false)).Rows;
            var people = read.People.ToDictionary(static person => person.Id, static person => person.DisplayName, StringComparer.Ordinal);
            var teams = read.Teams.ToDictionary(static team => team.Id, static team => team.Name, StringComparer.Ordinal);
            string Named(string kind, string id) => kind + ":" + (kind switch
            {
                FleetPrincipalKinds.Person => people.GetValueOrDefault(id, id),
                FleetPrincipalKinds.Team => teams.GetValueOrDefault(id, id),
                FleetPrincipalKinds.Service => read.Identities.FirstOrDefault(identity => identity.Id == id)?.Name ?? id,
                _ => id,
            });
            string DeviceName(string id) => read.Devices.TryGetValue(id, out var device) ? device.Name : id;
            AltaFleetPerson Person(PersonAccessDto person)
                => new(person.Id, person.DisplayName, person.Role, person.Status, [.. person.Teams.Select(static team => $"{team.TeamName} ({team.Role})")], person.Devices);
            return new AltaFleetAccess("ok", null)
            {
                Server = connection.Link.Url,
                Me = Person(read.Self.Person),
                Capabilities = read.Self.Capabilities,
                Signer = read.Self.Signer,
                Advanced = read.Self.Advanced,
                DocumentNumber = read.Self.Document.Number,
                Pending = read.Self.Document.Pending,
                People = [.. read.People.Select(Person)],
                Teams = [.. read.Teams.Select(team => new AltaFleetTeam(team.Id, team.Name,
                    [.. team.Members.Where(static member => member.Role == FleetTeamRoles.Maintainer).Select(static member => member.DisplayName)],
                    [.. team.Members.Where(static member => member.Role != FleetTeamRoles.Maintainer).Select(static member => member.DisplayName)],
                    [.. team.Machines.Select(DeviceName)], team.RunsMemberAutomations))],
                Grants = [.. read.Grants.Select(grant => new AltaFleetGrant(grant.Id, Named(grant.SubjectKind, grant.SubjectId), grant.Capability,
                    grant.ScopeKind + (grant.ScopeId is null ? string.Empty : ":" + grant.ScopeId), grant.Status, grant.ExpiresAt) { MissingKeys = grant.MissingKeys })],
                Identities = [.. read.Identities.Select(identity => new AltaFleetIdentity(identity.Id, identity.Name, Named(identity.OwnerKind, identity.OwnerId), identity.Enabled))],
                Signed = connection.Connector?.Access.Current is { } signed
                    ? new AltaFleetSignedAccess(signed.Number, signed.Teams.Values.ToDictionary(static team => team.Id, static team => (IReadOnlyList<string>)[.. team.Services ?? []]),
                        signed.Machines.ToDictionary(static pair => pair.Key, static pair => pair.Value), [.. signed.OrganizationMachines.Order(StringComparer.Ordinal)],
                        signed.OrganizationServices, [.. signed.Automations.Values.Select(static automation => new AltaFleetSignedAutomation(automation.Id,
                            automation.OwnerKind + ":" + automation.OwnerId, automation.Paused))])
                    : null,
                Owners = [.. read.Owners.Select(owner => new AltaFleetOwner(owner.ResourceKind, owner.ResourceId, owner.Name, owner.OwnerKind + ":" + owner.OwnerName, owner.Paused,
                    owner.OwnerSuspended))],
                Transfers = [.. read.Transfers.Select(transfer => new AltaFleetTransfer(transfer.Id, transfer.ResourceKind + ":" + (transfer.ResourceName ?? transfer.ResourceId),
                    Named(transfer.FromKind, transfer.FromId), Named(transfer.ToKind, transfer.ToId), transfer.State, transfer.Reason))],
                Review = review is null ? null : [.. review.Select(row => new AltaFleetAccessLine(row.PrincipalKind + ":" + row.PrincipalName, row.Capability,
                    row.ScopeKind + (row.ScopeId is null ? string.Empty : ":" + row.ScopeId), row.Source))],
                Proposals = [.. read.Proposals.Select(static proposal => new AltaFleetAccessProposal("ok", null, proposal.Id, proposal.Kind, proposal.Summary, proposal.State))],
            };
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AltaFleetAccessProposal> ProposeAccessAsync(string? server, string kind, string summary, string payload, string? sessionId, CancellationToken cancellationToken)
    {
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null, null, null, null, null);
        try
        {
            var proposal = await servers.AccessCallAsync(connection.Link.Id,
                (client, token) => client.ProposeAccessAsync(new CreateAccessProposalRequest(kind, summary, payload, sessionId), token), cancellationToken).ConfigureAwait(false);
            return new("ok", null, proposal.Id, proposal.Kind, proposal.Summary, proposal.State);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, null, null, null, null);
        }
    }
}
