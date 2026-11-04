using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// The budgets, their allowances and holds, and the notifications of the REST API v1 (<c>doc/fleet/observability.md</c>):
/// <c>budgets</c> signed by their owner's controller device, <c>machine-budgets</c> and <c>allowance</c> for the machines,
/// <c>budget-holds</c> for the runs that wait, <c>notifications</c> of people.
/// </summary>
internal static class BudgetEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var json = FleetBudgetsApiJsonContext.Default;
        api.MapGet("/budgets", async (HttpContext context, BudgetService budgets) =>
                Results.Json(await budgets.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetListDto))
            .Produces<FleetBudgetListDto>()
            .WithSummary("The budgets of the calling controller device's person, as signed, with their consumption in the current period.");

        api.MapPut("/budgets/{id}", (string id, PutFleetBudgetRequest? request, HttpContext context, BudgetService budgets) =>
                AuditedAsync(context, "budget.set", id, async actor => Results.Json(
                    await budgets.PutAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetDto)))
            .Produces<FleetBudgetDto>()
            .WithSummary("Stores a budget, or a newer version of it, that the calling controller device signed for its person: a limit of tokens or estimated cost per period for a scope, advisory or enforced.");

        api.MapGet("/machine-budgets", async (HttpContext context, BudgetService budgets) =>
                Results.Json(await budgets.ListForMachineAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    json.FleetMachineBudgetListDto))
            .Produces<FleetMachineBudgetListDto>()
            .WithSummary("The budgets of the owner of the calling machine, as signed, with their consumption and what the machine holds of each.");

        api.MapPost("/budgets/{id}/allowance", async (string id, FleetAllowanceRequest? request, HttpContext context, BudgetService budgets) =>
                Results.Json(await budgets.AllowanceAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, request, context.RequestAborted).ConfigureAwait(false),
                    json.FleetAllowanceDto))
            .Produces<FleetAllowanceDto>()
            .WithSummary("Gives the calling machine more of an enforced budget, within what no machine holds or used: never more than the limit.");

        api.MapPost("/budgets/{id}/holds", (string id, RequestFleetBudgetHoldRequest? request, HttpContext context, BudgetService budgets) =>
                AuditedAsync(context, "budget.hold", id, async actor => Results.Json(
                    await budgets.RequestHoldAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetHoldDto)))
            .Produces<FleetBudgetHoldDto>()
            .WithSummary("Records that a run of the calling machine waits for a reached budget; it starts once a controller device of the owner signed Continue anyway.");

        api.MapGet("/budget-holds", async (HttpContext context, BudgetService budgets) =>
                Results.Json(await budgets.ListHoldsAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetHoldListDto))
            .Produces<FleetBudgetHoldListDto>()
            .WithSummary("The runs that wait or waited for the budgets of the calling device's person, the newest first; a machine sees its own.");

        api.MapGet("/budget-holds/{id}", async (string id, HttpContext context, BudgetService budgets) =>
                Results.Json(await budgets.GetHoldAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, context.RequestAborted).ConfigureAwait(false), json.FleetBudgetHoldDto))
            .Produces<FleetBudgetHoldDto>()
            .WithSummary("A hold: for its machine, and for the controller devices of the owner of the budget.");

        api.MapPost("/budget-holds/{id}/continue", (string id, ContinueFleetBudgetHoldRequest? request, HttpContext context, BudgetService budgets) =>
                AuditedAsync(context, "budget.continue", id, async actor => Results.Json(
                    await budgets.ContinueAsync(actor, id, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetHoldDto)))
            .Produces<FleetBudgetHoldDto>()
            .WithSummary("Continues a run that waits with the Continue anyway the calling controller device of the owner signed; the machine verifies it before the run starts.");

        api.MapPost("/budget-holds/{id}/stop", (string id, HttpContext context, BudgetService budgets) =>
                AuditedAsync(context, "budget.stop", id, async actor => Results.Json(
                    await budgets.StopAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), json.FleetBudgetHoldDto)))
            .Produces<FleetBudgetHoldDto>()
            .WithSummary("Stops a run that waits for a reached budget: it does not start.");

        api.MapGet("/notifications", async (HttpContext context, NotificationService notifications) =>
                Results.Json(await notifications.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    json.FleetNotificationListDto))
            .Produces<FleetNotificationListDto>()
            .WithSummary("The notifications of the calling controller device's person, the newest first: budgets at 80% and 100%, lasting problems of their machines.");

        api.MapPost("/notifications/{id}/read", async (string id, HttpContext context, NotificationService notifications) =>
                Results.Json(await notifications.ReadAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, context.RequestAborted).ConfigureAwait(false),
                    json.FleetNotificationListDto))
            .Produces<FleetNotificationListDto>()
            .WithSummary("Marks a notification read on every device of the person.");
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
