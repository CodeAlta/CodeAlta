using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// Access control on the REST API (doc/fleet/access-control.md): people, teams, service identities, grants, owners, transfers,
// who can do what, the proposals of agents and the signed access document.
public sealed partial class FleetServerClient
{
    private static FleetAccessApiJsonContext AccessJson => FleetAccessApiJsonContext.Default;

    /// <summary>Reads what this device's person is and may do in the organization.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The person, their capabilities and where the signed access document stands.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessSelfDto> GetAccessSelfAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/access/self", null, AccessJson.AccessSelfDto, cancellationToken);

    /// <summary>Lists the people of the organization.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The people.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<PersonAccessDto>> ListPeopleAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/people", null, AccessJson.AccessListDtoPersonAccessDto, cancellationToken);

    /// <summary>Invites a person.</summary>
    /// <param name="request">Who, which role, how long the code works.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The person and the code they join with, shown once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<InvitationDto> InvitePersonAsync(InvitePersonRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/people", Body(request, AccessJson.InvitePersonRequest), AccessJson.InvitationDto, cancellationToken);
    }

    /// <summary>Changes a person's role.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="role">The new role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The person.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<PersonAccessDto> SetPersonRoleAsync(string personId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentException.ThrowIfNullOrEmpty(role);
        return SendAsync(HttpMethod.Put, $"api/v1/people/{Uri.EscapeDataString(personId)}/role", Body(new SetRoleRequest(role), AccessJson.SetRoleRequest),
            AccessJson.PersonAccessDto, cancellationToken);
    }

    /// <summary>Deprovisions a person who leaves.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="request">The successor of what they owned.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was done, and the devices to sign out of the device list.</returns>
    /// <exception cref="ArgumentException"><paramref name="personId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeprovisionResultDto> DeprovisionPersonAsync(string personId, DeprovisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/people/{Uri.EscapeDataString(personId)}/deprovision", Body(request, AccessJson.DeprovisionRequest),
            AccessJson.DeprovisionResultDto, cancellationToken);
    }

    /// <summary>Lists the teams.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The teams.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<TeamDto>> ListTeamsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/teams", null, AccessJson.AccessListDtoTeamDto, cancellationToken);

    /// <summary>Makes a team.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TeamDto> CreateTeamAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SendAsync(HttpMethod.Post, "api/v1/teams", Body(new CreateTeamRequest(name), AccessJson.CreateTeamRequest), AccessJson.TeamDto, cancellationToken);
    }

    /// <summary>Renames a team, or changes whether its machines run the automations of its members.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="request">What changes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team.</returns>
    /// <exception cref="ArgumentException"><paramref name="teamId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TeamDto> UpdateTeamAsync(string teamId, UpdateTeamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Patch, $"api/v1/teams/{Uri.EscapeDataString(teamId)}", Body(request, AccessJson.UpdateTeamRequest), AccessJson.TeamDto, cancellationToken);
    }

    /// <summary>Sets the on-call rotation of a team: its people in turn, each for a shift of hours.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="request">The people and the length of a turn.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team with its rotation.</returns>
    /// <exception cref="ArgumentException"><paramref name="teamId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TeamDto> SetOnCallAsync(string teamId, PutOnCallRotationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Put, $"api/v1/teams/{Uri.EscapeDataString(teamId)}/oncall", Body(request, AccessJson.PutOnCallRotationRequest), AccessJson.TeamDto, cancellationToken);
    }

    /// <summary>Takes the on-call rotation of a team away.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team.</returns>
    /// <exception cref="ArgumentException"><paramref name="teamId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TeamDto> ClearOnCallAsync(string teamId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        return SendAsync(HttpMethod.Delete, $"api/v1/teams/{Uri.EscapeDataString(teamId)}/oncall", null, AccessJson.TeamDto, cancellationToken);
    }

    /// <summary>Dissolves a team: what it owned goes to the organization.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transfers made.</returns>
    /// <exception cref="ArgumentException"><paramref name="teamId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<TransferDto>> DissolveTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        return SendAsync(HttpMethod.Delete, $"api/v1/teams/{Uri.EscapeDataString(teamId)}", null, AccessJson.AccessListDtoTransferDto, cancellationToken);
    }

    /// <summary>Sets a person's role in a team, or takes them out with a null role.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="personId">The person.</param>
    /// <param name="role">Their role, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team.</returns>
    /// <exception cref="ArgumentException">An id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TeamDto> SetTeamMemberAsync(string teamId, string personId, string? role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        var path = $"api/v1/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(personId)}";
        return role is null
            ? SendAsync(HttpMethod.Delete, path, null, AccessJson.TeamDto, cancellationToken)
            : SendAsync(HttpMethod.Put, path, Body(new SetTeamMemberRequest(role), AccessJson.SetTeamMemberRequest), AccessJson.TeamDto, cancellationToken);
    }

    /// <summary>Lists the service identities.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identities.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<ServiceIdentityDto>> ListServiceIdentitiesAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/service-identities", null, AccessJson.AccessListDtoServiceIdentityDto, cancellationToken);

    /// <summary>Makes a service identity, disabled.</summary>
    /// <param name="request">Its name and owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<ServiceIdentityDto> CreateServiceIdentityAsync(CreateServiceIdentityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/service-identities", Body(request, AccessJson.CreateServiceIdentityRequest), AccessJson.ServiceIdentityDto, cancellationToken);
    }

    /// <summary>Enables or disables a service identity.</summary>
    /// <param name="identityId">The identity.</param>
    /// <param name="enabled">Whether it may act.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="identityId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<ServiceIdentityDto> EnableServiceIdentityAsync(string identityId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityId);
        return SendAsync(HttpMethod.Post, $"api/v1/service-identities/{Uri.EscapeDataString(identityId)}/{(enabled ? "enable" : "disable")}", null, AccessJson.ServiceIdentityDto,
            cancellationToken);
    }

    /// <summary>Lists the grants this person may see.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grants.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<GrantDto>> ListGrantsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/grants", null, AccessJson.AccessListDtoGrantDto, cancellationToken);

    /// <summary>Makes a grant.</summary>
    /// <param name="request">The grant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<GrantDto> CreateGrantAsync(CreateGrantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/grants", Body(request, AccessJson.CreateGrantRequest), AccessJson.GrantDto, cancellationToken);
    }

    /// <summary>Takes a grant back.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<GrantDto> RevokeGrantAsync(string grantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        return SendAsync(HttpMethod.Delete, $"api/v1/grants/{Uri.EscapeDataString(grantId)}", null, AccessJson.GrantDto, cancellationToken);
    }

    /// <summary>Confirms, from a device of the owner of sealed content, that its keys were wrapped to the grantee's devices.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant, active.</returns>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<GrantDto> ConfirmGrantKeysAsync(string grantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        return SendAsync(HttpMethod.Post, $"api/v1/grants/{Uri.EscapeDataString(grantId)}/keys", null, AccessJson.GrantDto, cancellationToken);
    }

    /// <summary>Activates a grant of sealed session content with its keys: the sealed metadata and a copy of its key for each device of the grantee.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="request">The keys.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant, active.</returns>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<GrantDto> ConfirmGrantKeysAsync(string grantId, ConfirmGrantKeysRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/grants/{Uri.EscapeDataString(grantId)}/keys", Body(request, AccessJson.ConfirmGrantKeysRequest), AccessJson.GrantDto,
            cancellationToken);
    }

    /// <summary>Lists what grants of sealed content give this device: the sealed metadata of sessions and this device's copies of their keys.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The keys, which this device verifies and opens.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<GrantKeyDto>> ListGrantKeysAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/grant-keys", null, AccessJson.AccessListDtoGrantKeyDto, cancellationToken);

    /// <summary>Reads what an automation given to this person does, sealed by its former owner's computer, and this device's copy of its key.</summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hand-over, which this device verifies and opens.</returns>
    /// <exception cref="ArgumentException"><paramref name="transferId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server holds no hand-over of it for this device.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TransferHandoverCopyDto> GetTransferHandoverAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        return SendAsync(HttpMethod.Get, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/handover", null, AccessJson.TransferHandoverCopyDto, cancellationToken);
    }

    /// <summary>Lists the resources this person may see and their owners.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resources.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<ResourceOwnerDto>> ListOwnersAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/owners", null, AccessJson.AccessListDtoResourceOwnerDto, cancellationToken);

    /// <summary>Resumes a resource that waits for its owner.</summary>
    /// <param name="resourceKind">The kind of resource.</param>
    /// <param name="resourceId">Its id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resource and its owner.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<ResourceOwnerDto> ResumeResourceAsync(string resourceKind, string resourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceKind);
        ArgumentException.ThrowIfNullOrEmpty(resourceId);
        return SendAsync(HttpMethod.Post, $"api/v1/owners/{Uri.EscapeDataString(resourceKind)}/{Uri.EscapeDataString(resourceId)}/resume", null, AccessJson.ResourceOwnerDto,
            cancellationToken);
    }

    /// <summary>Lists the transfers this person may see.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transfers.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<TransferDto>> ListTransfersAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/transfers", null, AccessJson.AccessListDtoTransferDto, cancellationToken);

    /// <summary>Offers a machine or an automation to another owner.</summary>
    /// <param name="request">The resource and its new owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transfer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TransferDto> OfferTransferAsync(OfferTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/transfers", Body(request, AccessJson.OfferTransferRequest), AccessJson.TransferDto, cancellationToken);
    }

    /// <summary>Accepts, declines or cancels a transfer.</summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="verb"><c>accept</c>, <c>decline</c> or <c>cancel</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transfer.</returns>
    /// <exception cref="ArgumentException">An argument is empty or the verb is unknown.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TransferDto> DecideTransferAsync(string transferId, string verb, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        if (verb is not ("accept" or "decline" or "cancel")) throw new ArgumentException("The verb is accept, decline or cancel.", nameof(verb));
        return SendAsync(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/{verb}", null, AccessJson.TransferDto, cancellationToken);
    }

    /// <summary>Reads who can do what.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The lines of the review.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessReviewDto> GetAccessReviewAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/access/review", null, AccessJson.AccessReviewDto, cancellationToken);

    /// <summary>Lists the proposals of agents.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposals.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessListDto<AccessProposalDto>> ListAccessProposalsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/access/proposals", null, AccessJson.AccessListDtoAccessProposalDto, cancellationToken);

    /// <summary>Records a change of access an agent proposes.</summary>
    /// <param name="request">The proposal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessProposalDto> ProposeAccessAsync(CreateAccessProposalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/access/proposals", Body(request, AccessJson.CreateAccessProposalRequest), AccessJson.AccessProposalDto, cancellationToken);
    }

    /// <summary>Records that a person applied or dismissed a proposal.</summary>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="state"><c>applied</c> or <c>dismissed</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposal.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessProposalDto> DecideAccessProposalAsync(string proposalId, string state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        ArgumentException.ThrowIfNullOrEmpty(state);
        return SendAsync(HttpMethod.Post, $"api/v1/access/proposals/{Uri.EscapeDataString(proposalId)}/decide",
            Body(new DecideAccessProposalRequest(state), AccessJson.DecideAccessProposalRequest), AccessJson.AccessProposalDto, cancellationToken);
    }

    /// <summary>Reads the versions of the signed access document after a number.</summary>
    /// <param name="after">The number after which to list; 0 for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The versions, which the caller verifies.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessDocumentListDto> ListAccessDocumentsAsync(long after = 0, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/access/documents?after=" + Math.Max(0, after).ToString(System.Globalization.CultureInfo.InvariantCulture), null,
            FleetAccessDocumentJsonContext.Default.AccessDocumentListDto, cancellationToken);

    /// <summary>Reads the draft of the next version of the access document, for a signer.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The draft, unsigned.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessDocumentDraftDto> GetAccessDraftAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/access/draft", null, FleetAccessDocumentJsonContext.Default.AccessDocumentDraftDto, cancellationToken);

    /// <summary>Appends a version of the access document this device signed.</summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version as stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AccessDocumentDto> AppendAccessDocumentAsync(FleetSignedAccessDocument signed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signed);
        return SendAsync(HttpMethod.Post, "api/v1/access/documents", Body(new AppendAccessDocumentRequest(signed), FleetAccessDocumentJsonContext.Default.AppendAccessDocumentRequest),
            FleetAccessDocumentJsonContext.Default.AccessDocumentDto, cancellationToken);
    }
}
