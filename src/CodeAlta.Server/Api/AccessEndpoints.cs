using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// Access control on the REST API v1 (<c>doc/fleet/access-control.md</c>): <c>access</c> (the caller, who can do what, the
/// proposals of agents, the signed access document), <c>people</c>, <c>teams</c>, <c>service-identities</c>, <c>grants</c>,
/// <c>owners</c> and <c>transfers</c>. Every mutation is audited, refused ones too.
/// </summary>
internal static class AccessEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var json = FleetAccessApiJsonContext.Default;

        api.MapGet("/access/self", (HttpContext context, AccessService access) => Read(context, json.AccessSelfDto, actor => access.SelfAsync(actor, context.RequestAborted)))
            .Produces<AccessSelfDto>()
            .WithSummary("What the calling device's person is and may do in the organization, and where the signed access document stands.");

        api.MapGet("/access/review", (HttpContext context, AccessService access) => Read(context, json.AccessReviewDto, actor => access.ReviewAsync(actor, context.RequestAborted)))
            .Produces<AccessReviewDto>()
            .WithSummary("Who can do what: roles, team roles, grants and owners, for an access review; for owners, admins and auditors.");

        api.MapGet("/access/proposals", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoAccessProposalDto, actor => access.ListProposalsAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<AccessProposalDto>>()
            .WithSummary("The changes of access agents proposed: all for those who manage access, a person's own for others.");

        api.MapPost("/access/proposals", (CreateAccessProposalRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "access.propose", null, json.AccessProposalDto, actor => access.ProposeAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<AccessProposalDto>(StatusCodes.Status201Created)
            .WithSummary("Records a change of access an agent proposes for its person; it changes nothing until a person applies it.");

        api.MapPost("/access/proposals/{id}/decide", (string id, DecideAccessProposalRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "access.proposal.decide", id, json.AccessProposalDto,
                    actor => access.DecideProposalAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<AccessProposalDto>()
            .WithSummary("Records that a person applied or dismissed a proposal.");

        api.MapGet("/people", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoPersonAccessDto, actor => access.ListPeopleAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<PersonAccessDto>>()
            .WithSummary("The people of the organization with their roles, status and teams.");

        api.MapPost("/people", (InvitePersonRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "person.invite", null, json.InvitationDto, actor => access.InviteAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<InvitationDto>(StatusCodes.Status201Created)
            .WithSummary("Invites a person with a role: their computer joins with the code given back, shown once, and the owner approves it.");

        api.MapPut("/people/{id}/role", (string id, SetRoleRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "person.role", id, json.PersonAccessDto, actor => access.SetRoleAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<PersonAccessDto>()
            .WithSummary("Changes a person's role; only an owner gives or takes the roles of owner and admin, and the last owner keeps theirs.");

        api.MapPost("/people/{id}/deprovision", (string id, DeprovisionRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "person.deprovision", id, json.DeprovisionResultDto,
                    actor => access.DeprovisionAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<DeprovisionResultDto>()
            .WithSummary("Suspends a person who leaves: their devices are refused, what they owned goes to a successor, paused, and their work stops on team machines once the access document is signed.");

        api.MapGet("/teams", (HttpContext context, AccessService access) => Read(context, json.AccessListDtoTeamDto, actor => access.ListTeamsAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<TeamDto>>()
            .WithSummary("The teams with their maintainers, members and machines.");

        api.MapPost("/teams", (CreateTeamRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "team.create", null, json.TeamDto, actor => access.CreateTeamAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<TeamDto>(StatusCodes.Status201Created)
            .WithSummary("Makes a team; its maker is its first maintainer.");

        api.MapPatch("/teams/{id}", (string id, UpdateTeamRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "team.update", id, json.TeamDto, actor => access.UpdateTeamAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<TeamDto>()
            .WithSummary("Renames a team, or changes whether its machines run the personal automations of its members.");

        api.MapDelete("/teams/{id}", (string id, HttpContext context, AccessService access) =>
                Audited(context, "team.dissolve", id, json.AccessListDtoTransferDto, actor => access.DissolveTeamAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<AccessListDto<TransferDto>>()
            .WithSummary("Dissolves a team: what it owned goes to the organization, never vanishes.");

        api.MapPut("/teams/{id}/members/{personId}", (string id, string personId, SetTeamMemberRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "team.member", id, json.TeamDto,
                    actor => access.SetTeamMemberAsync(actor, id, personId, request ?? new SetTeamMemberRequest(FleetTeamRoles.Member), ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<TeamDto>()
            .WithSummary("Sets a person's role in a team: maintainer or member.");

        api.MapDelete("/teams/{id}/members/{personId}", (string id, string personId, HttpContext context, AccessService access) =>
                Audited(context, "team.member.remove", id, json.TeamDto,
                    actor => access.SetTeamMemberAsync(actor, id, personId, null, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<TeamDto>()
            .WithSummary("Takes a person out of a team.");

        api.MapPut("/teams/{id}/oncall", (string id, PutOnCallRotationRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "team.oncall", id, json.TeamDto, actor => access.SetOnCallAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<TeamDto>()
            .WithSummary("Sets the on-call rotation of a team: its people in turn, each for a shift of hours. Who is on call answers the approvals of its automations that ask the person on call.");

        api.MapDelete("/teams/{id}/oncall", (string id, HttpContext context, AccessService access) =>
                Audited(context, "team.oncall.clear", id, json.TeamDto, actor => access.ClearOnCallAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<TeamDto>()
            .WithSummary("Takes the on-call rotation of a team away.");

        api.MapGet("/service-identities", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoServiceIdentityDto, actor => access.ListServiceIdentitiesAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<ServiceIdentityDto>>()
            .WithSummary("The service identities of teams and of the organization.");

        api.MapPost("/service-identities", (CreateServiceIdentityRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "service_identity.create", null, json.ServiceIdentityDto,
                    actor => access.CreateServiceIdentityAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted), StatusCodes.Status201Created))
            .Produces<ServiceIdentityDto>(StatusCodes.Status201Created)
            .WithSummary("Makes a service identity of a team or of the organization, disabled until a person enables it.");

        api.MapPost("/service-identities/{id}/enable", (string id, HttpContext context, AccessService access) =>
                Audited(context, "service_identity.enable", id, json.ServiceIdentityDto,
                    actor => access.EnableServiceIdentityAsync(actor, id, true, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<ServiceIdentityDto>()
            .WithSummary("Enables a service identity.");

        api.MapPost("/service-identities/{id}/disable", (string id, HttpContext context, AccessService access) =>
                Audited(context, "service_identity.disable", id, json.ServiceIdentityDto,
                    actor => access.EnableServiceIdentityAsync(actor, id, false, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<ServiceIdentityDto>()
            .WithSummary("Disables a service identity.");

        api.MapGet("/grants", (HttpContext context, AccessService access) => Read(context, json.AccessListDtoGrantDto, actor => access.ListGrantsAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<GrantDto>>()
            .WithSummary("The grants: all for those who manage or review access, those of the caller and their teams for others.");

        api.MapPost("/grants", (CreateGrantRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "grant.create", null, json.GrantDto, actor => access.CreateGrantAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<GrantDto>(StatusCodes.Status201Created)
            .WithSummary("Makes a grant (subject, capability, scope); content is granted on one resource only, and sealed content waits for a device that holds its keys.");

        api.MapDelete("/grants/{id}", (string id, HttpContext context, AccessService access) =>
                Audited(context, "grant.revoke", id, json.GrantDto, actor => access.RevokeGrantAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<GrantDto>()
            .WithSummary("Takes a grant back.");

        api.MapPost("/grants/{id}/keys", (string id, ConfirmGrantKeysRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "grant.keys", id, json.GrantDto, actor => access.ConfirmGrantKeysAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<GrantDto>()
            .WithSummary("Confirms, from a device of the owner of sealed content, that its keys were wrapped to the subject's devices: the grant then applies.");

        api.MapGet("/providers", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoScopeProviderDto, actor => access.ListProvidersAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<ScopeProviderDto>>()
            .WithSummary("The providers of the teams and of the organization: their definitions and which machines hold their keys, never the keys.");

        api.MapPut("/providers/{id}", (string id, PutScopeProviderRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "provider.put", id, json.ScopeProviderDto, actor => access.PutProviderAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<ScopeProviderDto>()
            .WithSummary("Sets a provider of a team or of the organization, its key sealed for the machines of that scope: for who answers for it.");

        api.MapDelete("/providers/{id}", (string id, HttpContext context, AccessService access) =>
                Audited(context, "provider.delete", id, json.AccessListDtoScopeProviderDto, actor => access.DeleteProviderAsync(actor, id, ApiEndpoints.Origin(context),
                    context.RequestAborted)))
            .Produces<AccessListDto<ScopeProviderDto>>()
            .WithSummary("Deletes a provider of a team or of the organization; its machines drop its key at their next read.");

        api.MapGet("/provider-keys", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoScopeProviderKeyDto, actor => access.ListProviderKeysAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<ScopeProviderKeyDto>>()
            .WithSummary("The providers whose key the calling machine holds a copy of: their definitions and its copies, which it verifies and opens.");

        api.MapGet("/grant-keys", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoGrantKeyDto, actor => access.ListGrantKeysAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<GrantKeyDto>>()
            .WithSummary("What grants of sealed content give the calling device: the sealed metadata of each session and its copy of the key, which only that device opens.");

        api.MapGet("/owners", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoResourceOwnerDto, actor => access.ListOwnersAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<ResourceOwnerDto>>()
            .WithSummary("The resources and their owners, authors, whether they wait for their owner, and whether that owner is suspended.");

        api.MapPost("/owners/{kind}/{id}/resume", (string kind, string id, HttpContext context, AccessService access) =>
                Audited(context, "owner.resume", id, json.ResourceOwnerDto, actor => access.ResumeAsync(actor, kind, id, ApiEndpoints.Origin(context), context.RequestAborted)))
            .Produces<ResourceOwnerDto>()
            .WithSummary("Resumes a resource that waits for its owner, after a transfer or its owner leaving.");

        api.MapGet("/transfers", (HttpContext context, AccessService access) =>
                Read(context, json.AccessListDtoTransferDto, actor => access.ListTransfersAsync(actor, context.RequestAborted)))
            .Produces<AccessListDto<TransferDto>>()
            .WithSummary("The transfers that concern the caller or their teams; all of them for those who manage access.");

        api.MapPost("/transfers", (OfferTransferRequest? request, HttpContext context, AccessService access) =>
                Audited(context, "transfer.offer", request?.ResourceId, json.TransferDto,
                    actor => access.OfferTransferAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted), StatusCodes.Status201Created))
            .Produces<TransferDto>(StatusCodes.Status201Created)
            .WithSummary("Offers a machine or an automation to another owner: a person, a team or the organization, who accepts it.");

        api.MapGet("/transfers/{id}/handover", (string id, HttpContext context, AccessService access) =>
                Read(context, json.TransferHandoverCopyDto, actor => access.GetHandoverAsync(actor, id, context.RequestAborted)))
            .Produces<TransferHandoverCopyDto>()
            .WithSummary("What an automation given to the caller's person does, sealed by its former owner's computer, and this device's copy of its key.");

        foreach (var (verb, state) in new[] { ("accept", FleetTransferStates.Accepted), ("decline", FleetTransferStates.Declined), ("cancel", FleetTransferStates.Cancelled) })
        {
            api.MapPost("/transfers/{id}/" + verb, (string id, HttpContext context, AccessService access) =>
                    Audited(context, "transfer." + verb, id, json.TransferDto, actor => access.DecideTransferAsync(actor, id, state, ApiEndpoints.Origin(context), context.RequestAborted)))
                .Produces<TransferDto>()
                .WithSummary(verb switch
                {
                    "accept" => "Takes what was given: for the new owner.",
                    "decline" => "Refuses what was given: for the new owner.",
                    _ => "Takes an offer back: for the one who made it.",
                });
        }

        api.MapGet("/access/documents", (long? after, HttpContext context, AccessDocumentService documents) =>
                Read(context, FleetAccessDocumentJsonContext.Default.AccessDocumentListDto,
                    actor => documents.ListAsync(actor, Math.Max(0, after ?? 0), context.RequestAborted)))
            .Produces<AccessDocumentListDto>()
            .WithSummary("The versions of the signed access document after a number: every device of the organization verifies them from its anchor.");

        api.MapGet("/access/draft", (HttpContext context, AccessDocumentService documents) =>
                Read(context, FleetAccessDocumentJsonContext.Default.AccessDocumentDraftDto, actor => documents.DraftAsync(actor, context.RequestAborted)))
            .Produces<AccessDocumentDraftDto>()
            .WithSummary("What the next version of the access document would say, from the server's model, with what changes: a signer's console checks it and signs it.");

        api.MapPost("/access/documents", (AppendAccessDocumentRequest? request, HttpContext context, AccessDocumentService documents) =>
                Audited(context, "access_document.sign", null, FleetAccessDocumentJsonContext.Default.AccessDocumentDto,
                    actor => documents.AppendAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted), StatusCodes.Status201Created))
            .Produces<AccessDocumentDto>(StatusCodes.Status201Created)
            .WithSummary("Appends a version of the access document a signer's controller device signed; it must follow the last one and say what the server's model says.");
    }

    private static async Task<IResult> Read<T>(HttpContext context, JsonTypeInfo<T> type, Func<DeviceRecord, Task<T>> read)
        => Results.Json(await read(DeviceAuthenticationMiddleware.GetDevice(context)).ConfigureAwait(false), type);

    // A refused mutation is audited too, with its code, as the other mutations of the API.
    private static async Task<IResult> Audited<T>(HttpContext context, string action, string? target, JsonTypeInfo<T> type, Func<DeviceRecord, Task<T>> mutate,
        int status = StatusCodes.Status200OK)
    {
        var actor = DeviceAuthenticationMiddleware.GetDevice(context);
        try
        {
            return Results.Json(await mutate(actor).ConfigureAwait(false), type, statusCode: status);
        }
        catch (CoordinatorException exception)
        {
            var store = context.RequestServices.GetRequiredService<ICoordinatorStore>();
            await store.AppendAuditAsync(ApiEndpoints.Origin(context).Audit(action, actor, target, exception.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
