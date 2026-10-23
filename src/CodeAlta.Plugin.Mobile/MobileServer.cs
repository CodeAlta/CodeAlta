using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The in-process HTTP server of the mobile view. It listens on the loopback address only: other devices reach it
/// through Tailscale Serve, which terminates TLS and forwards to it. Every API call needs the access token.
/// </summary>
internal sealed class MobileServer : IAsyncDisposable
{
    internal const string CookieName = "alta_mobile";

    private readonly int _port;
    private readonly string _token;
    private readonly Func<int, string?, CancellationToken, Task<JsonElement>> _sessions;
    private readonly Func<object> _status;
    private readonly MobileLive _live;

    /// <summary>Reads the timeline of one session after a sequence number.</summary>
    internal Func<string, long, CancellationToken, Task<JsonElement>>? SessionEvents { get; init; }

    /// <summary>What the phone does through alta: projects, composer choices, new sessions, prompts and aborts.</summary>
    internal MobileApi? Api { get; init; }

    // A JSON object posted by the page; a form or another type is refused, which also keeps cross-site forms out.
    private static async Task<System.Text.Json.Nodes.JsonObject> Body(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true || request.ContentLength > 256 * 1024) return new() { ["invalid"] = true };
        try { return await System.Text.Json.Nodes.JsonNode.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false) as System.Text.Json.Nodes.JsonObject ?? new() { ["invalid"] = true }; }
        catch (JsonException) { return new() { ["invalid"] = true }; }
    }

    private static IResult Json(System.Text.Json.Nodes.JsonObject value)
        => Results.Text(value.ToJsonString(), "application/json", statusCode: value["error"] is null ? 200 : 400);
    private readonly MobileDiagrams _diagrams = new();
    private static string? Color(System.Text.Json.Nodes.JsonNode? node)
        => node?.GetValue<string>() is { } c && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c : null;
    private readonly XenoAtom.Logging.Logger _logger;
    private WebApplication? _app;

    internal MobileServer(int port, string token, Func<int, string?, CancellationToken, Task<JsonElement>> sessions, Func<object> status, MobileLive live, XenoAtom.Logging.Logger logger)
    {
        _port = port;
        _token = token;
        _sessions = sessions;
        _status = status;
        _live = live;
        _logger = logger;
    }

    internal int Port => _port;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "CodeAlta.Mobile", ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, _port));
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var login = context.Request.Headers["Tailscale-User-Login"].ToString();
            if (context.Request.Path.StartsWithSegments("/api"))
                _logger.Info($"mobile {context.Request.Method} {context.Request.Path} via {(string.IsNullOrEmpty(login) ? "local (no Tailscale-User-Login)" : "tailscale user " + login)}");
            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/", (HttpContext context) =>
        {
            // The first load carries the token in the query: it becomes a cookie, and the address loses it.
            if (context.Request.Query.TryGetValue("t", out var t))
            {
                if (!MobileToken.Matches(t.ToString(), _token)) return Results.Unauthorized();
                context.Response.Cookies.Append(CookieName, _token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.Headers.ContainsKey("Tailscale-User-Login") || context.Request.IsHttps,
                    Path = "/", MaxAge = TimeSpan.FromDays(30),
                });
                // The token also goes to the page, in the fragment (never sent to a server), to be kept in localStorage:
                // an app installed from this page starts at "/" and may not share this cookie (iOS gives a home-screen
                // app its own); with the kept token it gets its own cookie again (prefs.js, app.js).
                return Results.Redirect("/#k=" + Uri.EscapeDataString(_token));
            }

            return Asset("index.html", "text/html; charset=utf-8");
        });
        app.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/prefs.js", () => Asset("prefs.js", "text/javascript; charset=utf-8"));
        app.MapGet("/mermaid.min.js", () => Asset("mermaid.min.js", "text/javascript; charset=utf-8"));
        app.MapGet("/modelorder.js", () => Asset("modelorder.js", "text/javascript; charset=utf-8"));
        app.MapGet("/install.js", () => Asset("install.js", "text/javascript; charset=utf-8"));
        // Installing as an app: the manifest (no token in it), the icons, and a service worker that caches nothing.
        app.MapGet("/manifest.webmanifest", () => Asset("manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/sw.js", () => Asset("sw.js", "text/javascript; charset=utf-8"));
        app.MapGet("/icon-192.png", () => Asset("icon-192.png", "image/png"));
        app.MapGet("/icon-512.png", () => Asset("icon-512.png", "image/png"));
        app.MapGet("/icon-maskable-512.png", () => Asset("icon-maskable-512.png", "image/png"));
        app.MapGet("/apple-touch-icon.png", () => Asset("apple-touch-icon.png", "image/png"));

        var api = app.MapGroup("/api").AddEndpointFilter(async (invocation, next) =>
            Authorized(invocation.HttpContext) ? await next(invocation).ConfigureAwait(false) : Results.Unauthorized());
        api.MapGet("/sessions", async (int? offset, string? project, CancellationToken ct) =>
            Results.Text((await _sessions(offset ?? 0, string.IsNullOrWhiteSpace(project) ? null : project, ct).ConfigureAwait(false)).GetRawText(), "application/json"));
        // Projects, composer choices, creating a session and continuing one: what MobileApi reads and does through alta.
        api.MapGet("/projects", async (CancellationToken ct) => Json(await Api!.ProjectsAsync(ct).ConfigureAwait(false)));
        api.MapGet("/options", async (CancellationToken ct) => Json(await Api!.OptionsAsync(ct).ConfigureAwait(false)));
        api.MapPost("/sessions", async (HttpRequest request, CancellationToken ct) => Json(await Api!.CreateAsync(await Body(request, ct).ConfigureAwait(false), ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/prompt", async (string id, HttpRequest request, CancellationToken ct) => Json(await Api!.PromptAsync(id, await Body(request, ct).ConfigureAwait(false), ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/archive", async (string id, CancellationToken ct) => Json(await Api!.ArchiveAsync(id, ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/restore", async (string id, CancellationToken ct) => Json(await Api!.RestoreAsync(id, ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/abort", async (string id, CancellationToken ct) => Json(await Api!.AbortAsync(id, ct).ConfigureAwait(false)));
        api.MapGet("/sessions/{id}/events", async (string id, long? since, CancellationToken ct) =>
            SessionEvents is null || id.Length > 100 ? Results.NotFound()
                : Results.Text((await SessionEvents(id, since ?? 0, ct).ConfigureAwait(false)).GetRawText(), "application/json"));
        // A diagram to open in a tab of its own, at a link that needs no token: a new tab of an app installed on iOS
        // opens in Safari, which has neither the app's cookie nor its storage. The link is a random id, kept two hours.
        api.MapPost("/diagrams", async (HttpRequest request, CancellationToken ct) =>
        {
            if (request.ContentLength > 4 * 1024 * 1024) return Results.StatusCode(413);
            System.Text.Json.Nodes.JsonObject? body;
            try { body = await System.Text.Json.Nodes.JsonNode.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false) as System.Text.Json.Nodes.JsonObject; }
            catch (System.Text.Json.JsonException) { body = null; }
            var svg = body?["svg"]?.GetValue<string>();
            if (svg is null || !svg.TrimStart().StartsWith("<svg", StringComparison.Ordinal) || svg.Length > 3 * 1024 * 1024) return Results.BadRequest();
            return Results.Json(new { url = "/d/" + _diagrams.Add(svg, Color(body?["background"]), Color(body?["text"]), body?["dark"]?.GetValue<bool>() == true) });
        });
        app.MapGet("/d/{id}", (string id, HttpContext context) =>
        {
            if (_diagrams.Get(id) is not { } page) return Results.NotFound();
            // Mermaid's SVG, never trusted: no script, no fetch, nothing but its own inline styles and images.
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; sandbox";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.Text(page, "text/html; charset=utf-8");
        });
        api.MapGet("/status", () => Results.Json(_status()));
        // Server-sent events: the state of each session as it changes. The browser's EventSource reconnects by itself.
        api.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var channel = _live.Subscribe();
            try
            {
                await context.Response.WriteAsync("retry: 3000\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                await foreach (var frame in channel.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    await context.Response.WriteAsync(frame, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _live.Unsubscribe(channel);
            }
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    private bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && MobileToken.Matches(header[7..].Trim(), _token)) return true;
        return MobileToken.Matches(context.Request.Cookies[CookieName], _token);
    }

    // The build of the assets: index.html names them with it, so a phone never runs an app.js of an older build.
    private static readonly string AssetVersion = typeof(MobileServer).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..10];

    private static IResult Asset(string name, string contentType)
    {
        if (name == "index.html")
        {
            using var page = typeof(MobileServer).Assembly.GetManifestResourceStream("mobile/" + name);
            if (page is null) return Results.NotFound();
            var html = new StreamReader(page).ReadToEnd();
            foreach (var asset in (string[])["/app.js", "/app.css", "/prefs.js", "/modelorder.js", "/install.js"]) html = html.Replace($"\"{asset}\"", $"\"{asset}?v={AssetVersion}\"");
            return Results.Text(html, contentType);
        }

        var stream = typeof(MobileServer).Assembly.GetManifestResourceStream("mobile/" + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }

    public async ValueTask DisposeAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(patience.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
