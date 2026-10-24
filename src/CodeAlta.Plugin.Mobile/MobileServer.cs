using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeAlta.Plugins.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What the server of the mobile view serves, and with what.</summary>
internal sealed record MobileServerOptions
{
    /// <summary>Gets the port on the loopback address.</summary>
    public required int Port { get; init; }

    /// <summary>Gets the access token of now: resetting it disconnects every phone.</summary>
    public required Func<string> Token { get; init; }

    /// <summary>Gets the host names the requests may name, beside the loopback ones: the name of this computer on the tailnet, with its port.</summary>
    public required Func<IReadOnlyCollection<string>> Hosts { get; init; }

    /// <summary>Gets the files of the application's page, where the page of the phones is.</summary>
    public required IPluginAppFileService Files { get; init; }

    /// <summary>Gets the sessions the phone sees.</summary>
    public required MobileSessions Sessions { get; init; }

    /// <summary>Gets what the phone does through alta.</summary>
    public required MobileApi Api { get; init; }

    /// <summary>Gets the live state of the sessions.</summary>
    public required MobileLive Live { get; init; }

    /// <summary>Gets the log of the plugin.</summary>
    public required XenoAtom.Logging.Logger Logger { get; init; }
}

/// <summary>
/// The in-process HTTP server of the mobile view. It listens on the loopback address only: other devices reach it through
/// Tailscale Serve, which terminates TLS and forwards to it. It serves the page of the phones from the files of the
/// application's page, and its API to the phone that signed in with the access token.
/// </summary>
internal sealed partial class MobileServer : IAsyncDisposable
{
    /// <summary>The cookie a phone signs in with.</summary>
    internal const string CookieName = "alta_mobile";

    /// <summary>What the page of the phones may do: its own scripts and requests, inline styles (the diagrams), nothing else.</summary>
    internal const string PagePolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; "
        + "connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // A diagram is Mermaid's SVG, never trusted: no script, no request, nothing but its own inline styles and images.
    private const string DiagramPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; sandbox";
    private const long MaximumBody = 1024 * 1024;
    private const long MaximumDiagram = 4 * 1024 * 1024;

    private readonly MobileServerOptions _options;
    private readonly MobileDiagrams _diagrams = new();
    private WebApplication? _app;

    internal MobileServer(MobileServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the port on the loopback address.</summary>
    internal int Port => _options.Port;

    /// <summary>Starts listening.</summary>
    /// <exception cref="IOException">The port is used by another program.</exception>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "CodeAlta.Mobile", ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, _options.Port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaximumDiagram;
        });
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        var app = builder.Build();
        app.Use(GuardAsync);
        Map(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    /// <summary>Closes the live streams: a phone that connects again signs in again, with the token of now.</summary>
    internal void Disconnect() => _options.Live.CloseListeners();

    private void Map(WebApplication app)
    {
        // The page and its files, from the build of the application: hashed names never change, the rest is read each time.
        app.MapGet("/", (HttpContext context, CancellationToken ct) => FileAsync(context, "mobile.html", PagePolicy, ct));
        app.MapGet("/sw.js", (HttpContext context, CancellationToken ct) => FileAsync(context, "mobile/sw.js", null, ct));
        app.MapGet("/assets/{**file}", (HttpContext context, string file, CancellationToken ct) => FileAsync(context, "assets/" + file, null, ct));
        app.MapGet("/mobile/{**file}", (HttpContext context, string file, CancellationToken ct) => FileAsync(context, "mobile/" + file, null, ct));

        // The phone signs in with the token of the QR code, which it read from the fragment of the address: never a query, which
        // proxies and histories keep.
        app.MapPost("/api/login", async (HttpContext context, CancellationToken ct) =>
        {
            var body = await BodyAsync(context.Request, ct).ConfigureAwait(false);
            if (body is null || !MobileToken.Matches(MobileAlta.Text(body, "token"), _options.Token())) return Results.Unauthorized();
            context.Response.Cookies.Append(CookieName, _options.Token(), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = !IsLoopback(context.Request.Host.Host), Path = "/", MaxAge = TimeSpan.FromDays(30),
            });
            return Results.NoContent();
        });

        var api = app.MapGroup("/api").AddEndpointFilter(async (invocation, next) =>
            Authorized(invocation.HttpContext) ? await next(invocation).ConfigureAwait(false) : Results.Unauthorized());
        api.MapGet("/sessions", async (int? offset, int? limit, string? project, CancellationToken ct) =>
            Json(await _options.Sessions.ListAsync(offset ?? 0, limit ?? MobileSessions.DefaultPageSize, MobileApi.IsId(project) ? project : null, ct).ConfigureAwait(false)));
        api.MapGet("/sessions/{id}", async (string id, CancellationToken ct) => Json(await _options.Sessions.GetAsync(id, ct).ConfigureAwait(false)));
        api.MapGet("/sessions/{id}/events", async (string id, long? since, CancellationToken ct) =>
            Json(await _options.Sessions.EventsAsync(id, Math.Max(0, since ?? 0), ct).ConfigureAwait(false)));
        api.MapGet("/projects", async (CancellationToken ct) => Json(await _options.Api.ProjectsAsync(ct).ConfigureAwait(false)));
        api.MapGet("/options", async (CancellationToken ct) => Json(await _options.Api.OptionsAsync(ct).ConfigureAwait(false)));
        api.MapPost("/sessions", async (HttpRequest request, CancellationToken ct) =>
            await BodyAsync(request, ct).ConfigureAwait(false) is { } body ? Json(await _options.Api.CreateAsync(body, ct).ConfigureAwait(false)) : Invalid());
        api.MapPost("/sessions/{id}/prompt", async (string id, HttpRequest request, CancellationToken ct) =>
            await BodyAsync(request, ct).ConfigureAwait(false) is { } body ? Json(await _options.Api.PromptAsync(id, body, ct).ConfigureAwait(false)) : Invalid());
        api.MapPost("/sessions/{id}/abort", async (string id, CancellationToken ct) => Json(await _options.Api.AbortAsync(id, ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/archive", async (string id, CancellationToken ct) => Json(await _options.Api.ArchiveAsync(id, ct).ConfigureAwait(false)));
        api.MapPost("/sessions/{id}/restore", async (string id, CancellationToken ct) => Json(await _options.Api.RestoreAsync(id, ct).ConfigureAwait(false)));

        // A diagram to open in a tab of its own, at a link that needs no sign-in: a new tab of an app installed on iOS opens in
        // Safari, which has neither the app's cookie nor its storage. The link is a random id, kept two hours.
        api.MapPost("/diagrams", async (HttpRequest request, CancellationToken ct) =>
        {
            var body = await BodyAsync(request, ct, MaximumDiagram).ConfigureAwait(false);
            if (MobileAlta.Text(body, "svg") is not { } svg || !svg.TrimStart().StartsWith("<svg", StringComparison.Ordinal)) return Invalid();
            var url = "/d/" + _diagrams.Add(svg, Color(body, "background"), Color(body, "text"), MobileAlta.Flag(body, "dark"));
            return Json(new JsonObject { ["url"] = url });
        });
        app.MapGet("/d/{id}", (string id, HttpContext context) =>
        {
            if (_diagrams.Get(id) is not { } page) return Results.NotFound();
            context.Response.Headers.ContentSecurityPolicy = DiagramPolicy;
            return Results.Text(page, "text/html; charset=utf-8");
        });

        // Server-sent events: the state of each session as it changes. The page opens the stream again when it closes.
        api.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var channel = _options.Live.Subscribe();
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
            catch (OperationCanceledException)
            {
                // The phone left.
            }
            finally
            {
                _options.Live.Unsubscribe(channel);
            }
        });
    }

    // Every answer: no cache unless a file says otherwise, no sniffing, no referrer, no frame. A request that names another
    // host than this computer is refused: a page of another site that resolves its name to the loopback address gets nothing.
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        if (!AllowedHost(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private bool AllowedHost(HostString host)
    {
        if (!host.HasValue) return false;
        if (IsLoopback(host.Host)) return host.Port == _options.Port;
        return _options.Hosts().Contains(host.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]";

    private bool Authorized(HttpContext context)
    {
        var token = _options.Token();
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && MobileToken.Matches(header[7..].Trim(), token)) return true;
        return MobileToken.Matches(context.Request.Cookies[CookieName], token);
    }

    private async Task<IResult> FileAsync(HttpContext context, string path, string? policy, CancellationToken cancellationToken)
    {
        if (await _options.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } file) return Results.NotFound();
        context.Response.Headers.CacheControl = file.Immutable ? "public, max-age=31536000, immutable" : "no-cache";
        if (policy is not null) context.Response.Headers.ContentSecurityPolicy = policy;
        return Results.Bytes(file.Content, file.ContentType);
    }

    // A JSON object posted by the page; a form or another type is refused, which also keeps the forms of other sites out.
    private static async Task<JsonObject?> BodyAsync(HttpRequest request, CancellationToken cancellationToken, long maximum = MaximumBody)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true || request.ContentLength > maximum) return null;
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (limited.Length + read > maximum) return null;
                limited.Write(buffer, 0, read);
            }

            return JsonNode.Parse(limited.ToArray()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What alta gave: null is a session the phone does not see, an answer that holds nothing but its error is refused, and the
    // rest is the answer, with the error of a part of it (a list that one space could not give) beside what was read.
    private static IResult Json(JsonObject? value)
        => value is null ? Error(StatusCodes.Status404NotFound, "Unknown session.")
            : Results.Text(value.ToJsonString(), "application/json", statusCode: value.Count == 1 && value["error"] is JsonValue ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);

    private static IResult Invalid() => Error(StatusCodes.Status400BadRequest, "The request is not valid.");

    private static IResult Error(int status, string message) => Results.Text(new JsonObject { ["error"] = message }.ToJsonString(), "application/json", statusCode: status);

    private static string? Color(JsonObject? body, string name) => MobileAlta.Text(body, name) is { } color && HexColor().IsMatch(color) ? color : null;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;
        _options.Live.CloseListeners();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await app.StopAsync(patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The requests that did not end in time are dropped.
        }

        await app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
