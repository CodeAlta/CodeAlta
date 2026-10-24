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

    /// <summary>A page of the sessions, the latest activity first.</summary>
    /// <param name="offset">The sessions to skip.</param>
    /// <param name="limit">The sessions of the page.</param>
    /// <param name="projectId">The project the phone keeps to, or null for every project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<JsonObject> ListAsync(int offset, int limit, string? projectId, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaximumPageSize);
        var assigned = Spaces;
        var sessions = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? error = null;
        // alta lists the latest activity first: each space gives at most the sessions up to the end of the page, and one more
        // that says whether there are more.
        var wanted = (offset + limit + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var space in assigned.Count == 0 ? [null] : assigned.Cast<string?>())
        {
            List<string> args = ["session", "list", "--limit", wanted];
            if (projectId is not null) args.AddRange(["--project", projectId]);
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
            ["project"] = projectId, ["machine"] = Environment.MachineName, ["error"] = error,
            // The spaces the phone sees; empty when it sees every space.
            ["spaces"] = new JsonArray([.. assigned.Select(space => (JsonNode)names.Space(space))]),
        };
    }

    /// <summary>A space as the phone shows it: its id, its name, its color and its icon.</summary>
    internal async Task<JsonObject> SpaceAsync(string spaceId, CancellationToken cancellationToken)
        => (await NamesAsync(cancellationToken).ConfigureAwait(false)).Space(spaceId);

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
                if (text.Length > MaximumText) text = text[..MaximumText] + "…";
                events.Add(new JsonObject
                {
                    ["seq"] = sequence, ["at"] = MobileAlta.Text(record, "timestamp"), ["kind"] = kind, ["text"] = text,
                    ["activityId"] = MobileAlta.Text(record["metadata"] as JsonObject, "activityId"),
                });
            }

            // A read that filled its page, or whose output the host cut, says there may be more after the last one read.
            var more = result.Truncated || count >= EventsPerRead || result.OfType("alta.session.events.summary").Any(static summary => MobileAlta.Flag(summary, "truncated"));
            if (!more || count == 0) break;
        }

        return new JsonObject { ["since"] = since, ["events"] = events, ["error"] = error };
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
        await Task.WhenAll(projects, spaceList, models, prompts).ConfigureAwait(false);
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
            ["worktree"] = MobileAlta.Text(item, "worktreeDirectory") is { } worktree ? Path.GetFileName(worktree.TrimEnd('\\', '/')) : null,
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
        var text = string.Join(' ', markdown.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => !line.StartsWith("```", StringComparison.Ordinal)).Select(MobileActivity.PlainLine).Where(static line => line.Length > 0));
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
