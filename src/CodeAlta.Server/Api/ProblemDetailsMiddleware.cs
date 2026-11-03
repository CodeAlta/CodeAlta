using CodeAlta.Fleet.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CodeAlta.Server.Api;

/// <summary>
/// Answers the refusals of the coordinator as problem details (RFC 9457), with the stable <c>code</c> and the
/// <c>hint</c> of the fleet's errors as extensions, so that a client reacts instead of retrying blindly.
/// </summary>
internal sealed class ProblemDetailsMiddleware(RequestDelegate next, IProblemDetailsService problems)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (CoordinatorException exception) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, exception.Status, exception.Code, exception.Hint, exception.Message).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            // A body that does not read as its contract, or that is too large.
            await WriteAsync(context, exception.StatusCode, CoordinatorErrorCodes.InvalidBody, null, "The body of the request does not read as JSON of the expected contract.")
                .ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(HttpContext context, int status, string code, string? hint, string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
        {
            // RFC 9110: a 401 names how to authenticate.
            context.Response.Headers.WWWAuthenticate = $"{FleetSignedRequest.SignatureHeader} headers=\"{FleetSignedRequest.DeviceHeader} {FleetSignedRequest.TimestampHeader} {FleetSignedRequest.NonceHeader}\"";
        }

        var details = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Instance = context.Request.Path,
        };
        details.Extensions["code"] = code;
        if (hint is not null) details.Extensions["hint"] = hint;
        details.Extensions["requestId"] = context.TraceIdentifier;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details }).ConfigureAwait(false);
    }
}
