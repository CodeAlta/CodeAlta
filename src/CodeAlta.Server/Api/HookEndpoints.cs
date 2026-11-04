using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// The hooks of the REST API v1 (<c>doc/fleet/protocol.md</c>): <c>hooks</c>, managed by a controller device of the person
/// whose machine takes their events, and the ingress <c>POST /api/v1/hooks/{id}</c>, which a tool calls without a device:
/// its sender's signature is checked by the machine.
/// </summary>
internal static class HookEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/hooks", async (HttpContext context, HookService hooks) =>
                Results.Json(await hooks.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    FleetHookApiJsonContext.Default.HookListDto))
            .Produces<HookListDto>()
            .WithSummary("The hooks of the calling controller device's person: their addresses, machines and the events that wait.");

        api.MapPost("/hooks", (CreateHookRequest? request, HttpContext context, HookService hooks) =>
                AuditedAsync(context, "hook.create", null, async actor => Results.Json(
                    await hooks.CreateAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false),
                    FleetHookApiJsonContext.Default.HookDto, statusCode: StatusCodes.Status201Created)))
            .Produces<HookDto>(StatusCodes.Status201Created)
            .WithSummary("Makes a hook for one of the caller's person's machines: an address a tool pushes its signed events to.");

        api.MapDelete("/hooks/{id}", (string id, HttpContext context, HookService hooks) =>
                AuditedAsync(context, "hook.delete", id, async actor => Results.Json(
                    await hooks.DeleteAsync(actor, id, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false), FleetHookApiJsonContext.Default.HookListDto)))
            .Produces<HookListDto>()
            .WithSummary("Deletes a hook and its events: its address answers nothing any more. Answers the hooks left.");

        api.MapGet("/hooks/{id}/events", async (string id, int? limit, HttpContext context, HookService hooks) =>
                Results.Json(await hooks.ListEventsAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, limit, context.RequestAborted).ConfigureAwait(false),
                    FleetHookApiJsonContext.Default.HookEventListDto))
            .Produces<HookEventListDto>()
            .WithSummary("The latest events of a hook, the newest first: when each came and what its machine did with it, never their content.");

        api.MapPost("/hooks/{id}", async (string id, HttpContext context, HookService hooks) =>
            {
                // The body as it came, byte for byte: the machine checks the signature of exactly these bytes.
                var body = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
                var receipt = await hooks.ReceiveAsync(id, context.Request.Headers, body, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(receipt, FleetHookApiJsonContext.Default.HookReceiptDto, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(new AllowUnknownDeviceAttribute(), new Identity.RateLimitedAttribute(Identity.RateLimitedActions.HookIngress))
            .Produces<HookReceiptDto>(StatusCodes.Status202Accepted)
            .WithSummary("The ingress of a hook: a tool pushes an event; it is kept once and handed to the hook's machine, which checks the sender's signature.");
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > HookService.MaxBodyBytes) return new byte[HookService.MaxBodyBytes + 1];
        if (request.Body.CanSeek) request.Body.Position = 0;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > HookService.MaxBodyBytes) break;
        }

        return buffer.ToArray();
    }

    private static async Task<IResult> AuditedAsync(HttpContext context, string action, string? target, Func<DeviceRecord, Task<IResult>> mutate)
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
