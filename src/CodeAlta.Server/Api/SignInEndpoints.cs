using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAlta.Server.Api;

/// <summary>
/// Sign-in (<c>doc/fleet/identity.md</c>): the methods a person may use, the start and the completion of a sign-in
/// through the system browser of a device, the server's own passkey page, and the settings an owner changes.
/// </summary>
internal static class SignInEndpoints
{
    public static void Map(RouteGroupBuilder api, WebApplication app)
    {
        var signIn = api.MapGroup("/sign-in");
        signIn.MapGet("/methods", async (SignInService service, CancellationToken token)
                => Results.Json(await service.GetMethodsAsync(token).ConfigureAwait(false), FleetSignInJsonContext.Default.SignInMethodsResponse))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<SignInMethodsResponse>()
            .WithSummary("The ways a person may sign in to this server now; needs no device.");

        signIn.MapPost("/start", async (StartSignInRequest? request, HttpContext context, SignInService service) =>
                Results.Json(await service.StartAsync(request, DeviceAuthenticationMiddleware.GetSigned(context), ApiEndpoints.Origin(context), context, context.RequestAborted)
                    .ConfigureAwait(false), FleetSignInJsonContext.Default.StartSignInResponse))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<StartSignInResponse>()
            .WithSummary("Starts a sign-in through the system browser of a device, signed by its key: the address to open, which comes back to its loopback address.");

        signIn.MapPost("/complete", async (CompleteSignInRequest? request, HttpContext context, SignInService service) =>
                Results.Json(await service.CompleteAsync(request, DeviceAuthenticationMiddleware.GetSigned(context), ApiEndpoints.Origin(context), context.RequestAborted)
                    .ConfigureAwait(false), FleetSignInJsonContext.Default.CompleteSignInResponse))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<CompleteSignInResponse>()
            .WithSummary("Completes a sign-in with the code the browser brought back, signed by the key that started it, once: a grant to enroll that device, or a link.");

        signIn.MapPost("/passkey/options", async (PasskeyOptionsRequest? request, HttpContext context, SignInService service) =>
                Results.Json(await service.PasskeyOptionsAsync(request, context).ConfigureAwait(false), FleetSignInJsonContext.Default.PasskeyOptionsResponse))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<PasskeyOptionsResponse>()
            .WithSummary("The options of the passkey ceremony of the server's sign-in page.");

        signIn.MapPost("/passkey/finish", async (PasskeyFinishRequest? request, HttpContext context, SignInService service) =>
                Results.Json(await service.PasskeyFinishAsync(request, ApiEndpoints.Origin(context), context).ConfigureAwait(false),
                    FleetSignInJsonContext.Default.PasskeyFinishResponse))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<PasskeyFinishResponse>()
            .WithSummary("Ends the passkey ceremony of the server's sign-in page: where the browser goes back to the device.");

        signIn.MapGet("/settings", async (HttpContext context, SignInService service)
                => Results.Json(await service.GetSettingsAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    FleetSignInJsonContext.Default.SignInSettingsDto))
            .Produces<SignInSettingsDto>()
            .WithSummary("The sign-in settings of the organization, never the client secret; for an owner's controller device.");

        signIn.MapPut("/settings", (SetSignInSettingsRequest? request, HttpContext context, SignInService service) =>
                AuditedAsync(context, "sign_in.settings.set", actor => service.SetSettingsAsync(actor, request, ApiEndpoints.Origin(context), context.RequestAborted),
                    FleetSignInJsonContext.Default.SignInSettingsDto))
            .Produces<SignInSettingsDto>()
            .WithSummary("Changes the sign-in methods, the company's OpenID Connect provider and whether people must use it; for an owner's controller device.");

        signIn.MapGet("/logins", async (HttpContext context, SignInService service)
                => Results.Json(await service.ListLoginsAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted).ConfigureAwait(false),
                    FleetSignInJsonContext.Default.ListResponseSignInLoginDto))
            .Produces<ListResponse<SignInLoginDto>>()
            .WithSummary("The sign-in methods and passkeys linked to the calling device's person.");

        // The server's own sign-in page, for passkeys: WebAuthn runs in a browser, at the origin of the server.
        app.MapGet("/sign-in/passkey", (HttpContext context) => Page(context, PasskeyPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/sign-in/passkey.js", (HttpContext context) => Page(context, PasskeyPage.Script, "text/javascript; charset=utf-8")).ExcludeFromDescription();
    }

    private static IResult Page(HttpContext context, string content, string type)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers.CacheControl = "no-store";
        return Results.Text(content, type);
    }

    private static async Task<IResult> AuditedAsync<T>(HttpContext context, string action, Func<DeviceRecord, Task<T>> mutate, JsonTypeInfo<T> type)
    {
        var actor = DeviceAuthenticationMiddleware.GetDevice(context);
        try
        {
            return Results.Json(await mutate(actor).ConfigureAwait(false), type);
        }
        catch (CoordinatorException exception)
        {
            var store = context.RequestServices.GetRequiredService<ICoordinatorStore>();
            await store.AppendAuditAsync(ApiEndpoints.Origin(context).Audit(action, actor, actor.OrganizationId, exception.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>The passkey page of the server: one button, the ceremony, and the way back to CodeAlta.</summary>
internal static class PasskeyPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sign in with a passkey - CodeAlta Server</title>
        <style>
        body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f4f5f7; color: #1c2127; }
        main { max-width: 26rem; padding: 2rem; background: #fff; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
        h1 { font-size: 1.25rem; margin: 0 0 .75rem; }
        p { line-height: 1.45; }
        button { font: inherit; padding: .55rem 1.1rem; border: 0; border-radius: 4px; background: #2d72d2; color: #fff; cursor: pointer; }
        button:disabled { opacity: .6; cursor: default; }
        @media (prefers-color-scheme: dark) { body { background: #1c2127; color: #f6f7f9; } main { background: #252a31; } }
        </style>
        </head>
        <body>
        <main>
        <h1>CodeAlta Server</h1>
        <p id="status">Use a passkey of this server to sign in on your computer.</p>
        <button id="go" type="button">Continue with a passkey</button>
        </main>
        <script src="passkey.js"></script>
        </body>
        </html>
        """;

    public const string Script = """
        "use strict";
        (() => {
          const status = document.getElementById("status");
          const button = document.getElementById("go");
          const attempt = decodeURIComponent(location.hash.slice(1));
          const show = text => { status.textContent = text; };
          if (!attempt) { show("Start the sign-in from CodeAlta."); button.hidden = true; return; }
          if (!window.PublicKeyCredential || !PublicKeyCredential.parseRequestOptionsFromJSON) {
            show("This browser does not support passkeys: open the address in a recent browser."); button.hidden = true; return;
          }
          const post = async (path, body) => {
            const response = await fetch(path, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
            const json = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(json.detail || json.title || String(response.status));
            return json;
          };
          button.addEventListener("click", async () => {
            button.disabled = true;
            try {
              const options = await post("../api/v1/sign-in/passkey/options", { attemptId: attempt });
              const json = JSON.parse(options.optionsJson);
              const credential = options.ceremony === "create"
                ? await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(json) })
                : await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(json) });
              const finish = await post("../api/v1/sign-in/passkey/finish", { attemptId: attempt, credentialJson: JSON.stringify(credential) });
              show("Done. You can close this window and go back to CodeAlta.");
              button.hidden = true;
              location.replace(finish.redirectUrl);
            } catch (error) {
              show("The passkey sign-in did not complete: " + error.message);
              button.disabled = false;
            }
          });
        })();
        """;
}
