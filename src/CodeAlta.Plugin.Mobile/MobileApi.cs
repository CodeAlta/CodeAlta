using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// What the phone reads and does beyond the list: the projects, the choices of the composer, a new session and the prompts of
/// a session. Everything goes through <c>alta</c>, the gateway the desktop's own agents use: the projects from
/// <c>project list</c>, the models and their efforts from <c>model list --detailed</c>, the default selection and the tiers
/// from <c>model resolve</c> (the precedence <c>session create</c> applies), the agent prompts from <c>prompt list</c>, the
/// permission modes of the providers from <c>provider list --detailed</c>, and what the composer of a session offers from
/// <c>session choices</c>. A session the phone does not see (<see cref="MobileSessions.FindAsync"/>) is refused as unknown.
/// </summary>
internal sealed class MobileApi(IPluginAltaService alta, MobileSessions sessions, string? attachmentsDirectory = null)
{
    /// <summary>The longest prompt the phone sends.</summary>
    internal const int MaximumPrompt = 100_000;

    /// <summary>Most files of one prompt, as a send from the desktop takes.</summary>
    private const int MaximumAttachments = 64;

    /// <summary>Largest file, and largest total, in bytes.</summary>
    private const long MaximumFileBytes = 20L * 1024 * 1024;

    private const long MaximumTotalBytes = 64L * 1024 * 1024;

    // How long a request waits for a command that goes on with a turn: Tailscale Serve and the phone give up on a long answer,
    // and the live stream says the rest.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    private static readonly string[] Tiers = ["complex", "standard", "quick"];

    // The plugin's data folder. Tests that omit it use a directory of their own under the temp folder.
    private readonly string _attachmentsDirectory = attachmentsDirectory ?? Path.Combine(Path.GetTempPath(), "alta-mobile-attach");

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

    /// <summary>
    /// The choices of the new-session screen: models with their efforts, agent prompts, tiers, the default selection, and the
    /// permission modes of each provider that offers some.
    /// </summary>
    internal async Task<JsonObject> OptionsAsync(CancellationToken cancellationToken)
    {
        var models = MobileAlta.RunAsync(alta, ["model", "list", "--detailed"], null, cancellationToken);
        var resolved = MobileAlta.RunAsync(alta, ["model", "resolve"], null, cancellationToken);
        var prompts = MobileAlta.RunAsync(alta, ["prompt", "list"], null, cancellationToken);
        var providers = MobileAlta.RunAsync(alta, ["provider", "list", "--detailed"], null, cancellationToken);
        // The tiers of the settings, each with the model it gives now (alta takes the first ready model of the tier).
        var tiers = Tiers.Select(tier => MobileAlta.RunAsync(alta, ["model", "resolve", "--tier", tier], null, cancellationToken)).ToArray();
        await Task.WhenAll([models, resolved, prompts, providers, .. tiers]).ConfigureAwait(false);

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

        var providerItems = (await providers.ConfigureAwait(false)).OfType("alta.provider.item").ToArray();
        var modelList = new JsonArray();
        foreach (var model in (await models.ConfigureAwait(false)).OfType("alta.model.item"))
        {
            var providerKey = MobileAlta.Text(model, "providerKey");
            var entry = new JsonObject
            {
                ["provider"] = providerKey, ["id"] = MobileAlta.Text(model, "modelId"),
                ["name"] = MobileAlta.Text(model, "displayName") ?? MobileAlta.Text(model, "modelId"),
                ["providerName"] = providerKey is null ? null : await sessions.ProviderNameAsync(providerKey, cancellationToken).ConfigureAwait(false),
                ["efforts"] = model["supportedReasoningEfforts"]?.DeepClone() ?? new JsonArray(), ["defaultEffort"] = MobileAlta.Text(model, "defaultReasoningEffort"),
            };
            if (model["imageInput"] is JsonValue readsImages && readsImages.TryGetValue<bool>(out var imageInput)) entry["imageInput"] = imageInput;
            modelList.Add(entry);
        }

        var agents = new JsonArray();
        foreach (var prompt in (await prompts.ConfigureAwait(false)).OfType("alta.prompt").Where(static prompt => MobileAlta.Text(prompt, "promptKind") == "agent"))
            agents.Add(new JsonObject { ["id"] = MobileAlta.Text(prompt, "promptId"), ["name"] = MobileAlta.Text(prompt, "name"), ["description"] = MobileAlta.Text(prompt, "description") });

        // The modes a new session of a provider can start in; a provider that offers none is left out.
        var permissionModes = new JsonObject();
        foreach (var provider in providerItems)
        {
            if (MobileAlta.Text(provider, "providerKey") is { } key && Modes(provider) is { Count: > 0 } modes) permissionModes[key] = modes;
        }

        var defaults = (await resolved.ConfigureAwait(false)).OfType("alta.model.selection").FirstOrDefault();
        return new JsonObject
        {
            ["models"] = modelList, ["prompts"] = agents, ["tiers"] = tierList, ["permissionModes"] = permissionModes,
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
        var (files, attachmentError) = ReadAttachments(body);
        if (attachmentError is not null) return Error(attachmentError);
        if (project is null || (text is null && files is null)) return Error("A project and a prompt are needed.");
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
        if (Option(body, "permissionMode") is { } permissionMode) args.AddRange(["--permission-mode", permissionMode]);
        if (MobileAlta.Flag(body, "worktree")) args.Add("--worktree");
        // Not tied to the request: a phone that gives up must not leave a session half made.
        var created = await MobileAlta.RunAsync(alta, args, null, CancellationToken.None).ConfigureAwait(false);
        var id = created.OfType("alta.session.created").Select(static record => MobileAlta.Text(record, "sessionId")).FirstOrDefault(static value => value is not null);
        if (created.Error is not null || id is null) return Error(created.Error ?? "The session was not created.");

        // A send from a plugin returns when its turn ends: the request waits a moment for a refusal, and the turn goes on.
        // Files go with that first prompt, the same way a send of the window carries them.
        string? attachmentFile = null;
        List<string> sendArgs = ["session", "send", id, "--stdin"];
        if (files is not null) sendArgs.AddRange(["--attachments", attachmentFile = await WriteAttachmentsAsync(files).ConfigureAwait(false)]);
        var sent = await RunPromptAsync(sendArgs, text ?? "", attachmentFile).ConfigureAwait(false);
        if (sent is null) return new JsonObject { ["id"] = id, ["pending"] = true, ["error"] = null };
        return new JsonObject { ["id"] = id, ["sendError"] = sent.Error, ["error"] = null };
    }

    /// <summary>
    /// What the composer of a session offers, from <c>session choices</c> (what the window's composer offers): the selection of
    /// now with its names, the models of the session's provider with their efforts, the agent prompts and the permission modes.
    /// </summary>
    /// <returns>The choices, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> ChoicesAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["session", "choices", id], null, cancellationToken).ConfigureAwait(false);
        if (result.OfType("alta.session.choices").FirstOrDefault() is not { } choices) return Error(result.Error ?? "The choices of the session were not read.");

        var models = new JsonArray();
        foreach (var item in (choices["models"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (MobileAlta.Text(item, "id") is not { } modelId) continue;
            var choice = new JsonObject
            {
                ["id"] = modelId, ["name"] = MobileAlta.Text(item, "name") ?? modelId,
                ["efforts"] = Strings(item["efforts"]), ["startEffort"] = MobileAlta.Text(item, "startEffort"),
            };
            if (item["imageInput"] is JsonValue image && image.TryGetValue<bool>(out var readsImages)) choice["imageInput"] = readsImages;
            models.Add(choice);
        }

        var prompts = new JsonArray();
        foreach (var prompt in (choices["prompts"] as JsonArray ?? []).OfType<JsonObject>())
            if (MobileAlta.Text(prompt, "id") is { } promptId) prompts.Add(new JsonObject { ["id"] = promptId, ["name"] = MobileAlta.Text(prompt, "name") ?? promptId });

        var current = choices["current"] as JsonObject;
        var provider = MobileAlta.Text(current, "providerKey");
        var model = MobileAlta.Text(current, "modelId");
        var agent = MobileAlta.Text(current, "agentPromptId");
        return new JsonObject
        {
            ["current"] = new JsonObject
            {
                ["provider"] = provider, ["providerName"] = provider is null ? null : await sessions.ProviderNameAsync(provider, cancellationToken).ConfigureAwait(false),
                ["model"] = model, ["modelName"] = models.OfType<JsonObject>().Where(item => (string?)item["id"] == model).Select(static item => (string?)item["name"]).FirstOrDefault() ?? model,
                ["reasoning"] = MobileAlta.Text(current, "reasoningEffort"),
                ["agent"] = agent, ["agentName"] = prompts.OfType<JsonObject>().Where(item => (string?)item["id"] == agent).Select(static item => (string?)item["name"]).FirstOrDefault() ?? agent,
                ["permissionMode"] = MobileAlta.Text(current, "permissionMode"),
            },
            ["models"] = models, ["prompts"] = prompts, ["permissionModes"] = Modes(choices), ["error"] = null,
        };
    }

    /// <summary>
    /// Continues a session as the desktop composer does: <c>send</c> when it is idle; while a turn runs, <c>steer</c> into that
    /// turn or <c>queue</c> it for after. A send or a queue carries the model, the effort, the agent prompt and the permission mode
    /// chosen on the phone, which stay the session's; a steer joins the running turn and takes none.
    /// </summary>
    internal async Task<JsonObject?> PromptAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        var text = Prompt(body);
        var mode = Option(body, "mode") ?? "send";
        var (files, attachmentError) = ReadAttachments(body);
        if (attachmentError is not null) return Error(attachmentError);
        if ((text is null && files is null) || mode is not ("send" or "steer" or "queue")) return Error("A prompt is needed.");
        // A file joins a new prompt. It is not steered into the turn that runs, and it is not left in the text queue.
        if (files is not null && mode != "send") return Error("Attachments go with the next prompt, once the session is idle.");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        List<string> args = ["session", mode, id, "--stdin"];
        if (mode != "steer")
        {
            if (Option(body, "model") is { } model) args.AddRange(["--model", model]);
            if (Option(body, "reasoning") is { } reasoning) args.AddRange(["--reasoning", reasoning]);
            if (Option(body, "agent") is { } agent) args.AddRange(["--prompt-id", agent]);
            if (Option(body, "permissionMode") is { } permissionMode) args.AddRange(["--permission-mode", permissionMode]);
        }

        // The phone sends to a session it saw idle: when a turn started on the computer meanwhile, the prompt waits for it
        // in the queue, and is never steered into a turn the phone did not see. Files cannot wait there: the send is refused
        // while the session works, and the phone keeps them.
        string? attachmentFile = null;
        if (files is not null) args.AddRange(["--attachments", attachmentFile = await WriteAttachmentsAsync(files).ConfigureAwait(false)]);
        else if (mode == "send") args.Add("--queue-if-busy");

        var result = await RunPromptAsync(args, text ?? "", attachmentFile).ConfigureAwait(false);
        if (result is null) return new JsonObject { ["ok"] = true, ["mode"] = mode, ["pending"] = true, ["error"] = null };
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

    // The permission modes of a record, each with its name and its description; a mode given as a bare id is read too.
    private static JsonArray Modes(JsonObject record)
    {
        var modes = new JsonArray();
        foreach (var node in record["permissionModes"] as JsonArray ?? [])
        {
            var mode = node as JsonObject;
            var id = mode is null ? (node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null) : MobileAlta.Text(mode, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            modes.Add(new JsonObject { ["id"] = id, ["name"] = MobileAlta.Text(mode, "name"), ["description"] = MobileAlta.Text(mode, "description") });
        }

        return modes;
    }

    private static JsonArray Strings(JsonNode? node) => new([.. (node as JsonArray ?? []).Select(static item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
        .OfType<string>().Select(static text => (JsonNode)text)]);

    private static string? Option(JsonObject body, string name) =>MobileAlta.Text(body, name)?.Trim() is { } value && IsId(value) ? value : null;

    private static string? Prompt(JsonObject body) => MobileAlta.Text(body, "text") is { Length: <= MaximumPrompt } text && text.Trim().Length > 0 ? text : null;

    // The files of a prompt, or null when it has none. An error names one the host would refuse.
    private static (JsonArray? Files, string? Error) ReadAttachments(JsonObject body)
    {
        if (body["attachments"] is null or JsonArray { Count: 0 }) return (null, null);
        if (body["attachments"] is not JsonArray rows) return (null, "The attachments could not be read.");
        if (rows.Count > MaximumAttachments) return (null, $"A prompt has at most {MaximumAttachments} attachments.");
        long total = 0;
        var files = new JsonArray();
        foreach (var row in rows)
        {
            if (row is not JsonObject attachment
                || MobileAlta.Text(attachment, "title") is not { } title || title.Length > 80 || title.Any(static c => char.IsControl(c) || c is '/' or '\\')
                || MobileAlta.Text(attachment, "mediaType") is not { } mediaType || mediaType.Length > 128 || mediaType.Count(static c => c == '/') != 1
                || mediaType.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c))
                || attachment["base64"] is not JsonValue data || !data.TryGetValue<string>(out var base64) || !Base64Size(base64, out var bytes))
                return (null, "An attachment needs a name, a type and its content.");
            if (bytes is 0 or > MaximumFileBytes) return (null, "A file can be from 1 byte to 20 MB.");
            if ((total += bytes) > MaximumTotalBytes) return (null, "A prompt's files are limited to 64 MB in all.");
            files.Add(new JsonObject { ["title"] = title, ["mediaType"] = mediaType, ["base64"] = base64 });
        }

        return (files, null);
    }

    private static bool Base64Size(string data, out long bytes)
    {
        bytes = 0;
        if (data.Length == 0) return true;
        if (data.Length % 4 != 0) return false;
        // Longer than 20 MB of bytes: the size is refused, without reading it all into a buffer here.
        if (data.Length > (MaximumFileBytes + 2) / 3 * 4) { bytes = MaximumFileBytes + 1; return true; }
        for (var index = 0; index < data.Length; index++)
        {
            var character = data[index];
            if (character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/') continue;
            if (character == '=' && index >= data.Length - 2) continue;
            return false;
        }

        bytes = (long)data.Length / 4 * 3 - (data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith('=') ? 1 : 0);
        return bytes > 0;
    }

    /// <summary>Deletes attachment files left by a turn that did not finish, from a previous run of the plugin.</summary>
    internal static void RemoveStaleAttachments(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "alta-attach-*.json"))
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<string> WriteAttachmentsAsync(JsonArray attachments)
    {
        Directory.CreateDirectory(_attachmentsDirectory);
        var path = Path.Combine(_attachmentsDirectory, "alta-attach-" + Guid.NewGuid().ToString("N") + ".json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using (var writer = new Utf8JsonWriter(stream))
        {
            attachments.WriteTo(writer);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        return path;
    }

    // Runs a prompt and drops its attachment file when the command ends, including when this request has stopped waiting.
    private async Task<MobileAltaResult?> RunPromptAsync(IReadOnlyList<string> args, string text, string? attachmentFile)
    {
        var run = MobileAlta.RunAsync(alta, args, text, CancellationToken.None);
        if (attachmentFile is not null)
            _ = run.ContinueWith(static (_, path) =>
            {
                try { File.Delete((string)path!); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }, attachmentFile, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (await Task.WhenAny(run, Task.Delay(Patience, CancellationToken.None)).ConfigureAwait(false) != run) return null;
        return await run.ConfigureAwait(false);
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private sealed record MobileProject(string Id, string Name, string? Path, string Space);
}
