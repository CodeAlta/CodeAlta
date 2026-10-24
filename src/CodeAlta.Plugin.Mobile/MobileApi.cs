using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What the phone reads and does beyond the list: the projects, the choices of the composer, a new session and the prompts of
/// a session. Everything goes through <c>alta</c>, the gateway the desktop's own agents use: the projects from
/// <c>project list</c>, the models and their efforts from <c>model list --detailed</c>, the default selection and the tiers
/// from <c>model resolve</c> (the precedence <c>session create</c> applies), the agent prompts from <c>prompt list</c>. A
/// session the phone does not see (<see cref="MobileSessions.FindAsync"/>) is refused as unknown.
/// </summary>
internal sealed class MobileApi(IPluginAltaService alta, MobileSessions sessions)
{
    /// <summary>The longest prompt the phone sends.</summary>
    internal const int MaximumPrompt = 100_000;

    // How long a request waits for a command that goes on with a turn: Tailscale Serve and the phone give up on a long answer,
    // and the live stream says the rest.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    private static readonly string[] Tiers = ["complex", "standard", "quick"];

    /// <summary>Whether a text is the id of a session or the value of an option, which alta receives as one argument.</summary>
    internal static bool IsId(string? value) => value is { Length: > 0 and <= 200 } && !value.StartsWith('-') && !value.Any(char.IsControl);

    /// <summary>The projects of the spaces of the plugin, and the one of the latest session, which the new-session screen starts on.</summary>
    internal async Task<JsonObject> ProjectsAsync(CancellationToken cancellationToken)
    {
        var (projects, error) = await ReadProjectsAsync(cancellationToken).ConfigureAwait(false);
        var latest = await sessions.ListAsync(0, 1, null, cancellationToken).ConfigureAwait(false);
        var latestProject = (latest["sessions"] as JsonArray)?.OfType<JsonObject>().Select(static session => MobileAlta.Text(session, "projectId")).FirstOrDefault();
        var list = new JsonArray();
        foreach (var project in projects)
        {
            // The space a new session of the project goes to.
            list.Add(new JsonObject { ["id"] = project.Id, ["name"] = project.Name, ["path"] = project.Path, ["space"] = await sessions.SpaceAsync(project.Space, cancellationToken).ConfigureAwait(false) });
        }
        var known = latestProject is not null && projects.Any(project => string.Equals(project.Id, latestProject, StringComparison.OrdinalIgnoreCase));
        return new JsonObject { ["projects"] = list, ["default"] = known ? latestProject : projects.FirstOrDefault()?.Id, ["error"] = error };
    }

    /// <summary>The choices of the composer: models with their efforts, agent prompts, tiers and the default selection.</summary>
    internal async Task<JsonObject> OptionsAsync(CancellationToken cancellationToken)
    {
        var models = MobileAlta.RunAsync(alta, ["model", "list", "--detailed"], null, cancellationToken);
        var resolved = MobileAlta.RunAsync(alta, ["model", "resolve"], null, cancellationToken);
        var prompts = MobileAlta.RunAsync(alta, ["prompt", "list"], null, cancellationToken);
        // The tiers of the settings, each with the model it gives now (alta takes the first ready model of the tier).
        var tiers = Tiers.Select(tier => MobileAlta.RunAsync(alta, ["model", "resolve", "--tier", tier], null, cancellationToken)).ToArray();
        await Task.WhenAll([models, resolved, prompts, .. tiers]).ConfigureAwait(false);

        var tierList = new JsonArray();
        for (var index = 0; index < Tiers.Length; index++)
        {
            var result = await tiers[index].ConfigureAwait(false);
            var selection = result.OfType("alta.model.selection").FirstOrDefault();
            tierList.Add(new JsonObject
            {
                ["tier"] = Tiers[index], ["provider"] = MobileAlta.Text(selection, "providerKey"), ["model"] = MobileAlta.Text(selection, "modelId"),
                ["reasoning"] = MobileAlta.Text(selection, "reasoningEffort"), ["error"] = selection is null ? result.Error ?? "The tier has no ready model." : null,
            });
        }

        var modelList = new JsonArray();
        foreach (var model in (await models.ConfigureAwait(false)).OfType("alta.model.item"))
        {
            modelList.Add(new JsonObject
            {
                ["provider"] = MobileAlta.Text(model, "providerKey"), ["id"] = MobileAlta.Text(model, "modelId"),
                ["name"] = MobileAlta.Text(model, "displayName") ?? MobileAlta.Text(model, "modelId"), ["providerName"] = MobileAlta.Text(model, "provider"),
                ["efforts"] = model["supportedReasoningEfforts"]?.DeepClone() ?? new JsonArray(), ["defaultEffort"] = MobileAlta.Text(model, "defaultReasoningEffort"),
            });
        }

        var agents = new JsonArray();
        foreach (var prompt in (await prompts.ConfigureAwait(false)).OfType("alta.prompt").Where(static prompt => MobileAlta.Text(prompt, "promptKind") == "agent"))
            agents.Add(new JsonObject { ["id"] = MobileAlta.Text(prompt, "promptId"), ["name"] = MobileAlta.Text(prompt, "name"), ["description"] = MobileAlta.Text(prompt, "description") });

        var defaults = (await resolved.ConfigureAwait(false)).OfType("alta.model.selection").FirstOrDefault();
        return new JsonObject
        {
            ["models"] = modelList, ["prompts"] = agents, ["tiers"] = tierList,
            // A session that names no model starts on the standard tier, unless the settings say another default.
            ["defaultTier"] = MobileAlta.Text(defaults, "tier") ?? "standard",
            ["defaults"] = new JsonObject
            {
                ["provider"] = MobileAlta.Text(defaults, "providerKey"), ["model"] = MobileAlta.Text(defaults, "modelId"),
                ["reasoning"] = MobileAlta.Text(defaults, "reasoningEffort"), ["prompt"] = MobileAlta.Text(defaults, "agentPromptId") ?? MobileAlta.Text(defaults, "promptId"),
            },
            ["error"] = (await models.ConfigureAwait(false)).Error ?? (await prompts.ConfigureAwait(false)).Error,
        };
    }

    /// <summary>Creates a session in a project with the chosen model, effort and agent, then sends its first prompt.</summary>
    internal async Task<JsonObject> CreateAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var project = Option(body, "project");
        var text = Prompt(body);
        if (project is null || text is null) return Error("A project and a prompt are needed.");
        var tier = Option(body, "tier");
        if (tier is not null && !Tiers.Contains(tier, StringComparer.Ordinal)) return Error("Unknown tier.");
        var (projects, _) = await ReadProjectsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.FirstOrDefault(known => string.Equals(known.Id, project, StringComparison.OrdinalIgnoreCase)) is not { } chosen) return Error("Unknown project.");

        // The session goes to a space of the plugin that has the project: without one alta would put it in the space the window
        // shows, which the phone may not see.
        List<string> args = ["session", "create", "--project", chosen.Id, "--space", chosen.Space];
        if (tier is not null) args.AddRange(["--tier", tier]);
        else if (Option(body, "provider") is { } provider && Option(body, "model") is { } model) args.AddRange(["--provider", provider, "--model", model]);
        if (Option(body, "reasoning") is { } reasoning) args.AddRange(["--reasoning", reasoning]);
        if (Option(body, "prompt") is { } prompt) args.AddRange(["--prompt-id", prompt]);
        if (MobileAlta.Flag(body, "worktree")) args.Add("--worktree");
        // Not tied to the request: a phone that gives up must not leave a session half made.
        var created = await MobileAlta.RunAsync(alta, args, null, CancellationToken.None).ConfigureAwait(false);
        var id = created.OfType("alta.session.created").Select(static record => MobileAlta.Text(record, "sessionId")).FirstOrDefault(static value => value is not null);
        if (created.Error is not null || id is null) return Error(created.Error ?? "The session was not created.");

        // A send from a plugin returns when its turn ends: the request waits a moment for a refusal, and the turn goes on.
        var send = MobileAlta.RunAsync(alta, ["session", "send", id, "--stdin"], text, CancellationToken.None);
        if (await Task.WhenAny(send, Task.Delay(Patience, CancellationToken.None)).ConfigureAwait(false) != send)
            return new JsonObject { ["id"] = id, ["pending"] = true, ["error"] = null };
        var sent = await send.ConfigureAwait(false);
        return new JsonObject { ["id"] = id, ["sendError"] = sent.Error, ["error"] = null };
    }

    /// <summary>
    /// Continues a session as the desktop composer does: <c>send</c> when it is idle; while a turn runs, <c>steer</c> into that
    /// turn or <c>queue</c> it for after.
    /// </summary>
    internal async Task<JsonObject?> PromptAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        var text = Prompt(body);
        var mode = Option(body, "mode") ?? "send";
        if (text is null || mode is not ("send" or "steer" or "queue")) return Error("A prompt is needed.");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var run = MobileAlta.RunAsync(alta, ["session", mode, id, "--stdin"], text, CancellationToken.None);
        if (await Task.WhenAny(run, Task.Delay(Patience, CancellationToken.None)).ConfigureAwait(false) != run)
            return new JsonObject { ["ok"] = true, ["mode"] = mode, ["pending"] = true, ["error"] = null };
        var result = await run.ConfigureAwait(false);
        return new JsonObject { ["ok"] = result.Error is null, ["mode"] = mode, ["error"] = result.Error };
    }

    /// <summary>Stops the running turn, as the desktop's Abort.</summary>
    internal async Task<JsonObject?> AbortAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        // The stop goes on if the phone leaves, and the answer does not wait for the turn to wind down.
        var abort = MobileAlta.RunAsync(alta, ["session", "abort", id, "--reason", "Stopped from the phone"], null, CancellationToken.None);
        if (await Task.WhenAny(abort, Task.Delay(Patience, CancellationToken.None)).ConfigureAwait(false) != abort)
            return new JsonObject { ["ok"] = true, ["pending"] = true, ["error"] = null };
        var result = await abort.ConfigureAwait(false);
        return new JsonObject { ["ok"] = result.Error is null, ["error"] = result.Error };
    }

    /// <summary>Archives a session as the desktop's Archive does.</summary>
    internal Task<JsonObject?> ArchiveAsync(string id, CancellationToken cancellationToken) => SessionCommandAsync("archive", id, cancellationToken);

    /// <summary>Restores an archived session, which the list shows again.</summary>
    internal Task<JsonObject?> RestoreAsync(string id, CancellationToken cancellationToken) => SessionCommandAsync("restore", id, cancellationToken);

    private async Task<JsonObject?> SessionCommandAsync(string command, string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["session", command, id], null, CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["ok"] = result.Error is null, ["error"] = result.Error };
    }

    // The projects of the spaces of the plugin, each with the space a new session of it goes to.
    private async Task<(IReadOnlyList<MobileProject> Projects, string? Error)> ReadProjectsAsync(CancellationToken cancellationToken)
    {
        var assigned = sessions.Spaces;
        var projects = new Dictionary<string, MobileProject>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        foreach (var space in assigned.Count == 0 ? [null] : assigned.Cast<string?>())
        {
            var result = await MobileAlta.RunAsync(alta, space is null ? ["project", "list", "--detailed", "--all"] : ["project", "list", "--detailed", "--space", space], null, cancellationToken).ConfigureAwait(false);
            error ??= result.Error;
            foreach (var item in result.OfType("alta.project.item"))
            {
                if (MobileAlta.Text(item, "projectId") is not { } id || MobileAlta.Flag(item, "archived") || projects.ContainsKey(id)) continue;
                // A project in every space has no list of spaces: its sessions go to the default space.
                var spacesOfProject = (item["spaces"] as JsonArray)?.Select(static value => (string?)value).OfType<string>().ToArray() ?? [];
                var target = space ?? spacesOfProject.FirstOrDefault() ?? "default";
                projects[id] = new MobileProject(id, MobileAlta.Text(item, "displayName") ?? MobileAlta.Text(item, "name") ?? id, MobileAlta.Text(item, "projectPath"), target);
            }
        }

        return ([.. projects.Values.OrderBy(static project => project.Name, StringComparer.OrdinalIgnoreCase)], error);
    }

    private static string? Option(JsonObject body, string name) => MobileAlta.Text(body, name)?.Trim() is { } value && IsId(value) ? value : null;

    private static string? Prompt(JsonObject body) => MobileAlta.Text(body, "text") is { Length: <= MaximumPrompt } text && text.Trim().Length > 0 ? text : null;

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private sealed record MobileProject(string Id, string Name, string? Path, string Space);
}
