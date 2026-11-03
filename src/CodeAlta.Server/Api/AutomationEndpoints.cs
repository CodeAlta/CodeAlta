using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// The automation resources of the REST API v1 (<c>doc/fleet/protocol.md</c>): <c>automations</c> and their <c>runs</c>,
/// for a controller device of the person who gives them to their machines.
/// </summary>
internal static class AutomationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/automations", async (HttpContext context, FleetAutomationService automations) =>
                Results.Json(await automations.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    FleetAutomationApiJsonContext.Default.AutomationListDto))
            .Produces<AutomationListDto>()
            .WithSummary("The automations of the calling controller device's person, per machine: the facts the server reads, where each delivery stands, and the version as signed.");

        api.MapPut("/automations/{id}", (string id, PutAutomationRequest? request, HttpContext context, FleetAutomationService automations) =>
                AuditedAsync(context, "automation.put", id, async actor => Results.Json(
                    await automations.PutAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false),
                    FleetAutomationApiJsonContext.Default.AutomationDto)))
            .Produces<AutomationDto>()
            .WithSummary("Stores a version of an automation the calling controller device signed for one of its person's machines (sealed for it), or a signed removal, and delivers it to the machine, which verifies the signature.");

        api.MapDelete("/automations/{id}", (string id, string? machine, HttpContext context, FleetAutomationService automations) =>
                AuditedAsync(context, "automation.delete", id, async actor =>
                {
                    await automations.DeleteAsync(actor, id, machine, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false);
                    return Results.NoContent();
                }))
            .WithSummary("Forgets an automation for a machine (?machine=) once the machine took away what it held, after a signed removal.");

        api.MapGet("/automations/{id}/runs", async (string id, int? limit, HttpContext context, FleetAutomationService automations) =>
                Results.Json(await automations.ListRunsAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, limit, context.RequestAborted).ConfigureAwait(false),
                    FleetAutomationApiJsonContext.Default.AutomationRunListDto))
            .Produces<AutomationRunListDto>()
            .WithSummary("The runs of an automation as its machines reported them, and the due times they claimed, the newest first; never the prompt nor the answer.");
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
