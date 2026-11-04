using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// The signed policy and its kill switch on the REST API v1 (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill
/// switch): every device of the organization reads the chain and verifies it; a signer's controller device appends the next
/// version. A refused version is audited too.
/// </summary>
internal static class PolicyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var json = FleetPolicyDocumentJsonContext.Default;

        api.MapGet("/policies", async (long? after, HttpContext context, PolicyService policies) =>
                Results.Json(await policies.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), Math.Max(0, after ?? 0), context.RequestAborted).ConfigureAwait(false),
                    json.PolicyDocumentListDto))
            .Produces<PolicyDocumentListDto>()
            .WithSummary("The versions of the signed policy after a number: every device of the organization verifies them from its anchor.");

        api.MapPost("/policies", async (AppendPolicyDocumentRequest? request, HttpContext context, PolicyService policies) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                try
                {
                    var stored = await policies.AppendAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted).ConfigureAwait(false);
                    return Results.Json(stored, json.PolicyDocumentDto, statusCode: StatusCodes.Status201Created);
                }
                catch (CoordinatorException exception)
                {
                    var store = context.RequestServices.GetRequiredService<ICoordinatorStore>();
                    await store.AppendAuditAsync(ApiEndpoints.Origin(context).Audit("policy.sign", actor, null, exception.Code), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            })
            .Produces<PolicyDocumentDto>(StatusCodes.Status201Created)
            .WithSummary("Appends a version of the policy (its rules and its kill switch) a signer's controller device signed; it must follow the last one and verify.");
    }
}
