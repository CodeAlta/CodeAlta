using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What sessions ask permission for, as the phone reads and answers it: the requests of a session, as the window shows them, and
/// the user's answer, which the session receives as from the window. They go through the permission service of the host, never
/// through <c>alta</c>: the commands of <c>alta</c> are those of agents, which never allow what they ask. A session the phone
/// does not see (<see cref="MobileSessions.FindAsync"/>) is refused as unknown.
/// </summary>
/// <remarks>
/// A session started by an automation whose run waits for the person also says so with its requests, and the phone takes the
/// run over for the user (<c>alta automation takeover</c>, run for the user, never for an agent): what the run asks then waits
/// for them, and no timeout stops it.
/// </remarks>
internal sealed class MobilePermissions(IPluginPermissionService permissions, MobileSessions sessions, TimeSpan? patience = null, IPluginAltaService? alta = null)
{
    // How long a request waits for the host: the live stream says the rest.
    private readonly TimeSpan _patience = patience ?? TimeSpan.FromSeconds(3);

    /// <summary>The requests a session waits on.</summary>
    /// <returns>The requests, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var page = permissions.HasPermissions ? await permissions.ListAsync(id, cancellationToken).ConfigureAwait(false) : new PluginPermissionPage([], false);
        var requests = new JsonArray();
        foreach (var request in page.Requests) requests.Add(ToJson(request));
        var run = await RunOfAsync(id, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["requests"] = requests, ["hasMore"] = page.HasMore, ["error"] = null,
            ["automation"] = run is null ? null : new JsonObject { ["runId"] = MobileAlta.Text(run, "id"), ["name"] = MobileAlta.Text(run, "name"), ["waiting"] = IsWaiting(run) },
        };
    }

    /// <summary>Takes over the run of an automation that started a session and waits for the person.</summary>
    /// <returns>What became of it, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> TakeOverAsync(string id, CancellationToken cancellationToken)
    {
        if (!MobileApi.IsId(id) || await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        if (alta is null || await RunOfAsync(id, cancellationToken).ConfigureAwait(false) is not { } run || MobileAlta.Text(run, "id") is not { } runId || !IsWaiting(run))
            return new JsonObject { ["ok"] = false, ["error"] = "This run waits for nobody." };
        var result = await MobileAlta.RunAsync(alta, ["automation", "takeover", runId], null, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["ok"] = result.Error is null, ["error"] = result.Error };
    }

    // The run of the automation that started a session, as `alta automation current` says it; null for none.
    private async Task<JsonObject?> RunOfAsync(string id, CancellationToken cancellationToken)
    {
        if (alta is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["automation", "current", "--session", id], null, cancellationToken).ConfigureAwait(false);
        return result.Error is null ? result.OfType("alta.automation.run").FirstOrDefault() : null;
    }

    private static bool IsWaiting(JsonObject run) => MobileAlta.Text(run, "status") == "waiting";

    /// <summary>Answers a request of a session: <c>{ requestId, decision }</c>, a decision being <c>allow_once</c> or <c>deny</c>.</summary>
    /// <returns>What became of the answer, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> ResolveAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        var requestId = MobileAlta.Text(body, "requestId");
        PluginPermissionDecision? decision = MobileAlta.Text(body, "decision") switch
        {
            "allow_once" => PluginPermissionDecision.AllowOnce,
            "deny" => PluginPermissionDecision.Deny,
            _ => null,
        };
        if (!MobileApi.IsId(id) || !Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty || decision is null) return Result("invalid");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        // Not tied to the request: a phone that gives up must not leave its answer half sent.
        var answer = permissions.ResolveAsync(id, requestId!, decision.Value, CancellationToken.None).AsTask();
        if (await Task.WhenAny(answer, Task.Delay(_patience, CancellationToken.None)).ConfigureAwait(false) != answer) return Result("uncertain");
        return Result((await answer.ConfigureAwait(false)).Status);
    }

    /// <summary>What the phone says of an answer that did not reach the session.</summary>
    internal static string? Message(string status) => status switch
    {
        "resolved" => null,
        "rejected" => "This request was answered or ended meanwhile.",
        "uncertain" => "The answer was sent, but CodeAlta cannot tell whether the session got it. Check the session on the computer.",
        "invalid" => "The answer is not valid.",
        _ => "CodeAlta cannot take answers now.",
    };

    internal static JsonObject ToJson(PluginPermissionRequest request) => new()
    {
        ["id"] = request.RequestId, ["kind"] = request.Kind, ["command"] = request.Command, ["workingDirectory"] = request.WorkingDirectory,
        ["grantRoot"] = request.GrantRoot, ["reason"] = request.Reason, ["shortened"] = request.Shortened, ["outsideSandbox"] = request.OutsideSandbox,
        ["skill"] = request.SkillName is { } name ? new JsonObject { ["server"] = request.SkillServer, ["name"] = name } : null,
    };

    /// <summary>The requests of a session as the live stream says them: what tells the phone to read them again.</summary>
    internal static JsonObject Mark(PluginPermissionSummary summary) => new() { ["id"] = summary.RequestId, ["count"] = summary.Count };

    private static JsonObject Result(string status) => new() { ["ok"] = status == "resolved", ["status"] = status, ["error"] = Message(status) };
}
