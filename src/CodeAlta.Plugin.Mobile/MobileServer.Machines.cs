using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAlta.Plugin.Mobile;

internal sealed partial class MobileServer
{
    private void MapMachines(RouteGroupBuilder api)
    {
        api.MapGet("/machines/{machineId}/projects", async (string machineId, CancellationToken ct) =>
        {
            var (page, error) = await MachineAsync(machineId, null, ct).ConfigureAwait(false);
            return error ?? Json(await page!.Api.ProjectsAsync(ct).ConfigureAwait(false));
        });
        api.MapGet("/machines/{machineId}/options", async (string machineId, CancellationToken ct) =>
        {
            var (page, error) = await MachineAsync(machineId, null, ct).ConfigureAwait(false);
            return error ?? Json(await page!.Api.OptionsAsync(ct).ConfigureAwait(false));
        });
        api.MapPost("/machines/{machineId}/sessions", async (string machineId, HttpRequest request, CancellationToken ct) =>
        {
            var (page, error) = await MachineAsync(machineId, "start", ct, ActionId(request)).ConfigureAwait(false);
            if (error is not null) return error;
            return await PromptBodyAsync(request, async body =>
            {
                if (UnsupportedAttachments(machineId, body)) return UnsupportedFiles();
                return _options.Machines!.Qualify(await page!.Api.CreateAsync(body, ct).ConfigureAwait(false), page.Machine);
            }, ct).ConfigureAwait(false);
        });
        api.MapMethods("/machines/{machineId}/sessions/{id}", ["GET"], MachineSessionAsync);
        api.MapMethods("/machines/{machineId}/sessions/{id}/{**resource}", ["GET", "POST"], MachineSessionAsync);
    }

    private async Task<(MobileMachinePage? Page, IResult? Error)> MachineAsync(string id, string? permission, CancellationToken cancellationToken, string? requestId = null)
    {
        _options.Machines?.ReconcileScope();
        if (_options.Machines is null || await _options.Machines.OpenAsync(id, cancellationToken, requestId).ConfigureAwait(false) is not { } page)
            return (null, Error(StatusCodes.Status404NotFound, "Unknown machine."));
        if (page.Machine.State != "online")
            return (null, Error(page.Machine.State == "unauthorized" ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable,
                page.Machine.State == "unauthorized" ? "This computer is no longer allowed on that machine." : "This machine is offline. Reconnect it to open a session."));
        if (id != MobileMachines.Local && !page.Machine.Features.Contains("sessions.read"))
            return (null, Error(StatusCodes.Status409Conflict, "Update CodeAlta on this machine to open it on the phone."));
        if (!page.Machine.Permissions.Contains("view") || permission is not null && !page.Machine.Permissions.Contains(permission))
            return (null, Error(StatusCodes.Status403Forbidden, "This computer does not have permission to do that on this machine."));
        return (page, null);
    }

    private async Task<IResult> MachineSessionAsync(HttpContext context, string machineId, string id, CancellationToken cancellationToken)
    {
        var writing = HttpMethods.IsPost(context.Request.Method);
        var (page, error) = await MachineAsync(machineId, writing ? "drive" : null, cancellationToken, writing ? ActionId(context.Request) : null).ConfigureAwait(false);
        if (error is not null) return error;
        // Check the session independently of every action. It may have moved spaces since the list was fetched.
        if (await page!.Sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return Json(null);
        var resource = context.Request.RouteValues["resource"] as string ?? "";
        if (machineId != MobileMachines.Local && (resource.StartsWith("linked-images/", StringComparison.Ordinal)
            || resource is "file" or "chart-data" or "takeover")) return Results.NotFound();
        long? Long(string key) => long.TryParse(context.Request.Query[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        int? Int(string key) => int.TryParse(context.Request.Query[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        Task<JsonObject?> Body(Func<JsonObject, Task<JsonObject?>> action) => ReadBodyAsync(action, context.Request, cancellationToken);
        if (!writing)
        {
            if (resource.StartsWith("images/", StringComparison.Ordinal))
                return Image(context, await page.Sessions.Images.SavedAsync(id, resource["images/".Length..], cancellationToken).ConfigureAwait(false));
            if (resource.StartsWith("tools/", StringComparison.Ordinal))
            {
                var tool = resource["tools/".Length..];
                return tool.EndsWith("/output", StringComparison.Ordinal)
                    ? Json(await page.Api.ToolOutputAsync(id, tool[..^"/output".Length], Long("since"), Int("offset"), Int("before"), cancellationToken).ConfigureAwait(false))
                    : Json(await page.Api.ToolAsync(id, tool, Long("since"), cancellationToken).ConfigureAwait(false));
            }
            if (resource.StartsWith("jobs/", StringComparison.Ordinal))
                return Json(await page.Api.JobAsync(id, resource["jobs/".Length..], false, cancellationToken).ConfigureAwait(false));
            return Json(resource switch
            {
                "" => _options.Machines!.Qualify(await page.Sessions.GetAsync(id, cancellationToken).ConfigureAwait(false), page.Machine),
                "events" => await page.Sessions.EventsAsync(id, Math.Max(0, Long("since") ?? 0), cancellationToken).ConfigureAwait(false),
                "events/latest" => await page.Sessions.LatestEventsAsync(id, Long("before") is > 0 ? Long("before") : null, cancellationToken).ConfigureAwait(false),
                "choices" => await page.Api.ChoicesAsync(id, cancellationToken).ConfigureAwait(false),
                "jobs" => await page.Api.JobsAsync(id, cancellationToken).ConfigureAwait(false),
                "queue" => await page.Api.QueueAsync(id, cancellationToken).ConfigureAwait(false),
                "archive-children" => await page.Api.ArchiveChildrenAsync(id, cancellationToken).ConfigureAwait(false),
                "ask" => await page.Asks.GetAsync(id, cancellationToken).ConfigureAwait(false),
                "permissions" => await page.Permissions.GetAsync(id, cancellationToken).ConfigureAwait(false),
                _ => null,
            });
        }
        if (resource == "prompt")
            return await PromptBodyAsync(context.Request, body => UnsupportedAttachments(machineId, body) ? Task.FromResult<JsonObject?>(UnsupportedFiles())
                : page.Api.PromptAsync(id, body, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (resource.StartsWith("queue/", StringComparison.Ordinal) && resource.EndsWith("/remove", StringComparison.Ordinal))
            return Json(await page.Api.UnqueueAsync(id, resource["queue/".Length..^"/remove".Length], cancellationToken).ConfigureAwait(false));
        if (resource.StartsWith("steer/", StringComparison.Ordinal) && resource.EndsWith("/withdraw", StringComparison.Ordinal))
            return Json(await page.Api.UnsteerAsync(id, resource["steer/".Length..^"/withdraw".Length], cancellationToken).ConfigureAwait(false));
        if (resource.StartsWith("jobs/", StringComparison.Ordinal) && resource.EndsWith("/stop", StringComparison.Ordinal))
            return Json(await page.Api.JobAsync(id, resource["jobs/".Length..^"/stop".Length], true, cancellationToken).ConfigureAwait(false));
        return Json(resource switch
        {
            "provider" => await Body(body => page.Api.ProviderAsync(id, body, cancellationToken)).ConfigureAwait(false),
            "abort" => await page.Api.AbortAsync(id, cancellationToken).ConfigureAwait(false),
            "archive" => await page.Api.ArchiveAsync(id, context.Request.Query["withChildren"] == "true", cancellationToken).ConfigureAwait(false),
            "restore" => await page.Api.RestoreAsync(id, cancellationToken).ConfigureAwait(false),
            "ask/answer" => await Body(body => page.Asks.AnswerAsync(id, body, cancellationToken)).ConfigureAwait(false),
            "ask/cancel" => await Body(body => page.Asks.CancelAsync(id, body, cancellationToken)).ConfigureAwait(false),
            "permissions/resolve" => await Body(body => page.Permissions.ResolveAsync(id, body, cancellationToken)).ConfigureAwait(false),
            _ => null,
        });
    }

    private static async Task<JsonObject?> ReadBodyAsync(Func<JsonObject, Task<JsonObject?>> action, HttpRequest request, CancellationToken cancellationToken)
        => await BodyAsync(request, cancellationToken).ConfigureAwait(false) is { } body ? await action(body).ConfigureAwait(false)
            : new() { ["error"] = "The request is not valid." };

    private static bool UnsupportedAttachments(string machineId, MobilePromptBody body)
        => machineId != MobileMachines.Local && body.Attachments is { ValueKind: JsonValueKind.Array } files && files.GetArrayLength() > 0;

    private static JsonObject UnsupportedFiles() => new() { ["error"] = "Files and photos cannot be sent to another machine yet." };

    private static string ActionId(HttpRequest request)
        => Guid.TryParse(request.Headers["X-Alta-Request-Id"], out var id) && id != Guid.Empty ? id.ToString("N") : Guid.NewGuid().ToString("N");
}
