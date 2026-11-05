using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAlta.Server.Telemetry;

/// <summary>
/// Names the span ASP.NET Core opens for each request after its route (<c>GET /api/v1/devices/{id}</c>) and gives it the
/// attributes of the HTTP conventions: method, route, path and status. The path of a relay holds its ticket: the
/// exporter's processor replaces it. Nothing of the body or the headers is recorded.
/// </summary>
internal sealed class HttpSpanEnricher(RequestDelegate next)
{
    /// <summary>The name of the activity source of ASP.NET Core.</summary>
    public const string AspNetCoreSource = "Microsoft.AspNetCore";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            if (Activity.Current is { IsAllDataRequested: true } activity && activity.Source.Name == AspNetCoreSource)
            {
                var method = context.Request.Method;
                activity.SetTag("http.request.method", method);
                activity.SetTag("url.path", context.Request.Path.Value);
                activity.SetTag("http.response.status_code", context.Response.StatusCode);
                if ((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } route)
                {
                    activity.SetTag("http.route", route);
                    activity.DisplayName = $"{method} {route}";
                }

                if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError) activity.SetStatus(ActivityStatusCode.Error);
            }
        }
    }
}
