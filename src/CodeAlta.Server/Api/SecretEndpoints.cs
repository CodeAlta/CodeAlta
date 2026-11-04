using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// The secrets, variables and environments of the REST API v1 (<c>doc/fleet/secrets.md</c>): <c>secrets</c>, their
/// <c>copies</c> and <c>uses</c>, <c>environments</c> and <c>approvals</c>. Values are write-only: set, rotate, delete and
/// list by name; they travel sealed for the devices that use them, never readable by the server or a person.
/// </summary>
internal static class SecretEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var json = FleetSecretsApiJsonContext.Default;
        api.MapGet("/secrets", async (HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretListDto))
            .Produces<FleetSecretListDto>()
            .WithSummary("The secrets and variables of the calling controller device's person: name, scope, version, who set them and when, the machines that hold them; never a value, only the copy sealed for the caller.");

        api.MapGet("/secrets/held", async (HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.ListHeldAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretListDto))
            .Produces<FleetSecretListDto>()
            .WithSummary("The secrets and variables the calling machine holds a copy of, each with that copy, which only the machine opens.");

        api.MapPut("/secrets/{id}", (string id, SetFleetSecretRequest? request, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, request?.Version > 1 ? "secret.rotate" : "secret.set", id, async actor => Results.Json(
                    await secrets.PutAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretDto)))
            .Produces<FleetSecretDto>()
            .WithSummary("Sets a secret or a variable (version 1) or rotates it (the next version), with the copies the calling controller device sealed for the machines allowed to use it and its person's controller devices.");

        api.MapPost("/secrets/{id}/copies", (string id, AddFleetSecretCopiesRequest? request, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "secret.grant", id, async actor => Results.Json(
                    await secrets.AddCopiesAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretDto)))
            .Produces<FleetSecretDto>()
            .WithSummary("Gives a machine allowed later its copy of the current version, sealed by a controller device that holds the value.");

        api.MapDelete("/secrets/{id}/copies/{device}", (string id, string device, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "secret.withdraw", id, async actor => Results.Json(
                    await secrets.RemoveCopyAsync(actor, id, device, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretDto)))
            .Produces<FleetSecretDto>()
            .WithSummary("Takes a machine's copy of a value away: it does not get the value at its next run.");

        api.MapDelete("/secrets/{id}", (string id, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "secret.delete", id, async actor => Results.Json(
                    await secrets.DeleteAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetSecretListDto)))
            .Produces<FleetSecretListDto>()
            .WithSummary("Deletes a secret or a variable and every copy of it; answers the values left.");

        api.MapGet("/secrets/{id}/uses", async (string id, HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.ListUsesAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, context.RequestAborted).ConfigureAwait(false), json.FleetSecretUseListDto))
            .Produces<FleetSecretUseListDto>()
            .WithSummary("The uses the machines published of a value: which run, which version, when; never the value.");

        api.MapPost("/secret-uses", async (PublishFleetSecretUsesRequest? request, HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.PublishUsesAsync(DeviceAuthenticationMiddleware.GetDevice(context), request, context.RequestAborted).ConfigureAwait(false),
                    json.FleetSecretUseListDto))
            .Produces<FleetSecretUseListDto>()
            .WithSummary("Records the uses the calling machine made of the values it holds.");

        api.MapGet("/environments", async (HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.ListEnvironmentsAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    json.FleetEnvironmentListDto))
            .Produces<FleetEnvironmentListDto>()
            .WithSummary("The environments of the calling device's person, with their protection rules as signed.");

        api.MapPut("/environments/{id}", (string id, PutFleetEnvironmentRequest? request, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "environment.set", id, async actor => Results.Json(
                    await secrets.PutEnvironmentAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetEnvironmentDto)))
            .Produces<FleetEnvironmentDto>()
            .WithSummary("Stores an environment, or a newer version of it, that the calling controller device signed for its person: approvers, machine labels, automations.");

        api.MapPost("/environments/{id}/approvals", (string id, RequestFleetApprovalRequest? request, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "environment.approval.request", id, async actor => Results.Json(
                    await secrets.RequestApprovalAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetApprovalDto)))
            .Produces<FleetApprovalDto>()
            .WithSummary("Asks, for a run of the calling machine, the approval the environment requires; the run starts once an approver's device signed it.");

        api.MapGet("/approvals", async (HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.ListApprovalsAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    json.FleetApprovalListDto))
            .Produces<FleetApprovalListDto>()
            .WithSummary("The approvals of the environments of the calling device's person and of those it approves, the newest first.");

        api.MapGet("/approvals/{id}", async (string id, HttpContext context, FleetSecretService secrets) =>
                Results.Json(await secrets.GetApprovalAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, context.RequestAborted).ConfigureAwait(false), json.FleetApprovalDto))
            .Produces<FleetApprovalDto>()
            .WithSummary("An approval: for its machine, and for the controller devices of the environment's person and its approvers.");

        api.MapPost("/approvals/{id}/approve", (string id, ApproveFleetApprovalRequest? request, HttpContext context, FleetSecretService secrets) =>
                AuditedAsync(context, "environment.approve", id, async actor => Results.Json(
                    await secrets.ApproveAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetApprovalDto)))
            .Produces<FleetApprovalDto>()
            .WithSummary("Gives the approval of a run with the approval the calling controller device of an approver signed; the machine verifies the signature before the run starts.");
    }

    // A refused mutation is audited too, with its code, as the other mutations of the API.
    private static async Task<IResult> AuditedAsync(HttpContext context, string action, string target, Func<DeviceRecord, Task<IResult>> mutate)
    {
        var actor = DeviceAuthenticationMiddleware.GetDevice(context);
        try
        {
            return await mutate(actor).ConfigureAwait(false);
        }
        catch (CoordinatorException exception)
        {
            var store = context.RequestServices.GetRequiredService<ICoordinatorStore>();
            await store.AppendAuditAsync(ApiEndpoints.Origin(context).Audit(action, actor, target, exception.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
