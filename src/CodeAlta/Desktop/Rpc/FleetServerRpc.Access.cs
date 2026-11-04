using System.Text;
using System.Text.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// People and access of a server (doc/fleet/access-control.md), for Settings > Fleet > People and access: people and their
// roles, invitations, teams and their machines, grants, owners and transfers, the changes agents proposed, deprovisioning,
// who can do what, and the signed access document, which this computer signs when its person is a signer and agrees to
// the changes this computer itself read.
internal sealed partial class FleetServerService
{
    private const int MaxSignedChanges = 500;
    private const int MaxShareableSessions = 100;

    /// <summary>People and access at a server, as this computer's person may see them.</summary>
    [NeoRpcMethod("access")]
    public async Task<FleetAccessOverview> AccessAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetAccessOverview.Failed(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return FleetAccessOverview.Failed("invalid_request", null);
        try
        {
            var read = await _servers!.ReadAccessAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            var connection = _servers.Connections.FirstOrDefault(connection => connection.Link.Id == request.LinkId);
            return AccessView(read, connection?.Link.Url, Shareable(connection, read));
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return FleetAccessOverview.Failed(failure.Status, failure.Message);
        }
    }

    /// <summary>Invites a person with a role: the code their computer joins with, shown once, and the address of the server.</summary>
    [NeoRpcMethod("invitePerson")]
    public async Task<FleetAccessInvitation> InvitePersonAsync(FleetAccessInviteRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64
            || request.Role is { } role && !FleetOrganizationRoles.IsKnown(role))
            return new("invalid_request", null, null, null, null);
        try
        {
            var invitation = await _servers!.AccessCallAsync(request.LinkId,
                (client, token) => client.InvitePersonAsync(new InvitePersonRequest(request.Name.Trim(), request.Role), token), cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", null, invitation.Token, invitation.ExpiresAt, _servers.Connections.FirstOrDefault(connection => connection.Link.Id == request.LinkId)?.Link.ShareableUrl);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, null, null, null);
        }
    }

    /// <summary>Changes a person's role.</summary>
    [NeoRpcMethod("setPersonRole")]
    public Task<FleetSecretActionResult> SetPersonRoleAsync(FleetAccessRoleRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetOrganizationRoles.IsKnown(request!.Role) || string.IsNullOrEmpty(request.PersonId)) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.SetPersonRoleAsync(request.PersonId, request.Role, token), cancellationToken)
                .ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>
    /// Deprovisions a person who leaves: suspended at once, what they owned given to the successor, paused, and their devices
    /// signed out of the device list by this computer. Machines stop their work once the access document is signed.
    /// </summary>
    [NeoRpcMethod("deprovisionPerson")]
    public async Task<FleetAccessDeprovisionResult> DeprovisionPersonAsync(FleetAccessDeprovisionRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, 0, []);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || string.IsNullOrEmpty(request.PersonId)
            || request.SuccessorKind is not (null or FleetPrincipalKinds.Person or FleetPrincipalKinds.Team or FleetPrincipalKinds.Organization))
            return new("invalid_request", null, 0, []);
        try
        {
            var (result, left) = await _servers!.DeprovisionAsync(request.LinkId, request.PersonId, new DeprovisionRequest(request.SuccessorKind, request.SuccessorId), cancellationToken)
                .ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", null, result.Transfers.Count, [.. left]);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, 0, []);
        }
    }

    /// <summary>Makes a team, of which this computer's person is the first maintainer.</summary>
    [NeoRpcMethod("createTeam")]
    public Task<FleetSecretActionResult> CreateTeamAsync(FleetAccessNameRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrWhiteSpace(request!.Name) || request.Name.Length > 64) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.CreateTeamAsync(request.Name.Trim(), token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Renames a team, or changes whether its machines run the automations of its members.</summary>
    [NeoRpcMethod("updateTeam")]
    public Task<FleetSecretActionResult> UpdateTeamAsync(FleetAccessTeamRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.TeamId) || request.Name is { Length: 0 or > 64 }) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.UpdateTeamAsync(request.TeamId, new UpdateTeamRequest(request.Name?.Trim(),
                request.RunsMemberAutomations), token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Dissolves a team: what it owned goes to the organization.</summary>
    [NeoRpcMethod("dissolveTeam")]
    public Task<FleetSecretActionResult> DissolveTeamAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => AccessRefAsync(request, (client, id, token) => client.DissolveTeamAsync(id, token), cancellationToken);

    /// <summary>Sets a person's role in a team, or takes them out with no role.</summary>
    [NeoRpcMethod("setTeamMember")]
    public Task<FleetSecretActionResult> SetTeamMemberAsync(FleetAccessTeamMemberRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.TeamId) || string.IsNullOrEmpty(request.PersonId) || request.Role is { } role && !FleetTeamRoles.IsKnown(role))
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.SetTeamMemberAsync(request.TeamId, request.PersonId, request.Role, token), cancellationToken)
                .ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Sets the on-call rotation of a team, its people in turn; no people take it away.</summary>
    [NeoRpcMethod("setOnCall")]
    public Task<FleetSecretActionResult> SetOnCallAsync(FleetAccessOnCallRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.TeamId) || request.People is { Length: > 50 } || request.People?.Any(string.IsNullOrWhiteSpace) == true
                || request.People is { Length: > 0 } && request.ShiftHours is < 1 or > 168)
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => request.People is { Length: > 0 } people
                ? client.SetOnCallAsync(request.TeamId, new PutOnCallRotationRequest(people, request.ShiftHours), token)
                : client.ClearOnCallAsync(request.TeamId, token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Makes a grant.</summary>
    [NeoRpcMethod("createGrant")]
    public Task<FleetSecretActionResult> CreateGrantAsync(FleetAccessGrantRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetPrincipalKinds.CanBeGranted(request!.SubjectKind) || string.IsNullOrEmpty(request.SubjectId) || !FleetCapabilities.IsKnown(request.Capability)
                || !FleetScopeKinds.IsKnown(request.ScopeKind))
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.CreateGrantAsync(new CreateGrantRequest(request.SubjectKind, request.SubjectId,
                request.Capability, request.ScopeKind, request.ScopeId, null, request.Sealed), token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Takes a grant back.</summary>
    [NeoRpcMethod("revokeGrant")]
    public Task<FleetSecretActionResult> RevokeGrantAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => AccessRefAsync(request, (client, id, token) => client.RevokeGrantAsync(id, token), cancellationToken);

    /// <summary>Gives a grant of sealed content its keys, as a computer of their owner: sealed here and wrapped to each device of the grantee.</summary>
    [NeoRpcMethod("confirmGrantKeys")]
    public Task<FleetSecretActionResult> ConfirmGrantKeysAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.Id)) return new FleetSecretActionResult("invalid_request", null);
            try
            {
                await _servers!.ConfirmGrantKeysAsync(request.LinkId, request.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                return new FleetSecretActionResult("keys_unavailable", exception.Message);
            }

            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Offers a machine or an automation to another owner.</summary>
    [NeoRpcMethod("offerTransfer")]
    public Task<FleetSecretActionResult> OfferTransferAsync(FleetAccessTransferRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetResourceKinds.Transfers(request!.ResourceKind) || string.IsNullOrEmpty(request.ResourceId) || !FleetPrincipalKinds.CanOwn(request.ToKind))
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.OfferTransferAsync(request.LinkId, new OfferTransferRequest(request.ResourceKind, request.ResourceId, request.ToKind, request.ToId ?? string.Empty),
                cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Accepts, declines or cancels a transfer.</summary>
    [NeoRpcMethod("decideTransfer")]
    public Task<FleetSecretActionResult> DecideTransferAsync(FleetAccessDecideRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.Id) || request.Verb is not ("accept" or "decline" or "cancel")) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.DecideTransferAsync(request.Id, request.Verb, token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Resumes a resource that waits for its owner.</summary>
    [NeoRpcMethod("resumeResource")]
    public Task<FleetSecretActionResult> ResumeResourceAsync(FleetAccessResourceRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetResourceKinds.IsKnown(request!.Kind) || string.IsNullOrEmpty(request.Id)) return new FleetSecretActionResult("invalid_request", null);
            // An automation handed over is taken here first: this computer then signs and seals its versions for the new owner's machines.
            return await _servers!.ResumeResourceAsync(request.LinkId, request.Kind, request.Id, cancellationToken).ConfigureAwait(false) is { } problem
                ? new FleetSecretActionResult("refused", problem)
                : new FleetSecretActionResult("ok", null);
        });

    /// <summary>Makes a service identity of a team or of the organization, disabled.</summary>
    [NeoRpcMethod("createServiceIdentity")]
    public Task<FleetSecretActionResult> CreateServiceIdentityAsync(FleetAccessIdentityRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrWhiteSpace(request!.Name) || request.Name.Length > 64 || request.OwnerKind is not (FleetPrincipalKinds.Team or FleetPrincipalKinds.Organization))
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.CreateServiceIdentityAsync(new CreateServiceIdentityRequest(request.Name.Trim(),
                request.OwnerKind, request.OwnerId ?? string.Empty), token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Enables or disables a service identity.</summary>
    [NeoRpcMethod("enableServiceIdentity")]
    public Task<FleetSecretActionResult> EnableServiceIdentityAsync(FleetAccessEnableRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.Id)) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.EnableServiceIdentityAsync(request.Id, request.Enabled, token), cancellationToken)
                .ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>
    /// Applies or dismisses a change an agent proposed: applying makes the change with this computer's person's own request
    /// (a role, a team member, a grant, a transfer, a team), then records it; the server checks it as any change of theirs.
    /// </summary>
    [NeoRpcMethod("decideProposal")]
    public Task<FleetSecretActionResult> DecideProposalAsync(FleetAccessProposalDecision request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.Id)) return new FleetSecretActionResult("invalid_request", null);
            var read = await _servers!.ReadAccessAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            if (read.Proposals.FirstOrDefault(proposal => proposal.Id == request.Id) is not { State: FleetAccessProposalStates.Open } proposal)
                return new FleetSecretActionResult("conflict", null);
            if (request.Apply && await ApplyProposalAsync(request.LinkId, proposal, cancellationToken).ConfigureAwait(false) is { } refusal) return refusal;
            await _servers.AccessCallAsync(request.LinkId, (client, token) => client.DecideAccessProposalAsync(request.Id,
                request.Apply ? FleetAccessProposalStates.Applied : FleetAccessProposalStates.Dismissed, token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>
    /// Reads what the next version of the access document changes, as this computer verifies the chain and reads the draft:
    /// none when nothing waits, or this computer's person is no signer (status <c>forbidden</c>).
    /// </summary>
    [NeoRpcMethod("prepareAccessDocument")]
    public async Task<FleetAccessDocumentPreview> PrepareAccessDocumentAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], 0);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, [], 0);
        try
        {
            var draft = await _servers!.PrepareAccessDocumentAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            return draft is null ? new("ok", null, [], 0) : new("ok", null, [.. draft.Changes], (int)Math.Min(draft.Document.Number, int.MaxValue));
        }
        catch (FleetAccessDocumentException exception)
        {
            return new(FleetAccessDocumentInvalid, exception.Message, [], 0);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, [], 0);
        }
    }

    /// <summary>
    /// Signs the next version of the access document with this computer's key, when it changes what the person read and
    /// agreed to (<paramref name="request"/>'s changes): otherwise nothing is signed and the status is <c>changed</c>.
    /// </summary>
    [NeoRpcMethod("signAccessDocument")]
    public Task<FleetSecretActionResult> SignAccessDocumentAsync(FleetAccessSignRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Changes is null || request.Changes.Length > MaxSignedChanges) return new FleetSecretActionResult("invalid_request", null);
            FleetAccessDraft? draft;
            try
            {
                draft = await _servers!.PrepareAccessDocumentAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetAccessDocumentException exception)
            {
                return new FleetSecretActionResult(FleetAccessDocumentInvalid, exception.Message);
            }

            if (draft is null) return new FleetSecretActionResult("ok", null);
            if (!draft.Changes.SequenceEqual(request.Changes, StringComparer.Ordinal)) return new FleetSecretActionResult("changed", null);
            await _servers.SignAccessDocumentAsync(request.LinkId, draft, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Who can do what at a server, as CSV text for an access review; for owners, admins and auditors.</summary>
    [NeoRpcMethod("accessReview")]
    public async Task<FleetAccessReview> AccessReviewAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, 0);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, null, 0);
        try
        {
            var review = await _servers!.AccessCallAsync(request.LinkId, static (client, token) => client.GetAccessReviewAsync(token), cancellationToken).ConfigureAwait(false);
            return new("ok", null, ReviewCsv(review), review.Rows.Count);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, null, 0);
        }
    }

    private const string FleetAccessDocumentInvalid = "access_document.invalid";

    /// <summary>Who can do what as CSV: one line per principal, capability and scope, with where it comes from.</summary>
    internal static string ReviewCsv(AccessReviewDto review)
    {
        var builder = new StringBuilder("principal_kind,principal_id,principal,capability,scope_kind,scope_id,source\r\n");
        foreach (var row in review.Rows)
        {
            builder.Append(string.Join(',', new[] { row.PrincipalKind, row.PrincipalId, row.PrincipalName, row.Capability, row.ScopeKind, row.ScopeId ?? string.Empty, row.Source }
                .Select(static value => Csv(value)))).Append("\r\n");
        }

        return builder.ToString();

        // A cell that starts like a formula is quoted with a leading apostrophe, so that a spreadsheet does not run it.
        static string Csv(string value)
        {
            var text = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
            return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
        }
    }

    /// <summary>
    /// The sessions this person may share sealed: on their own machines or started by them, whose title opens on this computer
    /// (a grant of their content gives the grantee that title, sealed again for them here).
    /// </summary>
    internal static FleetAccessSessionView[] Shareable(FleetServerConnection? connection, DesktopFleetAccess read)
    {
        if (connection?.Connector is not { MetadataKeys: { } keys } connector) return [];
        var me = read.Self.Person.Id;
        var mine = read.Owners.Where(owner => owner.ResourceKind == FleetResourceKinds.Machine && owner.OwnerKind == FleetPrincipalKinds.Person && owner.OwnerId == me)
            .Select(static owner => owner.ResourceId).ToHashSet(StringComparer.Ordinal);
        var directory = connector.Directory;
        return [.. directory.Sessions.Values.Where(session => mine.Contains(session.MachineId) || session.Fact.PersonId == me)
            .OrderByDescending(static session => session.Fact.UpdatedAt)
            .Select(session => keys.TryOpen(session.Fact.SessionId, session.Fact.SealedMetadata, out var metadata) && metadata?.Title is { Length: > 0 } title
                ? new FleetAccessSessionView(session.Fact.SessionId, title,
                    directory.Machines.TryGetValue(session.MachineId, out var machine) ? machine.Inventory?.Name ?? session.MachineId : session.MachineId)
                : null)
            .OfType<FleetAccessSessionView>().Take(MaxShareableSessions)];
    }

    /// <summary>The page's view of what the server holds: names resolved, what this person may act on marked.</summary>
    internal static FleetAccessOverview AccessView(DesktopFleetAccess read, string? serverUrl) => AccessView(read, serverUrl, []);

    /// <summary>The page's view of what the server holds, with the sessions this person may share sealed.</summary>
    internal static FleetAccessOverview AccessView(DesktopFleetAccess read, string? serverUrl, FleetAccessSessionView[] sessions)
    {
        var titles = sessions.ToDictionary(static session => session.Id, static session => session.Title, StringComparer.Ordinal);
        var me = read.Self.Person;
        var administers = FleetOrganizationRoles.Administers(me.Role);
        var maintains = new HashSet<string>(me.Teams.Where(static team => team.Role == FleetTeamRoles.Maintainer).Select(static team => team.TeamId), StringComparer.Ordinal);
        var people = read.People.ToDictionary(static person => person.Id, static person => person.DisplayName, StringComparer.Ordinal);
        var teams = read.Teams.ToDictionary(static team => team.Id, static team => team.Name, StringComparer.Ordinal);
        string Device(string id) => read.Devices.TryGetValue(id, out var device) ? device.Name : id;
        string Named(string kind, string id) => kind switch
        {
            FleetPrincipalKinds.Person => people.GetValueOrDefault(id, id),
            FleetPrincipalKinds.Team => teams.GetValueOrDefault(id, id),
            FleetPrincipalKinds.Service => read.Identities.FirstOrDefault(identity => identity.Id == id)?.Name ?? id,
            FleetPrincipalKinds.Organization => "Organization",
            _ => id,
        };
        bool Answers(string kind, string id) => kind switch
        {
            FleetPrincipalKinds.Person => id == me.Id,
            FleetPrincipalKinds.Team => maintains.Contains(id),
            FleetPrincipalKinds.Organization => administers,
            _ => false,
        };
        string ScopeName(string kind, string? id) => kind switch
        {
            FleetScopeKinds.Organization => "Organization",
            FleetScopeKinds.Team when id is not null => teams.GetValueOrDefault(id, id),
            FleetScopeKinds.Resource when id is not null && id.StartsWith("machine:", StringComparison.Ordinal) => Device(id["machine:".Length..]),
            FleetScopeKinds.Resource when id is not null && id.StartsWith("session:", StringComparison.Ordinal) => titles.GetValueOrDefault(id["session:".Length..], id),
            _ => id ?? string.Empty,
        };
        FleetAccessPersonView Person(PersonAccessDto person) => new(person.Id, person.DisplayName, person.Role, person.Status, person.Devices,
            [.. person.Teams.Select(static team => new FleetAccessMembershipView(team.TeamId, team.TeamName, team.Role))], person.Devices > 0, person.Id == me.Id);
        return new FleetAccessOverview("ok", null, Person(me), [.. read.Self.Capabilities], read.Self.Signer, read.Self.Advanced,
            new FleetAccessDocumentView((int)Math.Min(read.Self.Document.Number, int.MaxValue), read.Self.Document.SignedBy, read.Self.Document.SignedAt, [.. read.Self.Document.Pending]),
            [.. read.People.Select(Person)],
            [.. read.Teams.Select(team => new FleetAccessTeamView(team.Id, team.Name,
                [.. team.Members.Select(static member => new FleetAccessMemberView(member.PersonId, member.DisplayName, member.Role))],
                [.. team.Machines.Select(machine => new FleetSecretTarget(machine, Device(machine)))], team.RunsMemberAutomations, administers || maintains.Contains(team.Id))
            {
                OnCall = team.OnCall is { } rotation ? new FleetAccessOnCallView([.. rotation.People.Select(id => people.GetValueOrDefault(id, id))], rotation.ShiftHours,
                    rotation.OnCallNow is { } now ? people.GetValueOrDefault(now, now) : null, rotation.Until) : null,
            })],
            [.. read.Identities.Select(identity => new FleetAccessIdentityView(identity.Id, identity.Name, identity.OwnerKind, Named(identity.OwnerKind, identity.OwnerId),
                identity.Enabled))],
            [.. read.Grants.Select(grant => new FleetAccessGrantView(grant.Id, grant.SubjectKind, grant.SubjectId, Named(grant.SubjectKind, grant.SubjectId), grant.Capability,
                grant.ScopeKind, grant.ScopeId, ScopeName(grant.ScopeKind, grant.ScopeId), grant.Status, grant.ExpiresAt))],
            [.. read.Owners.Select(owner => new FleetAccessOwnerView(owner.ResourceKind, owner.ResourceId,
                owner.Name ?? (owner.ResourceKind == FleetResourceKinds.Machine ? Device(owner.ResourceId) : owner.ResourceId), owner.OwnerKind, owner.OwnerId, owner.OwnerName,
                owner.AuthorId is { } author ? people.GetValueOrDefault(author, author) : null, owner.Paused, owner.OwnerSuspended,
                Answers(owner.OwnerKind, owner.OwnerId) || administers))],
            [.. read.Transfers.Select(transfer => new FleetAccessTransferView(transfer.Id, transfer.ResourceKind, transfer.ResourceId,
                transfer.ResourceName ?? (transfer.ResourceKind == FleetResourceKinds.Machine ? Device(transfer.ResourceId) : transfer.ResourceId),
                Named(transfer.FromKind, transfer.FromId), transfer.ToKind, transfer.ToId, Named(transfer.ToKind, transfer.ToId), transfer.State, transfer.Reason, transfer.OfferedAt,
                transfer.State == FleetTransferStates.Offered && Answers(transfer.ToKind, transfer.ToId),
                transfer.State == FleetTransferStates.Offered && (transfer.OfferedBy == me.Id || administers)))],
            [.. read.Proposals.Select(proposal => new FleetAccessProposalView(proposal.Id, people.GetValueOrDefault(proposal.PersonId, proposal.PersonId), proposal.SessionId,
                proposal.Kind, proposal.Summary, proposal.Payload, proposal.State, proposal.CreatedAt))],
            serverUrl, sessions);
    }

    // The change a proposal names, made with this person's own request; null once made, else why not.
    private async Task<FleetSecretActionResult?> ApplyProposalAsync(string linkId, AccessProposalDto proposal, CancellationToken cancellationToken)
    {
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(proposal.Payload);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new FleetSecretActionResult("invalid_request", "The proposal does not read.");
        }

        string? Text(string name) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (proposal.Kind)
        {
            case FleetAccessProposalKinds.Role when Text("personId") is { } person && Text("role") is { } role:
                await _servers!.AccessCallAsync(linkId, (client, token) => client.SetPersonRoleAsync(person, role, token), cancellationToken).ConfigureAwait(false);
                return null;
            case FleetAccessProposalKinds.TeamMember when Text("teamId") is { } team && Text("personId") is { } member:
                await _servers!.AccessCallAsync(linkId, (client, token) => client.SetTeamMemberAsync(team, member, Text("role"), token), cancellationToken).ConfigureAwait(false);
                return null;
            case FleetAccessProposalKinds.Grant when Text("subjectKind") is { } subjectKind && Text("subjectId") is { } subject && Text("capability") is { } capability
                && Text("scopeKind") is { } scopeKind:
                await _servers!.AccessCallAsync(linkId, (client, token) => client.CreateGrantAsync(new CreateGrantRequest(subjectKind, subject, capability, scopeKind, Text("scopeId")),
                    token), cancellationToken).ConfigureAwait(false);
                return null;
            case FleetAccessProposalKinds.Transfer when Text("resourceKind") is { } kind && Text("resourceId") is { } resource && Text("toKind") is { } toKind:
                await _servers!.AccessCallAsync(linkId, (client, token) => client.OfferTransferAsync(new OfferTransferRequest(kind, resource, toKind, Text("toId") ?? string.Empty),
                    token), cancellationToken).ConfigureAwait(false);
                return null;
            case FleetAccessProposalKinds.Team when Text("name") is { } name:
                await _servers!.AccessCallAsync(linkId, (client, token) => client.CreateTeamAsync(name, token), cancellationToken).ConfigureAwait(false);
                return null;
            default:
                return new FleetSecretActionResult("invalid_request", "The proposal does not say what it changes.");
        }
    }

    private Task<FleetSecretActionResult> AccessRefAsync<T>(FleetSecretRefRequest request, Func<FleetServerClient, string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (string.IsNullOrEmpty(request!.Id)) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.AccessCallAsync(request.LinkId, (client, token) => call(client, request.Id, token), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });
}

/// <summary>People and access at a server, for the page.</summary>
/// <param name="Status"><c>ok</c>, or why it could not be read.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Me">This computer's person.</param>
/// <param name="Capabilities">What they may do at the organization's scope.</param>
/// <param name="Signer">Whether their devices sign the access document.</param>
/// <param name="Advanced">Whether the organization uses teams, grants or roles beyond owner and members.</param>
/// <param name="Document">Where the access document stands.</param>
/// <param name="People">The people.</param>
/// <param name="Teams">The teams.</param>
/// <param name="Identities">The service identities.</param>
/// <param name="Grants">The grants.</param>
/// <param name="Owners">The resources and their owners.</param>
/// <param name="Transfers">The transfers.</param>
/// <param name="Proposals">The changes agents proposed.</param>
/// <param name="ServerUrl">The address of the server, which an invited person joins.</param>
/// <param name="Sessions">The sessions this person may share sealed, their titles opened on this computer.</param>
internal sealed record FleetAccessOverview(string Status, string? Message, FleetAccessPersonView? Me, string[] Capabilities, bool Signer, bool Advanced,
    FleetAccessDocumentView? Document, FleetAccessPersonView[] People, FleetAccessTeamView[] Teams, FleetAccessIdentityView[] Identities, FleetAccessGrantView[] Grants,
    FleetAccessOwnerView[] Owners, FleetAccessTransferView[] Transfers, FleetAccessProposalView[] Proposals, string? ServerUrl, FleetAccessSessionView[] Sessions)
{
    /// <summary>Gets an overview of a failure.</summary>
    /// <param name="status">The status.</param>
    /// <param name="message">What was said.</param>
    /// <returns>The overview.</returns>
    public static FleetAccessOverview Failed(string status, string? message) => new(status, message, null, [], false, false, null, [], [], [], [], [], [], [], null, []);
}

/// <summary>A session this person may share with its content sealed.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Title">Its title, opened on this computer.</param>
/// <param name="MachineName">The machine it runs on.</param>
internal sealed record FleetAccessSessionView(string Id, string Title, string MachineName);

/// <summary>A person.</summary>
/// <param name="Id">Their id.</param>
/// <param name="Name">Their name.</param>
/// <param name="Role">Their role.</param>
/// <param name="Status"><c>active</c> or <c>suspended</c>.</param>
/// <param name="Devices">How many approved devices they have.</param>
/// <param name="Teams">Their teams.</param>
/// <param name="Joined">Whether a device of theirs joined: false for a person invited who has not.</param>
/// <param name="IsMe">Whether they are this computer's person.</param>
internal sealed record FleetAccessPersonView(string Id, string Name, string Role, string Status, int Devices, FleetAccessMembershipView[] Teams, bool Joined, bool IsMe);

/// <summary>A membership of a team.</summary>
/// <param name="TeamId">The team.</param>
/// <param name="TeamName">Its name.</param>
/// <param name="Role">The role in it.</param>
internal sealed record FleetAccessMembershipView(string TeamId, string TeamName, string Role);

/// <summary>A team.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Members">Its maintainers and members.</param>
/// <param name="Machines">Its team machines.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the automations of its members.</param>
/// <param name="CanManage">Whether this computer's person manages it.</param>
internal sealed record FleetAccessTeamView(string Id, string Name, FleetAccessMemberView[] Members, FleetSecretTarget[] Machines, bool RunsMemberAutomations, bool CanManage)
{
    /// <summary>Gets its on-call rotation; null for none.</summary>
    public FleetAccessOnCallView? OnCall { get; init; }
}

/// <summary>The on-call rotation of a team, as the page shows it.</summary>
/// <param name="People">The names of its people, in the order of their turns.</param>
/// <param name="ShiftHours">How many hours a turn lasts.</param>
/// <param name="OnCallNow">The name of who is on call now; null for no one.</param>
/// <param name="Until">When their turn ends.</param>
internal sealed record FleetAccessOnCallView(string[] People, int ShiftHours, string? OnCallNow, DateTimeOffset? Until);

/// <param name="TeamId">The team.</param>
/// <param name="People">Its people in the order of their turns; empty to take the rotation away.</param>
/// <param name="ShiftHours">How many hours a turn lasts.</param>
internal sealed record FleetAccessOnCallRequest(string? ExpectedEpoch, string LinkId, string TeamId, string[]? People, int ShiftHours);

/// <summary>A member of a team.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="Name">Their name.</param>
/// <param name="Role"><c>maintainer</c> or <c>member</c>.</param>
internal sealed record FleetAccessMemberView(string PersonId, string Name, string Role);

/// <summary>A service identity.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind"><c>team</c> or <c>organization</c>.</param>
/// <param name="OwnerName">The name of its owner.</param>
/// <param name="Enabled">Whether it may act.</param>
internal sealed record FleetAccessIdentityView(string Id, string Name, string OwnerKind, string OwnerName, bool Enabled);

/// <summary>A grant.</summary>
/// <param name="Id">Its id.</param>
/// <param name="SubjectKind">Who is granted.</param>
/// <param name="SubjectId">Their id.</param>
/// <param name="SubjectName">Their name.</param>
/// <param name="Capability">What they may do.</param>
/// <param name="ScopeKind">Where.</param>
/// <param name="ScopeId">The id of the scope.</param>
/// <param name="ScopeName">Its name.</param>
/// <param name="Status"><c>active</c> or <c>pending_keys</c>.</param>
/// <param name="ExpiresAt">When it stops applying.</param>
internal sealed record FleetAccessGrantView(string Id, string SubjectKind, string SubjectId, string SubjectName, string Capability, string ScopeKind, string? ScopeId, string ScopeName,
    string Status, DateTimeOffset? ExpiresAt);

/// <summary>A resource and its owner.</summary>
/// <param name="Kind"><c>machine</c> or <c>automation</c>.</param>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind">Who owns it.</param>
/// <param name="OwnerId">Their id.</param>
/// <param name="OwnerName">Their name.</param>
/// <param name="Author">Who made it.</param>
/// <param name="Paused">Whether it waits for its owner.</param>
/// <param name="OwnerSuspended">Whether its owner left.</param>
/// <param name="CanAct">Whether this computer's person answers for it: they may give it or resume it.</param>
internal sealed record FleetAccessOwnerView(string Kind, string Id, string Name, string OwnerKind, string OwnerId, string OwnerName, string? Author, bool Paused, bool OwnerSuspended,
    bool CanAct);

/// <summary>A transfer.</summary>
/// <param name="Id">Its id.</param>
/// <param name="ResourceKind">What kind of resource.</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="ResourceName">Its name.</param>
/// <param name="FromName">The owner it leaves.</param>
/// <param name="ToKind">The kind of owner it goes to.</param>
/// <param name="ToId">Their id.</param>
/// <param name="ToName">Their name.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Reason">Why.</param>
/// <param name="OfferedAt">When.</param>
/// <param name="Incoming">Whether this computer's person accepts or declines it.</param>
/// <param name="Outgoing">Whether this computer's person may cancel it.</param>
internal sealed record FleetAccessTransferView(string Id, string ResourceKind, string ResourceId, string ResourceName, string FromName, string ToKind, string ToId, string ToName,
    string State, string Reason, DateTimeOffset OfferedAt, bool Incoming, bool Outgoing);

/// <summary>A change an agent proposed.</summary>
/// <param name="Id">Its id.</param>
/// <param name="PersonName">The person the agent acts for.</param>
/// <param name="SessionId">The session that proposed it.</param>
/// <param name="Kind">What it changes.</param>
/// <param name="Summary">What it does and why.</param>
/// <param name="Payload">The change, as JSON.</param>
/// <param name="State">Where it stands.</param>
/// <param name="CreatedAt">When.</param>
internal sealed record FleetAccessProposalView(string Id, string PersonName, string? SessionId, string Kind, string Summary, string Payload, string State, DateTimeOffset CreatedAt);

/// <summary>Where the access document stands.</summary>
/// <param name="Number">Its last version.</param>
/// <param name="SignedBy">Who signed it.</param>
/// <param name="SignedAt">When.</param>
/// <param name="Pending">What machines do not see yet.</param>
internal sealed record FleetAccessDocumentView(int Number, string? SignedBy, DateTimeOffset? SignedAt, string[] Pending);

/// <summary>An invitation.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Token">The code the person joins with, shown once.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
/// <param name="ServerUrl">The address of the server.</param>
internal sealed record FleetAccessInvitation(string Status, string? Message, string? Token, DateTimeOffset? ExpiresAt, string? ServerUrl);

/// <summary>What a deprovisioning did.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Transferred">How many resources went to the successor.</param>
/// <param name="NotRevoked">The devices this computer could not sign out of the device list.</param>
internal sealed record FleetAccessDeprovisionResult(string Status, string? Message, int Transferred, string[] NotRevoked);

/// <summary>What the next version of the access document changes.</summary>
/// <param name="Status"><c>ok</c>, <c>forbidden</c> for a person who signs nothing, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Changes">The changes, as this computer reads them; empty when nothing waits.</param>
/// <param name="Number">The number of the version to sign.</param>
internal sealed record FleetAccessDocumentPreview(string Status, string? Message, string[] Changes, int Number);

/// <summary>Who can do what, as CSV.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Csv">The CSV text.</param>
/// <param name="Lines">How many lines it has, its header aside.</param>
internal sealed record FleetAccessReview(string Status, string? Message, string? Csv, int Lines);

/// <summary>A request to invite a person.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Name">Their name.</param>
/// <param name="Role">Their role; member when null.</param>
internal sealed record FleetAccessInviteRequest(string ExpectedEpoch, string LinkId, string Name, string? Role);

/// <summary>A request to change a role.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Role">The new role.</param>
internal sealed record FleetAccessRoleRequest(string ExpectedEpoch, string LinkId, string PersonId, string Role);

/// <summary>A request to deprovision a person.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="PersonId">The person.</param>
/// <param name="SuccessorKind">A person, a team, or null for the organization.</param>
/// <param name="SuccessorId">Their id.</param>
internal sealed record FleetAccessDeprovisionRequest(string ExpectedEpoch, string LinkId, string PersonId, string? SuccessorKind, string? SuccessorId);

/// <summary>A request with a name.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Name">The name.</param>
internal sealed record FleetAccessNameRequest(string ExpectedEpoch, string LinkId, string Name);

/// <summary>A request to change a team.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="TeamId">The team.</param>
/// <param name="Name">Its new name, or null.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the automations of its members, or null.</param>
internal sealed record FleetAccessTeamRequest(string ExpectedEpoch, string LinkId, string TeamId, string? Name, bool? RunsMemberAutomations);

/// <summary>A request to set a member of a team.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="TeamId">The team.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Role">Their role, or null to take them out.</param>
internal sealed record FleetAccessTeamMemberRequest(string ExpectedEpoch, string LinkId, string TeamId, string PersonId, string? Role);

/// <summary>A request to make a grant.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="SubjectKind">Who is granted.</param>
/// <param name="SubjectId">Their id.</param>
/// <param name="Capability">What they may do.</param>
/// <param name="ScopeKind">Where.</param>
/// <param name="ScopeId">The id of the scope.</param>
/// <param name="Sealed">Whether it opens content sealed from the server.</param>
internal sealed record FleetAccessGrantRequest(string ExpectedEpoch, string LinkId, string SubjectKind, string SubjectId, string Capability, string ScopeKind, string? ScopeId,
    bool Sealed);

/// <summary>A request to offer a transfer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="ResourceKind">The kind of resource.</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="ToKind">The kind of the new owner.</param>
/// <param name="ToId">Their id; null for the organization.</param>
internal sealed record FleetAccessTransferRequest(string ExpectedEpoch, string LinkId, string ResourceKind, string ResourceId, string ToKind, string? ToId);

/// <summary>A request to decide a transfer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The transfer.</param>
/// <param name="Verb"><c>accept</c>, <c>decline</c> or <c>cancel</c>.</param>
internal sealed record FleetAccessDecideRequest(string ExpectedEpoch, string LinkId, string Id, string Verb);

/// <summary>A request about one resource.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Kind">The kind of resource.</param>
/// <param name="Id">Its id.</param>
internal sealed record FleetAccessResourceRequest(string ExpectedEpoch, string LinkId, string Kind, string Id);

/// <summary>A request to make a service identity.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind"><c>team</c> or <c>organization</c>.</param>
/// <param name="OwnerId">The team; null for the organization.</param>
internal sealed record FleetAccessIdentityRequest(string ExpectedEpoch, string LinkId, string Name, string OwnerKind, string? OwnerId);

/// <summary>A request to enable or disable a service identity.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The identity.</param>
/// <param name="Enabled">Whether it may act.</param>
internal sealed record FleetAccessEnableRequest(string ExpectedEpoch, string LinkId, string Id, bool Enabled);

/// <summary>A request to apply or dismiss a proposal.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The proposal.</param>
/// <param name="Apply">Whether to apply it; false dismisses it.</param>
internal sealed record FleetAccessProposalDecision(string ExpectedEpoch, string LinkId, string Id, bool Apply);

/// <summary>A request to sign the next version of the access document.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Changes">The changes the person read and agreed to, as the preview gave them.</param>
internal sealed record FleetAccessSignRequest(string ExpectedEpoch, string LinkId, string[] Changes);
