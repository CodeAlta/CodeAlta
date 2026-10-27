using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
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
internal sealed class MobileApi(IPluginAltaService alta, MobileSessions sessions, string? attachmentsDirectory = null, TimeProvider? clock = null, TimeSpan? promptPatience = null)
{
    /// <summary>The longest prompt the phone sends.</summary>
    internal const int MaximumPrompt = 100_000;

    /// <summary>Most files of one prompt, as a send from the desktop takes.</summary>
    private const int MaximumAttachments = 64;

    /// <summary>Largest file, and largest total, in bytes: images count, as the phone counts them when they are added.</summary>
    private const long MaximumFileBytes = 20L * 1024 * 1024;

    private const long MaximumTotalBytes = 64L * 1024 * 1024;

    /// <summary>The start of the name of a file of attachments written for alta: <c>alta-attach-&lt;process&gt;-&lt;id&gt;.json</c>.</summary>
    internal const string AttachmentFilePrefix = "alta-attach-";

    // How long a request waits for a stop: Tailscale Serve and the phone give up on a long answer, and the live stream says the rest.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    // A prompt is answered once alta handed it over (--no-wait): its files saved and converted, its turn started or the prompt
    // queued, or what refused it. This bound only keeps the phone from waiting on a command that hangs, before the phone or
    // Tailscale Serve would give up on the request.
    private static readonly TimeSpan DefaultPromptPatience = TimeSpan.FromSeconds(50);

    private static readonly string[] Tiers = ["complex", "standard", "quick"];

    // JSON as the page writes it (JSON.stringify): a '+' of base64 is not escaped.
    private static readonly JsonSerializerOptions AsThePhoneWrites = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly SearchValues<byte> Base64Alphabet = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8);

    // The plugin's data folder. Tests that omit it use a directory of their own under the temp folder.
    private readonly string _attachmentsDirectory = attachmentsDirectory ?? Path.Combine(Path.GetTempPath(), "alta-mobile-attach");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _promptPatience = promptPatience ?? DefaultPromptPatience;

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

    /// <summary>Creates a session from a body read whole (a small one, or a test's).</summary>
    internal async Task<JsonObject> CreateAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var parsed = MobilePromptBody.Parse(body.ToJsonString(AsThePhoneWrites))!;
        return await CreateAsync(parsed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a session in a project with the chosen model, effort and agent, then sends its first prompt and its files.</summary>
    internal async Task<JsonObject> CreateAsync(MobilePromptBody body, CancellationToken cancellationToken)
    {
        var fields = body.Fields;
        var project = Option(fields, "project");
        var (text, textError) = Prompt(fields);
        if (textError is not null) return Error(textError);
        var (files, attachmentError) = CheckAttachments(body.Attachments);
        if (attachmentError is not null) return Error(attachmentError);
        if (project is null || (text is null && files is null)) return Error("A project and a prompt are needed.");
        var tier = Option(fields, "tier");
        if (tier is not null && !Tiers.Contains(tier, StringComparer.Ordinal)) return Error("Unknown tier.");
        var (projects, _) = await ReadProjectsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.FirstOrDefault(known => string.Equals(known.Id, project, StringComparison.OrdinalIgnoreCase)) is not { } chosen) return Error("Unknown project.");

        // The session goes to a space of the plugin that has the project: without one alta would put it in the space the window
        // shows, which the phone may not see.
        List<string> args = ["session", "create", "--project", chosen.Id, "--space", chosen.Space];
        if (tier is not null) args.AddRange(["--tier", tier]);
        else if (Option(fields, "provider") is { } provider && Option(fields, "model") is { } model) args.AddRange(["--provider", provider, "--model", model]);
        if (Option(fields, "reasoning") is { } reasoning) args.AddRange(["--reasoning", reasoning]);
        if (Option(fields, "prompt") is { } prompt) args.AddRange(["--prompt-id", prompt]);
        if (Option(fields, "permissionMode") is { } permissionMode) args.AddRange(["--permission-mode", permissionMode]);
        if (MobileAlta.Flag(fields, "worktree")) args.Add("--worktree");
        // Not tied to the request: a phone that gives up must not leave a session half made.
        var created = await MobileAlta.RunAsync(alta, args, null, CancellationToken.None).ConfigureAwait(false);
        var id = created.OfType("alta.session.created").Select(static record => MobileAlta.Text(record, "sessionId")).FirstOrDefault(static value => value is not null);
        if (created.Error is not null || id is null) return Error(created.Error ?? "The session was not created.");

        // Files go with that first prompt, the same way a send of the window carries them.
        List<string> sendArgs = ["session", "send", id, "--stdin", "--no-wait"];
        string? attachmentFile = null;
        if (files is not null)
        {
            if (await WriteAttachmentsAsync(files.Value).ConfigureAwait(false) is not { } written)
                return new JsonObject { ["id"] = id, ["sendError"] = "The files could not be kept for the prompt.", ["error"] = null };
            body.ReleaseAttachments();
            sendArgs.AddRange(["--attachments", attachmentFile = written]);
        }

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

    /// <summary>Continues a session from a body read whole (a small one, or a test's).</summary>
    internal async Task<JsonObject?> PromptAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        using var parsed = MobilePromptBody.Parse(body.ToJsonString(AsThePhoneWrites))!;
        return await PromptAsync(id, parsed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues a session as the desktop composer does: <c>send</c> when it is idle; while a turn runs, <c>steer</c> into that
    /// turn or <c>queue</c> it for after. A send or a queue carries the model, the effort, the agent prompt and the permission mode
    /// chosen on the phone, which stay the session's, and the files of the prompt; a steer joins the running turn and takes none.
    /// </summary>
    internal async Task<JsonObject?> PromptAsync(string id, MobilePromptBody body, CancellationToken cancellationToken)
    {
        var fields = body.Fields;
        var (text, textError) = Prompt(fields);
        if (textError is not null) return Error(textError);
        var mode = Option(fields, "mode") ?? "send";
        var (files, attachmentError) = CheckAttachments(body.Attachments);
        if (attachmentError is not null) return Error(attachmentError);
        if ((text is null && files is null) || mode is not ("send" or "steer" or "queue")) return Error("A prompt is needed.");
        // A file joins a new prompt or a queued one: it is not steered into the turn that runs.
        if (files is not null && mode == "steer") return Error("Files are not steered into a turn: they go with Send or Queue.");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        List<string> args = ["session", mode, id, "--stdin"];
        if (mode != "steer")
        {
            if (Option(fields, "model") is { } model) args.AddRange(["--model", model]);
            if (Option(fields, "reasoning") is { } reasoning) args.AddRange(["--reasoning", reasoning]);
            if (Option(fields, "agent") is { } agent) args.AddRange(["--prompt-id", agent]);
            if (Option(fields, "permissionMode") is { } permissionMode) args.AddRange(["--permission-mode", permissionMode]);
        }

        // The phone sends to a session it saw idle: when a turn started on the computer meanwhile, the prompt waits for it
        // in the queue with its files, and is never steered into a turn the phone did not see.
        if (mode == "send") args.AddRange(["--queue-if-busy", "--no-wait"]);
        string? attachmentFile = null;
        if (files is not null)
        {
            if (await WriteAttachmentsAsync(files.Value).ConfigureAwait(false) is not { } written) return Error("The files could not be kept for the prompt.");
            body.ReleaseAttachments();
            args.AddRange(["--attachments", attachmentFile = written]);
        }

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
        if (await Task.WhenAny(abort, Task.Delay(Patience, _clock, CancellationToken.None)).ConfigureAwait(false) != abort)
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

    // The text of a prompt: none when it is empty or blank, and a refusal when it is longer than the phone sends. A prompt
    // with files may have no text, never a text cut away.
    private static (string? Text, string? Error) Prompt(JsonObject body)
    {
        if (MobileAlta.Text(body, "text") is not { } text) return (null, null);
        return text.Length > MaximumPrompt ? (null, "A prompt has at most 100,000 characters.") : (text, null);
    }

    // The files of a prompt, checked as the host would check them, or null when it has none. An error names what the host
    // would refuse; nothing is decoded but the header of an image.
    private static (JsonElement? Files, string? Error) CheckAttachments(JsonElement? attachments)
    {
        if (attachments is not { } rows || rows.ValueKind == JsonValueKind.Null || rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == 0) return (null, null);
        if (rows.ValueKind != JsonValueKind.Array) return (null, "The attachments could not be read.");
        if (rows.GetArrayLength() > MaximumAttachments) return (null, $"A prompt has at most {MaximumAttachments} attachments.");
        long total = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || Text(row, "title") is not { } title || title.Length > 80 || title.Any(static c => char.IsControl(c) || c is '/' or '\\')
                || Text(row, "mediaType") is not { } mediaType || mediaType.Length > 128 || mediaType.Count(static c => c == '/') != 1
                || mediaType.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ';')
                || !row.TryGetProperty("base64", out var data) || data.ValueKind != JsonValueKind.String)
                return (null, "An attachment needs a name, a type and its content.");
            var bytes = Measure(data, mediaType == ImageMediaType, out var readImage);
            if (bytes < 0) return (null, $"The content of {title} is not valid base64.");
            if (bytes is 0 or > MaximumFileBytes) return (null, "A file can be from 1 byte to 20 MB.");
            if ((total += bytes) > MaximumTotalBytes) return (null, "A prompt's files are limited to 64 MB in all.");
            if (!readImage) return (null, $"{title} is not an image the host reads: an 8-bit RGB or RGBA PNG, not interlaced.");
        }

        return (rows, null);
    }

    // An image is a PNG, as the host takes it (OwnedPromptImages); any other type is a file.
    private const string ImageMediaType = "image/png";

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    // The size of the content of an attachment, from its base64 as the phone sent it (-1 when it is not canonical base64), and
    // for an image whether the host reads it. A JSON string of base64 has nothing to unescape, unless a sender escaped its slashes.
    private static long Measure(JsonElement data, bool image, out bool readImage)
    {
        var raw = JsonMarshal.GetRawUtf8Value(data)[1..^1];
        return raw.Contains((byte)'\\') ? Measure(Encoding.UTF8.GetBytes(data.GetString()!), image, out readImage) : Measure(raw, image, out readImage);
    }

    private static long Measure(ReadOnlySpan<byte> base64, bool image, out bool readImage)
    {
        var size = Base64Size(base64);
        readImage = !image || size > 0 && IsHostPng(base64);
        return size;
    }

    /// <summary>The size of the bytes of canonical base64 (its alphabet, its padding only at its end, no bit left over), or -1.</summary>
    internal static long Base64Size(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        if (data.Length % 4 != 0) return -1;
        var padding = data[^1] == '=' ? data[^2] == '=' ? 2 : 1 : 0;
        var size = (long)data.Length / 4 * 3 - padding;
        // Larger than a file can be: refused for its size, without reading it all.
        if (size > MaximumFileBytes) return size;
        var body = data[..^padding];
        if (body.IndexOfAnyExcept(Base64Alphabet) >= 0) return -1;
        // What the host compares with the base64 of the bytes it decoded: the bits after the last byte are zero.
        var last = Base64Value(body[^1]);
        if (padding == 1 && (last & 0b11) != 0 || padding == 2 && (last & 0b1111) != 0) return -1;
        return size;
    }

    private static int Base64Value(byte character) => character switch
    {
        >= (byte)'A' and <= (byte)'Z' => character - 'A',
        >= (byte)'a' and <= (byte)'z' => character - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => character - '0' + 52,
        (byte)'+' => 62,
        _ => 63,
    };

    // The header of a PNG the host reads: its signature, then an IHDR of an 8-bit RGB or RGBA image, not interlaced. The host
    // checks the rest (its chunks, its data) when it saves the prompt.
    private static bool IsHostPng(ReadOnlySpan<byte> base64)
    {
        // The first 33 bytes: the signature, the length and the type of the first chunk, and its 13 bytes.
        if (base64.Length < 76) return false;
        Span<byte> header = stackalloc byte[33];
        if (Base64.DecodeFromUtf8(base64[..44], header, out _, out var written, isFinalBlock: false) != OperationStatus.Done || written != 33) return false;
        return header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(header[8..]) == 13 && header[12..16].SequenceEqual("IHDR"u8)
            && BinaryPrimitives.ReadInt32BigEndian(header[16..]) > 0 && BinaryPrimitives.ReadInt32BigEndian(header[20..]) > 0
            && header[24] == 8 && header[25] is 2 or 6 && header[26] == 0 && header[27] == 0 && header[28] == 0;
    }

    /// <summary>
    /// Deletes the files of attachments left by a CodeAlta that ended before its command read them. The developer instance and
    /// the normal one share the folder: a file of a process that still runs is left to it, unless it is a day old.
    /// </summary>
    internal static void RemoveStaleAttachments(string directory, TimeProvider? clock = null)
    {
        if (!Directory.Exists(directory)) return;
        var oldest = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime - TimeSpan.FromDays(1);
        foreach (var path in Directory.EnumerateFiles(directory, AttachmentFilePrefix + "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path).AsSpan(AttachmentFilePrefix.Length);
                var dash = name.IndexOf('-');
                if (dash > 0 && int.TryParse(name[..dash], out var process) && IsRunning(process) && File.GetLastWriteTimeUtc(path) >= oldest) continue;
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsRunning(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    // Writes the files of a prompt for alta, their base64 copied as the phone sent it. Null when the file could not be written.
    private async Task<string?> WriteAttachmentsAsync(JsonElement rows)
    {
        var path = Path.Combine(_attachmentsDirectory, $"{AttachmentFilePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            writer.WriteStartArray();
            foreach (var row in rows.EnumerateArray())
            {
                writer.WriteStartObject();
                writer.WriteString("title", Text(row, "title"));
                writer.WriteString("mediaType", Text(row, "mediaType"));
                var base64 = row.GetProperty("base64");
                var raw = JsonMarshal.GetRawUtf8Value(base64);
                writer.WritePropertyName("base64");
                if (raw.Contains((byte)'\\')) writer.WriteStringValue(base64.GetString());
                else writer.WriteRawValue(raw, skipInputValidation: true);
                writer.WriteEndObject();
                // One file at a time in the buffer of the writer.
                await writer.FlushAsync().ConfigureAwait(false);
            }

            writer.WriteEndArray();
            await writer.FlushAsync().ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Delete(path);
            return null;
        }
    }

    // Runs a prompt that returns once alta handed it over (--no-wait), and drops its file of attachments when the command ends,
    // also after this request stopped waiting. Null when the command outlived the patience of the phone.
    private async Task<MobileAltaResult?> RunPromptAsync(IReadOnlyList<string> args, string text, string? attachmentFile)
    {
        var run = MobileAlta.RunAsync(alta, args, text, CancellationToken.None);
        if (attachmentFile is not null)
            _ = run.ContinueWith(static (_, path) => Delete((string)path!), attachmentFile, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        using var patience = new CancellationTokenSource();
        var waited = Task.Delay(_promptPatience, _clock, patience.Token);
        if (await Task.WhenAny(run, waited).ConfigureAwait(false) != run) return null;
        await patience.CancelAsync().ConfigureAwait(false);
        return await run.ConfigureAwait(false);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private sealed record MobileProject(string Id, string Name, string? Path, string Space);
}
