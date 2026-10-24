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
        var names = await ProjectNamesAsync(cancellationToken).ConfigureAwait(false);
        var page = ordered.Skip(offset).Take(limit).Select(item => (JsonNode)Describe(item, names)).ToArray();
        return new JsonObject
        {
            ["sessions"] = new JsonArray(page), ["next"] = offset + page.Length, ["hasMore"] = ordered.Count > offset + page.Length,
            ["project"] = projectId, ["machine"] = Environment.MachineName, ["error"] = error,
        };
    }

    /// <summary>One session as the list shows it, or null when the phone does not see it.</summary>
    internal async Task<JsonObject?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var status = await FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return status is null || IsArchived(status) ? null : Describe(status, await ProjectNamesAsync(cancellationToken).ConfigureAwait(false));
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

    // The names of the projects by their id, as the desktop names them.
    private async Task<Dictionary<string, string>> ProjectNamesAsync(CancellationToken cancellationToken)
    {
        var result = await MobileAlta.RunAsync(alta, ["project", "list", "--detailed", "--all"], null, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in result.OfType("alta.project.item"))
            if (MobileAlta.Text(project, "projectId") is { } id) names[id] = MobileAlta.Text(project, "displayName") ?? MobileAlta.Text(project, "name") ?? MobileAlta.Text(project, "slug") ?? id;
        return names;
    }

    private JsonObject Describe(JsonObject item, IReadOnlyDictionary<string, string> projectNames)
    {
        var id = MobileAlta.Text(item, "sessionId")!;
        var projectId = MobileAlta.Text(item, "projectId");
        var at = LastActivity(item);
        return new JsonObject
        {
            ["id"] = id,
            ["title"] = MobileAlta.Text(item, "title"),
            ["projectId"] = projectId,
            ["project"] = projectId is not null && projectNames.TryGetValue(projectId, out var name) ? name : null,
            ["summary"] = MobileAlta.Text(item, "latestSummary"),
            ["lastActivity"] = at == DateTimeOffset.MinValue ? null : at.ToString("O", CultureInfo.InvariantCulture),
            // What the plugin saw of the session since it started; else what alta says of it now.
            ["live"] = live.Describe(id) ?? MobileLive.Quiet(id, projectId, MobileAlta.Flag(item, "isRunning")),
        };
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
