using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What sessions ask permission for, as the phone reads and answers it: the requests of a session, as the window shows them, and
/// the user's answer, which the session receives as from the window. They go through the permission service of the host, never
/// through <c>alta</c>: the commands of <c>alta</c> are those of agents, which never allow what they ask. A session the phone
/// does not see (<see cref="MobileSessions.FindAsync"/>) is refused as unknown.
/// </summary>
internal sealed class MobilePermissions(IPluginPermissionService permissions, MobileSessions sessions, TimeSpan? patience = null)
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
        return new JsonObject { ["requests"] = requests, ["hasMore"] = page.HasMore, ["error"] = null };
    }

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
