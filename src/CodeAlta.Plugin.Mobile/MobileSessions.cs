using System.Globalization;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The sessions the phone sees: those of the spaces the plugin is assigned to (Settings &gt; Plugins), archived ones left
/// out, read with <c>alta session list</c>, <c>alta session status</c> and <c>alta session events</c>. A session of another
/// space is not found, so the phone can neither read it nor act on it.
/// </summary>
internal sealed class MobileSessions(IPluginAltaService alta, IPluginSpaceService spaces, MobileLive live)
{
    /// <summary>The sessions of a page when the phone does not say.</summary>
    internal const int DefaultPageSize = 6;

    /// <summary>The most sessions of a page.</summary>
    internal const int MaximumPageSize = 50;

    private const int EventsPerRead = 400;
    private const int MaximumReads = 40;
    private const int MaximumText = 20_000;
    private const int MaximumSummary = 300;
    private static readonly TimeSpan NamesLife = TimeSpan.FromSeconds(30);

    private volatile MobileNames? _names;

    /// <summary>The ids of the spaces of the plugin; empty when it is in every space.</summary>
    internal IReadOnlyList<string> Spaces => spaces.GetAssignedSpaces();

    /// <summary>How many projects the phone keeps to at most: each is a list asked of alta.</summary>
    internal const int MaximumProjects = 16;

    /// <summary>A page of the sessions, the latest activity first.</summary>
    /// <param name="offset">The sessions to skip.</param>
    /// <param name="limit">The sessions of the page.</param>
    /// <param name="projectIds">The projects the phone keeps to (the first <see cref="MaximumProjects"/>), or null or none for every project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<JsonObject> ListAsync(int offset, int limit, IReadOnlyList<string>? projectIds, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaximumPageSize);
        var assigned = Spaces;
        var projects = (projectIds ?? []).Where(MobileApi.IsId).Distinct(StringComparer.Ordinal).Take(MaximumProjects).ToArray();
        var sessions = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? error = null;
        // alta lists the latest activity first: each space and each project gives at most the sessions up to the end of
        // the page, and one more that says whether there are more.
        var wanted = (offset + limit + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var space in assigned.Count == 0 ? [null] : assigned.Cast<string?>())
        foreach (var project in projects.Length == 0 ? [null] : projects.Cast<string?>())
        {
            List<string> args = ["session", "list", "--limit", wanted];
            if (project is not null) args.AddRange(["--project", project]);
            if (space is not null) args.AddRange(["--space", space]);
            var result = await MobileAlta.RunAsync(alta, args, null, cancellationToken).ConfigureAwait(false);
            error ??= result.Error;
            foreach (var item in result.OfType("alta.session.item"))
                if (MobileAlta.Text(item, "sessionId") is { } id && !IsArchived(item) && seen.Add(id)) sessions.Add(item);
        }

        var ordered = sessions.OrderByDescending(LastActivity).ThenBy(static item => MobileAlta.Text(item, "title"), StringComparer.OrdinalIgnoreCase).ToList();
        var names = await NamesAsync(cancellationToken).ConfigureAwait(false);
        var page = ordered.Skip(offset).Take(limit).Select(item => (JsonNode)Describe(item, names)).ToArray();
        return new JsonObject
        {
            ["sessions"] = new JsonArray(page), ["next"] = offset + page.Length, ["hasMore"] = ordered.Count > offset + page.Length,
            ["projects"] = new JsonArray([.. projects.Select(static project => (JsonNode)project)]), ["machine"] = Environment.MachineName, ["error"] = error,
            // The spaces the phone sees; empty when it sees every space.
            ["spaces"] = new JsonArray([.. assigned.Select(space => (JsonNode)names.Space(space))]),
        };
    }

    /// <summary>A space as the phone shows it: its id, its name, its color and its icon.</summary>
    internal async Task<JsonObject> SpaceAsync(string spaceId, CancellationToken cancellationToken)
        => (await NamesAsync(cancellationToken).ConfigureAwait(false)).Space(spaceId);

    /// <summary>The name of a provider, as the desktop shows it; its key when alta names it not.</summary>
    internal async Task<string> ProviderNameAsync(string providerKey, CancellationToken cancellationToken)
        => (await NamesAsync(cancellationToken).ConfigureAwait(false)).Providers.GetValueOrDefault(providerKey) ?? providerKey;

    /// <summary>One session as the list shows it, or null when the phone does not see it.</summary>
    internal async Task<JsonObject?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var status = await FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return status is null || IsArchived(status) ? null : Describe(status, await NamesAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The status of a session of the spaces of the plugin, archived or not; null for an id that is no session, or a session
    /// of another space.
    /// </summary>
    internal async Task<JsonObject?> FindAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!MobileApi.IsId(sessionId)) return null;
        var result = await MobileAlta.RunAsync(alta, ["session", "status", sessionId], null, cancellationToken).ConfigureAwait(false);
        if (result.OfType("alta.session.status").FirstOrDefault() is not { } status) return null;
        var assigned = Spaces;
        // A session written before sessions had a space is in every space of its project.
        return assigned.Count == 0 || MobileAlta.Text(status, "spaceId") is not { } space || assigned.Contains(space, StringComparer.OrdinalIgnoreCase) ? status : null;
    }

    /// <summary>
    /// The timeline of a session after a sequence number, read with <c>alta session events</c> a page at a time, from the
    /// journal the desktop timeline is built from. Tool output is left out: the phone shows the calls.
    /// </summary>
    /// <returns>The events, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> EventsAsync(string sessionId, long since, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null) return null;
        var events = new JsonArray();
        string? error = null;
        for (var read = 0; read < MaximumReads; read++)
        {
            var result = await MobileAlta.RunAsync(alta, ["session", "events", sessionId, "--since", since.ToString(CultureInfo.InvariantCulture),
                "--limit", EventsPerRead.ToString(CultureInfo.InvariantCulture), "--no-tool-output", "--include", "user,assistant,reasoning,tool,host",
                "--fields", "sequenceNumber,timestamp,kind,content,metadata"], null, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                error = result.Error;
                break;
            }

            var count = 0;
            foreach (var record in result.OfType("alta.session.event"))
            {
                count++;
                var sequence = record["sequenceNumber"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
                if (sequence > since) since = sequence;
                var kind = MobileAlta.Text(record, "kind") ?? "";
                if (kind is "host.usageupdated" or "host.modelchanged") continue;
                var text = string.Concat((record["content"] as JsonArray ?? []).Select(static part => (string?)part?["text"] ?? ""));
                var metadata = record["metadata"] as JsonObject;
                JsonArray? attachments = null;
                if (kind == "user.message" && metadata?["attachments"] is JsonArray listed && listed.Count > 0) (text, attachments) = WithoutPaths(text, listed);
                if (text.Length > MaximumText) text = text[..MaximumText] + "…";
                var entry = new JsonObject
                {
                    ["seq"] = sequence, ["at"] = MobileAlta.Text(record, "timestamp"), ["kind"] = kind, ["text"] = text,
                    ["activityId"] = MobileAlta.Text(metadata, "activityId"),
                };
                if (attachments is { Count: > 0 }) entry["attachments"] = attachments;
                events.Add(entry);
            }

            // A read that filled its page, or whose output the host cut, says there may be more after the last one read.
            var more = result.Truncated || count >= EventsPerRead || result.OfType("alta.session.events.summary").Any(static summary => MobileAlta.Flag(summary, "truncated"));
            if (!more || count == 0) break;
        }

        return new JsonObject { ["since"] = since, ["events"] = events, ["error"] = error };
    }

    /// <summary>
    /// The text of a user message without the lines that name its files with their paths on this computer
    /// (<c>Local image (title): path</c>, <c>Attached file (title): path</c>), and its files as the phone sees them: a title, a
    /// type and the id of the saved copy (its name, without a folder), which alta lists in the metadata of the message.
    /// </summary>
    internal static (string Text, JsonArray Attachments) WithoutPaths(string text, JsonArray listed)
    {
        var attachments = new JsonArray();
        var lines = new List<string>(text.Split('\n'));
        var removed = false;
        foreach (var item in listed.OfType<JsonObject>())
        {
            if (MobileAlta.Text(item, "title") is not { } title || MobileAlta.Text(item, "id") is not { } id || id.Contains('/') || id.Contains('\\')) continue;
            attachments.Add(new JsonObject { ["title"] = title, ["mediaType"] = MobileAlta.Text(item, "mediaType") ?? "application/octet-stream", ["id"] = id });
            // The last line that names it, as the host writes them.
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var line = lines[index].AsSpan().TrimEnd('\r');
                var named = line.StartsWith($"Local image ({title}): ", StringComparison.Ordinal) || line.StartsWith("Local image: ", StringComparison.Ordinal)
                    || line.StartsWith($"Attached file ({title}): ", StringComparison.Ordinal) || line.StartsWith($"Attached file ({id}): ", StringComparison.Ordinal);
                if (!named || !(line.EndsWith("/" + id, StringComparison.Ordinal) || line.EndsWith("\\" + id, StringComparison.Ordinal))) continue;
                lines.RemoveAt(index);
                removed = true;
                break;
            }
        }

        return (removed ? string.Join('\n', lines).TrimEnd('\r', '\n') : text, attachments);
    }

    // The names of the projects, the spaces, the models and the agent prompts, read at most every half minute: a page of the
    // list asks for them each time.
    private async Task<MobileNames> NamesAsync(CancellationToken cancellationToken)
    {
        if (_names is { } known && DateTimeOffset.UtcNow - known.ReadAt < NamesLife) return known;
        var projects = MobileAlta.RunAsync(alta, ["project", "list", "--detailed", "--all"], null, cancellationToken);
        var spaceList = MobileAlta.RunAsync(alta, ["space", "list"], null, cancellationToken);
        var models = MobileAlta.RunAsync(alta, ["model", "list", "--detailed"], null, cancellationToken);
        var prompts = MobileAlta.RunAsync(alta, ["prompt", "list"], null, cancellationToken);
        var providers = MobileAlta.RunAsync(alta, ["provider", "list", "--detailed"], null, cancellationToken);
        await Task.WhenAll(projects, spaceList, models, prompts, providers).ConfigureAwait(false);
        var names = new MobileNames(DateTimeOffset.UtcNow);
        foreach (var project in (await projects.ConfigureAwait(false)).OfType("alta.project.item"))
            if (MobileAlta.Text(project, "projectId") is { } id) names.Projects[id] = MobileAlta.Text(project, "displayName") ?? MobileAlta.Text(project, "name") ?? MobileAlta.Text(project, "slug") ?? id;
        foreach (var space in (await spaceList.ConfigureAwait(false)).OfType("alta.space.item"))
            if (MobileAlta.Text(space, "id") is { } id) names.Spaces[id] = space;
        foreach (var model in (await models.ConfigureAwait(false)).OfType("alta.model.item"))
        {
            if (MobileAlta.Text(model, "providerKey") is not { } provider) continue;
            if (MobileAlta.Text(model, "provider") is { } providerName) names.Providers[provider] = providerName;
            if (MobileAlta.Text(model, "modelId") is { } modelId) names.Models[(provider, modelId)] = MobileAlta.Text(model, "displayName") ?? modelId;
        }

        // A provider is named as the window names it; the provider of a model record is its configured name, or its key.
        foreach (var provider in (await providers.ConfigureAwait(false)).OfType("alta.provider.item"))
            if (MobileAlta.Text(provider, "providerKey") is { } key && MobileAlta.Text(provider, "displayName") is { } name) names.Providers[key] = name;

        foreach (var prompt in (await prompts.ConfigureAwait(false)).OfType("alta.prompt"))
            if (MobileAlta.Text(prompt, "promptId") is { } id) names.Prompts[id] = MobileAlta.Text(prompt, "name") ?? id;
        return _names = names;
    }

    private JsonObject Describe(JsonObject item, MobileNames names)
    {
        var id = MobileAlta.Text(item, "sessionId")!;
        var projectId = MobileAlta.Text(item, "projectId");
        var at = LastActivity(item);
        var selection = item["modelSelection"] as JsonObject;
        var provider = MobileAlta.Text(selection, "providerKey") ?? MobileAlta.Text(item, "providerKey");
        var model = MobileAlta.Text(selection, "modelId");
        var agent = MobileAlta.Text(selection, "agentPromptId") ?? MobileAlta.Text(item, "agentPromptId");
        return new JsonObject
        {
            ["id"] = id,
            ["title"] = MobileAlta.Text(item, "title"),
            ["projectId"] = projectId,
            ["project"] = projectId is not null && names.Projects.TryGetValue(projectId, out var projectName) ? projectName : null,
            ["space"] = names.Space(MobileAlta.Text(item, "spaceId") ?? "default"),
            // What the session runs on, as the composer of the desktop says it.
            ["model"] = provider is null ? null : new JsonObject
            {
                ["provider"] = provider, ["providerName"] = names.Providers.GetValueOrDefault(provider) ?? provider,
                ["id"] = model, ["name"] = model is null ? null : names.Models.GetValueOrDefault((provider, model)) ?? model,
                ["reasoning"] = MobileAlta.Text(selection, "reasoningEffort"),
            },
            ["agent"] = agent is null ? null : new JsonObject { ["id"] = agent, ["name"] = names.Prompts.GetValueOrDefault(agent) ?? agent },
            // The last folder of the path, whatever the system that wrote it.
            ["worktree"] = MobileAlta.Text(item, "worktreeDirectory") is { } worktree ? worktree.TrimEnd('\\', '/').Split('\\', '/')[^1] : null,
            ["parentId"] = MobileAlta.Text(item, "parentSessionId"),
            ["summary"] = PlainSummary(MobileAlta.Text(item, "latestSummary")),
            ["lastActivity"] = at == DateTimeOffset.MinValue ? null : at.ToString("O", CultureInfo.InvariantCulture),
            // What the plugin saw of the session since it started; else what alta says of it now.
            ["live"] = live.Describe(id) ?? MobileLive.Quiet(id, projectId, MobileAlta.Flag(item, "isRunning")),
        };
    }

    // The names the list shows, as alta gave them at one time.
    private sealed class MobileNames(DateTimeOffset readAt)
    {
        internal DateTimeOffset ReadAt { get; } = readAt;

        internal Dictionary<string, string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, JsonObject> Spaces { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<(string Provider, string Model), string> Models { get; } = [];

        internal Dictionary<string, string> Prompts { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal JsonObject Space(string id)
        {
            Spaces.TryGetValue(id, out var space);
            return new JsonObject
            {
                ["id"] = id, ["name"] = MobileAlta.Text(space, "name") ?? id, ["color"] = MobileAlta.Text(space, "color"), ["icon"] = MobileAlta.Text(space, "icon"),
            };
        }
    }

    // The summary as one line of plain text: a row shows it in a line, without the marks of its Markdown.
    internal static string? PlainSummary(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        // The prose only: the lines of a code block are left out with its fences.
        var prose = new List<string>();
        var inCode = false;
        foreach (var line in markdown.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            else if (!inCode && MobileActivity.PlainLine(line) is { Length: > 0 } plain) prose.Add(plain);
        }

        var text = string.Join(' ', prose);
        if (text.Length == 0) return null;
        return text.Length <= MaximumSummary ? text : text[..(MaximumSummary - 1)] + "…";
    }

    private static bool IsArchived(JsonObject item)
        => string.Equals(MobileAlta.Text(item, "state"), "archived", StringComparison.OrdinalIgnoreCase)
           || string.Equals(MobileAlta.Text(item, "status"), "archived", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset LastActivity(JsonObject item)
    {
        foreach (var name in (string[])["lastActiveAt", "updatedAt", "createdAt"])
            if (MobileAlta.Text(item, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return at;
        return DateTimeOffset.MinValue;
    }
}
