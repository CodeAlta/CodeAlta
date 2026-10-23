using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What the phone reads and does beyond the list: the projects, the choices of the composer, a new session and the
/// prompts of a session. Everything goes through <c>alta</c>, the gateway the desktop's own agents use: the projects
/// from <c>project list</c>, the models and their efforts from <c>model list --detailed</c>, the default selection from
/// <c>model resolve</c> (the precedence <c>session create</c> applies), the agent prompts from <c>prompt list</c>.
/// </summary>
internal sealed class MobileApi(IPluginAltaService alta)
{
    private async Task<(int Code, string? Error, List<JsonObject> Records)> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var result = await alta.InvokeAsync(args, stdin, cancellationToken: ct).ConfigureAwait(false);
        var records = new List<JsonObject>();
        string? error = null;
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject record) continue;
                if ((string?)record["type"] == "alta.error") error ??= (string?)record["message"] ?? (string?)record["code"];
                else if ((string?)record["type"] != "alta.result") records.Add(record);
            }
            catch (JsonException) { }
        }

        return (result.ExitCode, result.ExitCode == 0 ? null : error ?? result.Error ?? "The command failed.", records);
    }

    /// <summary>The projects, and the one of the latest session, which the new-session screen starts on.</summary>
    internal async Task<JsonObject> ProjectsAsync(CancellationToken ct)
    {
        var (_, error, records) = await RunAsync(["project", "list"], null, ct).ConfigureAwait(false);
        var projects = new JsonArray();
        foreach (var record in records)
            foreach (var entry in record["projects"] as JsonArray ?? [])
                if (entry is JsonArray { Count: >= 2 } pair && (string?)pair[0] is { } id)
                    projects.Add(new JsonObject { ["id"] = id, ["path"] = (string?)pair[1], ["name"] = Path.GetFileName(((string?)pair[1] ?? id).TrimEnd('\\', '/')) });

        // The latest session says the latest project: its folder is one of the projects.
        string? latest = null;
        var (_, _, sessions) = await RunAsync(["session", "list", "--limit", "20"], null, ct).ConfigureAwait(false);
        var newest = sessions.Where(static s => s["sessionId"] is not null)
            .OrderByDescending(static s => (string?)s["lastActiveAt"] ?? (string?)s["updatedAt"] ?? "", StringComparer.Ordinal).FirstOrDefault();
        if (newest is not null && (string?)newest["workingDirectory"] is { } folder)
            latest = projects.OfType<JsonObject>().FirstOrDefault(p => folder.StartsWith(((string?)p["path"] ?? "\0").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<string>();
        return new JsonObject { ["projects"] = projects, ["default"] = latest ?? projects.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>(), ["error"] = error };
    }

    /// <summary>The choices of the composer: models with their efforts, agent prompts, and the default selection.</summary>
    internal async Task<JsonObject> OptionsAsync(CancellationToken ct)
    {
        var models = RunAsync(["model", "list", "--detailed"], null, ct);
        var resolved = RunAsync(["model", "resolve"], null, ct);
        var prompts = RunAsync(["prompt", "list"], null, ct);
        // The tiers of the settings, each with the model it gives now (alta takes the first ready target of the tier).
        string[] names = ["complex", "standard", "quick"];
        var tiers = names.Select(t => RunAsync(["model", "resolve", "--tier", t], null, ct)).ToArray();
        await Task.WhenAll([models, resolved, prompts, .. tiers]).ConfigureAwait(false);
        var tierList = new JsonArray();
        for (var i = 0; i < names.Length; i++)
        {
            var s = (await tiers[i]).Records.FirstOrDefault(static r => (string?)r["type"] == "alta.model.selection");
            tierList.Add(new JsonObject
            {
                ["tier"] = names[i], ["provider"] = (string?)s?["providerKey"], ["model"] = (string?)s?["modelId"],
                ["reasoning"] = (string?)s?["reasoningEffort"], ["error"] = s is null ? (await tiers[i]).Error : null,
            });
        }
        var list = new JsonArray();
        foreach (var m in (await models).Records.Where(static r => (string?)r["type"] == "alta.model.item"))
            list.Add(new JsonObject
            {
                ["provider"] = (string?)m["providerKey"], ["id"] = (string?)m["modelId"], ["name"] = (string?)m["displayName"] ?? (string?)m["modelId"],
                ["providerName"] = (string?)m["providerName"] ?? (string?)m["providerDisplayName"],
                ["efforts"] = m["supportedReasoningEfforts"]?.DeepClone() ?? new JsonArray(), ["defaultEffort"] = (string?)m["defaultReasoningEffort"],
            });
        var agents = new JsonArray();
        foreach (var p in (await prompts).Records.Where(static r => (string?)r["promptKind"] == "agent"))
            agents.Add(new JsonObject { ["id"] = (string?)p["promptId"], ["name"] = (string?)p["name"], ["description"] = (string?)p["description"] });
        var selection = (await resolved).Records.FirstOrDefault(static r => (string?)r["type"] == "alta.model.selection");
        return new JsonObject
        {
            ["models"] = list, ["prompts"] = agents, ["tiers"] = tierList,
            ["defaultTier"] = (string?)selection?["tier"],
            ["defaults"] = new JsonObject
            {
                ["provider"] = (string?)selection?["providerKey"], ["model"] = (string?)selection?["modelId"],
                ["reasoning"] = (string?)selection?["reasoningEffort"], ["prompt"] = (string?)selection?["agentPromptId"] ?? (string?)selection?["promptId"],
            },
            ["error"] = (await models).Error ?? (await prompts).Error,
        };
    }

    private static string? Str(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    // An option value goes to alta as one argument; a value that looks like an option is refused.
    private static bool Safe(string? value) => value is null || (!value.StartsWith('-') && value.Length < 200);

    /// <summary>Creates a session in a project with the chosen model, effort and agent, then sends its first prompt.</summary>
    internal async Task<JsonObject> CreateAsync(JsonObject body, CancellationToken ct)
    {
        var project = Str(body, "project"); var provider = Str(body, "provider"); var model = Str(body, "model");
        var reasoning = Str(body, "reasoning"); var prompt = Str(body, "prompt"); var text = Str(body, "text");
        if (body["invalid"] is not null || project is null || text is null || !Safe(project) || !Safe(provider) || !Safe(model) || !Safe(reasoning) || !Safe(prompt))
            return new JsonObject { ["error"] = "A project and a prompt are needed." };
        List<string> args = ["session", "create", "--project", project];
        var tier = Str(body, "tier");
        if (tier is not null && tier is not ("complex" or "standard" or "quick")) return new JsonObject { ["error"] = "Unknown tier." };
        if (tier is not null) args.AddRange(["--tier", tier]);
        else if (provider is not null && model is not null) args.AddRange(["--provider", provider, "--model", model]);
        if (reasoning is not null) args.AddRange(["--reasoning", reasoning]);
        if (prompt is not null) args.AddRange(["--prompt-id", prompt]);
        if (body["worktree"] is JsonValue w && w.TryGetValue<bool>(out var worktree) && worktree) args.Add("--worktree");
        // Not tied to the request: a phone that gives up must not leave a session half made.
        var (_, error, records) = await RunAsync(args, null, CancellationToken.None).ConfigureAwait(false);
        var id = records.Select(static r => (string?)r["sessionId"]).FirstOrDefault(static s => s is not null);
        if (error is not null || id is null) return new JsonObject { ["error"] = error ?? "The session was not created." };
        // alta's send returns when the turn ends (a plugin's call is not detached as an agent's is): it must not hold
        // the request, which Tailscale Serve and the phone give up on ("Failed to fetch"). It goes on by itself; a
        // refusal within 3 s is reported with the id, and after that the turn runs and SSE tells the rest.
        var send = RunAsync(["session", "send", id, "--stdin"], text, CancellationToken.None);
        if (await Task.WhenAny(send, Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false) != send)
            return new JsonObject { ["id"] = id, ["pending"] = true, ["error"] = null };
        var sent = await send.ConfigureAwait(false);
        return new JsonObject { ["id"] = id, ["error"] = null, ["sendError"] = sent.Error };
    }

    /// <summary>
    /// Continues a session as the desktop composer does: <c>send</c> when it is idle; while a turn runs, <c>steer</c>
    /// ("Send now (steer)") into that turn or <c>queue</c> ("Enqueue until idle") for after it.
    /// </summary>
    internal async Task<JsonObject> PromptAsync(string id, JsonObject body, CancellationToken ct)
    {
        var text = Str(body, "text"); var mode = Str(body, "mode") ?? "send";
        if (body["invalid"] is not null || text is null || !Safe(id) || mode is not ("send" or "steer" or "queue")) return new JsonObject { ["error"] = "A prompt is needed." };
        // As for a new session: a send returns when its turn ends, so the answer waits 3 s at most.
        var run = RunAsync(["session", mode, id, "--stdin"], text, CancellationToken.None);
        if (await Task.WhenAny(run, Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false) != run)
            return new JsonObject { ["ok"] = true, ["mode"] = mode, ["pending"] = true, ["error"] = null };
        var (_, error, _) = await run.ConfigureAwait(false);
        return new JsonObject { ["ok"] = error is null, ["mode"] = mode, ["error"] = error };
    }

    /// <summary>Stops the running turn, as the desktop's Abort (F8).</summary>
    internal async Task<JsonObject> AbortAsync(string id, CancellationToken ct)
    {
        if (!Safe(id)) return new JsonObject { ["error"] = "Unknown session." };
        // The abort is not tied to the request: it goes on if the phone leaves, and the answer does not wait for the
        // turn to wind down. A refusal within 3 s is reported; after that the stop is under way and SSE says the rest.
        var abort = RunAsync(["session", "abort", id, "--reason", "Stopped from the mobile view"], null, CancellationToken.None);
        if (await Task.WhenAny(abort, Task.Delay(3000, ct)).ConfigureAwait(false) != abort) return new JsonObject { ["ok"] = true, ["pending"] = true, ["error"] = null };
        var (_, error, _) = await abort.ConfigureAwait(false);
        return new JsonObject { ["ok"] = error is null, ["error"] = error };
    }

    /// <summary>Archive: archives the session as the desktop's Archive does (<c>alta session archive</c>); <c>alta session restore</c> brings it back.</summary>
    internal async Task<JsonObject> ArchiveAsync(string id, CancellationToken ct)
    {
        if (!Safe(id)) return new JsonObject { ["error"] = "Unknown session." };
        var (_, error, _) = await RunAsync(["session", "archive", id], null, CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["ok"] = error is null, ["error"] = error };
    }

    /// <summary>Restores an archived session (<c>alta session restore</c>), which lists it again.</summary>
    internal async Task<JsonObject> RestoreAsync(string id, CancellationToken ct)
    {
        if (!Safe(id)) return new JsonObject { ["error"] = "Unknown session." };
        var (_, error, _) = await RunAsync(["session", "restore", id], null, CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["ok"] = error is null, ["error"] = error };
    }
}
