using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Api;

/// <summary>
/// Counts the requests of the endpoints the server takes from devices it does not know yet (setup, sign-in, enrollment,
/// recovery, the hooks' ingress) per address, before their body is read or their signature checked, and refuses them
/// past their limit with a delay to retry after.
/// </summary>
internal sealed class RateLimitMiddleware(RequestDelegate next, AuthenticationRateLimiter limiter, ICoordinatorStore store)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RateLimitedAttribute>() is { } limited)
            await limiter.RequireAddressAsync(limited.Action, ApiEndpoints.Origin(context), store).ConfigureAwait(false);
        await next(context).ConfigureAwait(false);
    }
}
