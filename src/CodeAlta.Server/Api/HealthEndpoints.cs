using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAlta.Server.Api;

/// <summary>
/// The probes of a load balancer, a container platform or a monitor (<c>doc/fleet/observability.md</c>, health checks):
/// <c>/health/live</c> says the process answers, <c>/health/ready</c> (and <c>/health</c>) says it can serve, which is
/// that its store is reachable and migrated. They need no device, are cheap, and tell nothing of the organization, the
/// version or the machine: a status word only. <c>/api/v1/server/health</c> stays the richer answer for the CodeAlta clients.
/// </summary>
internal static class HealthEndpoints
{
    private const string Live = """{"status":"live"}""";
    private const string Ready = """{"status":"ready"}""";
    private const string Unavailable = """{"status":"unavailable"}""";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/health").WithTags("health");

        group.MapGet("/live", (HttpContext context) => Answer(context, StatusCodes.Status200OK, Live))
            .WithSummary("Liveness: the process answers. It checks nothing else, so a platform never restarts a server for a slow store.");
        group.MapGet("/ready", ReadyAsync)
            .WithSummary("Readiness: the store is reachable and its migrations are done; 503 otherwise.");
        group.MapGet("", ReadyAsync)
            .WithSummary("The same answer as /health/ready.");
    }

    private static async Task<IResult> ReadyAsync(HttpContext context, ICoordinatorStore store)
    {
        try
        {
            // A store that opened its database has run its migrations (it does not start otherwise); a version above zero
            // is read from the database, so it also proves the store answers now.
            var ready = await store.GetSchemaVersionAsync(context.RequestAborted).ConfigureAwait(false) > 0;
            return Answer(context, ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, ready ? Ready : Unavailable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Answer(context, StatusCodes.Status503ServiceUnavailable, Unavailable);
        }
    }

    private static IResult Answer(HttpContext context, int status, string body)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(body, "application/json", statusCode: status);
    }
}
