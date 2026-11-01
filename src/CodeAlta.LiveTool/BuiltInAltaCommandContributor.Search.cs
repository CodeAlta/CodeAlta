using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Search;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta search` commands: one index of the conversations, plans, tasks, skills, tools, MCP servers, plugins, the
// commands of the window and the user guide; by words, and by meaning when it is on. A host without the index (the
// terminal application) has no such group.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const int SearchDefaultLimit = 10;
    private const int SearchMaximumLimit = 50;
    private const int SearchShowCharacters = 12_000;

    // One command: `show <ref>`, `load <ref>…`, `status` and `reindex` are forms of its words, as a group of the command line
    // cannot also take a query. Reindexing builds again what the index derives from the journals and files: it changes no
    // data of the user. Loading changes the tools of the calling session, so its form is listed as mutating.
    private static readonly AltaCommandPolicy[] SearchPolicies =
    [
        Read("search", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("search load", requiresRuntime: true),
    ];

    private static Command CreateSearchCommand(AltaCommandContext context)
    {
        var words = new List<string>();
        string? kinds = null, project = null, session = null, since = null, until = null, role = null, mode = null, limit = null, around = null;
        bool recent = false, includeCurrent = false, full = false;
        var command = Leaf("search", "Search the conversations, plans, tasks, skills, tools, MCP servers, plugins and the user guide, by words and by meaning.");
        command.Add("<query>*", "What to look for: words, an identifier, an error, or a question in your own words. Quote a phrase. Or `show <ref>`, `load <ref>…`, `status`, `reindex`.", value =>
        {
            if (value is not null) words.Add(value);
        });
        command.Add("kind=", "Kinds, separated by commas: message, session, note, plan, task, skill, tool, mcp, plugin, command, doc, script, setting. Every kind when left out.", value => kinds = value);
        command.Add("project=", "A project: its id, its name, its folder, or `.` for the project of this session. Every project of the space when left out.", value => project = value);
        command.Add("session=", "One session only.", value => session = value);
        command.Add("since=", "The earliest time: `30d`, `12h`, `2w`, or a date such as `2026-10-01`.", value => since = value);
        command.Add("until=", "The latest time, in the same forms.", value => until = value);
        command.Add("role=", "For messages: `user`, `assistant` or `agent` (a prompt from another session).", value => role = value);
        command.Add("mode=", "`auto` (the default): by words, and by meaning when it is on; `keyword`; `meaning`.", value => mode = value);
        command.Add("limit=", $"The most hits: {SearchDefaultLimit.ToString(CultureInfo.InvariantCulture)} when left out, {SearchMaximumLimit.ToString(CultureInfo.InvariantCulture)} at most.", value => limit = value);
        command.Add("recent", "Prefer the newest among hits as good.", _ => recent = true);
        command.Add("include-current", "Include the calling session, which is left out by default.", _ => includeCurrent = true);
        command.Add("context=", "With `show` of a message: the turns before and after it, 0 to 10.", value => around = value);
        command.Add("full", "With `show` of a message: every message of its turns, the steering and the messages between tool calls too.", _ => full = true);
        command.Add(async (_, _) =>
        {
            if (words is ["status"]) return await HandleSearchStatusAsync(context).ConfigureAwait(false);
            if (words is ["reindex"]) return await HandleSearchReindexAsync(context, kinds).ConfigureAwait(false);
            if (words is ["show", var reference] && reference.Contains(':', StringComparison.Ordinal)) return await HandleSearchShowAsync(context, reference, around, full).ConfigureAwait(false);
            if (words.Count > 1 && words[0] == "load" && words.Skip(1).All(static word => word.Contains(':', StringComparison.Ordinal)))
                return await HandleSearchLoadAsync(context, words.Skip(1).Distinct(StringComparer.Ordinal).ToArray()).ConfigureAwait(false);
            return await HandleSearchAsync(context, string.Join(' ', words), kinds, project, session, since, until, role, mode, limit, recent, includeCurrent).ConfigureAwait(false);
        });
        AddHelpText(
            command,
            "A hit names what it found (`ref`), shows a snippet with the matched words in **bold**, says whether it was found by its `words`, its `meaning` or `both`, and gives the command that uses it (`next`).",
            "A message is the prompt or the final answer of a turn: `alta search show <ref>` reads the whole turn; `--context 1` adds the turns around it; `--full` adds the steering and the messages between tool calls.",
            "To find a tool or a skill by what it does: `alta search \"<what you need>\" --kind tool,mcp,skill`; load an MCP tool found into your session with `alta search load <ref>` (it is registered in the running turn: call it in your next step), or call it once with `alta mcp tool call`.",
            "`alta search status` says what the index holds and how far indexing is; `alta search reindex [--kind <kinds>]` indexes again in the background.",
            "`alta search load <ref>…` loads the MCP tools of hits into the calling session, which keeps them when it is resumed; it is refused while the user reviews the commands of the session.",
            "Examples: `alta search \"why did we change the journal fingerprint\" --kind message --project .`; `alta search \"post a message on slack\" --kind tool,mcp,skill`; `alta search load mcp:slack/post_message`; `alta search worktree --kind doc`; `alta search show msg:<session>/<content> --context 1`.");
        return command;
    }

    private static async Task<int> HandleSearchAsync(AltaCommandContext context, string query, string? kindsText, string? projectText, string? sessionText, string? sinceText,
        string? untilText, string? roleText, string? modeText, string? limitText, bool recent, bool includeCurrent)
    {
        const string CommandPath = "alta search";
        if (!TryGetSearch(context, out var search)) return AltaExitCodes.ServiceUnavailable;
        if (string.IsNullOrWhiteSpace(query)) return UsageError(context, "usage.missingQuery", "Give what to look for: `alta search \"<words or a question>\"`.", CommandPath);
        if (!TryParseKinds(kindsText, out var kinds, out var unknown)) return UsageError(context, "usage.invalidKind", $"Unknown kind '{unknown}': use message, session, note, plan, task, skill, tool, mcp, plugin, command, doc, script or setting.", CommandPath);
        var limit = SearchDefaultLimit;
        if (NormalizeOptionalText(limitText) is { } limitValue && (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > SearchMaximumLimit))
            return UsageError(context, "usage.invalidLimit", $"The limit must be from 1 to {SearchMaximumLimit.ToString(CultureInfo.InvariantCulture)}.", CommandPath);
        if (!TryParseTime(sinceText, out var since)) return UsageError(context, "usage.invalidSince", "--since is `30d`, `12h`, `2w` or a date such as 2026-10-01.", CommandPath);
        if (!TryParseTime(untilText, out var until)) return UsageError(context, "usage.invalidUntil", "--until is `30d`, `12h`, `2w` or a date such as 2026-10-01.", CommandPath);
        var role = SearchRole.None;
        if (NormalizeOptionalText(roleText) is { } roleValue && !SearchNames.TryParseRole(roleValue, out role)) return UsageError(context, "usage.invalidRole", "--role is user, assistant or agent.", CommandPath);
        var mode = (NormalizeOptionalText(modeText) ?? "auto").ToLowerInvariant() switch
        {
            "auto" => (SearchMode?)SearchMode.Auto,
            "keyword" or "keywords" or "words" => SearchMode.Keyword,
            "meaning" or "semantic" => SearchMode.Meaning,
            _ => null,
        };
        if (mode is null) return UsageError(context, "usage.invalidMode", "--mode is auto, keyword or meaning.", CommandPath);

        var scope = await SearchScopeAsync(context, kinds, includeCurrent).ConfigureAwait(false);
        if (NormalizeOptionalText(projectText) is { } projectValue)
        {
            var projectId = await ResolveSearchProjectAsync(context, projectValue).ConfigureAwait(false);
            if (projectId is null) return NotFound(context, "project.notFound", $"No project is '{projectValue}'. List them with `alta project list`.");
            scope = scope with { ProjectId = projectId };
        }

        if (NormalizeOptionalText(sessionText) is { } sessionValue) scope = scope with { SessionId = sessionValue, ExceptSessionId = null };
        scope = scope with { Since = since, Until = until, Role = role };
        var response = await search.SearchAsync(new SearchRequest(query, scope)
        {
            Limit = limit, Mode = mode.Value, Recent = recent, Origin = NormalizeOptionalText(context.Caller.SourceSessionId) is null ? "client" : "agent",
            OriginSessionId = NormalizeOptionalText(context.Caller.SourceSessionId),
        }, context.CancellationToken).ConfigureAwait(false);

        var rank = 0;
        var sandbox = await SandboxSearchFilterAsync(context).ConfigureAwait(false);
        foreach (var hit in response.Hits)
        {
            if (sandbox is not null && !await sandbox(hit).ConfigureAwait(false)) continue;
            WriteCanvasRecord(context, HitRecord(context, hit, ++rank));
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.search.summary",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["query"] = query,
            ["count"] = response.Hits.Count,
            ["truncated"] = response.Truncated,
            ["mode"] = response.Mode == SearchMode.Auto ? "words+meaning" : "words",
            ["meaning"] = MeaningName(response.Meaning),
            ["coverage"] = response.Meaning is SearchMeaningState.Indexing ? Math.Round(response.Coverage, 3) : null,
            ["kinds"] = kinds is null ? null : kinds.Select(SearchNames.Of).ToArray(),
            ["projectId"] = scope.ProjectId,
            ["sessionId"] = scope.SessionId,
            ["spaceId"] = scope.SpaceId,
            ["hint"] = response.Hits.Count == 0
                ? (response.Meaning == SearchMeaningState.Off ? "Nothing matched these words. Try other words, an identifier, or fewer filters; search by meaning is off." : "Nothing matched. Try other words or fewer filters.")
                : null,
        });
        return AltaExitCodes.Success;
    }

    internal static string MeaningName(SearchMeaningState state) => state switch
    {
        SearchMeaningState.Ready => "ready",
        SearchMeaningState.Indexing => "indexing",
        SearchMeaningState.Warming => "warming",
        SearchMeaningState.Failed => "failed",
        SearchMeaningState.NotInstalled => "not_installed",
        _ => "off",
    };

    private static Dictionary<string, object?> HitRecord(AltaCommandContext context, SearchHit hit, int rank)
    {
        var extra = ParseExtra(hit.Extra);
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.search.hit",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["rank"] = rank,
            ["kind"] = SearchNames.Of(hit.Kind),
            ["ref"] = hit.Ref,
            ["title"] = hit.Title,
            ["snippet"] = Bold(hit.Snippet, hit.Marks),
            ["at"] = hit.At.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["match"] = SearchNames.Of(hit.Match),
            ["role"] = SearchNames.Of(hit.Role),
            ["sessionId"] = hit.SessionId,
            ["runId"] = hit.RunId,
            ["projectId"] = hit.ProjectId,
        };
        foreach (var name in new[] { "state", "server", "tool", "name", "page", "anchor", "path", "id", "shortcut", "plugin" })
        {
            if (extra.TryGetValue(name, out var value)) record[name] = value;
        }

        record["next"] = NextCommand(hit, extra, CanLoadTools(context));
        return record;
    }

    private static Dictionary<string, string> ParseExtra(string? extra)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(extra)) return values;
        try
        {
            using var document = JsonDocument.Parse(extra);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) values[property.Name] = property.Value.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }

    // The command that uses a hit: what an agent does next with it.
    private static string? NextCommand(SearchHit hit, IReadOnlyDictionary<string, string> extra, bool canLoad)
    {
        string? Value(string name) => extra.TryGetValue(name, out var value) ? value : null;
        return hit.Kind switch
        {
            SearchKind.Mcp when canLoad && Value("tool") is { } tool && Value("state") != "disabled" => $"alta search load {hit.Ref} (then call it as {Value("alias") ?? "mcp__<server>__" + tool}; or once: alta mcp tool call --server {Value("server")} --tool {tool} --arguments '{{…}}')",
            SearchKind.Message or SearchKind.Note or SearchKind.Session => $"alta search show {hit.Ref} --context 1",
            SearchKind.Plan when Value("id") is { } id => $"alta plan show {id}",
            SearchKind.Task when Value("id") is { } id => $"alta task show {id}",
            SearchKind.Skill when Value("name") is { } name => $"alta skill activate {name}",
            SearchKind.Mcp when Value("tool") is { } tool && Value("server") is { } server => $"alta mcp tool call --server {server} --tool {tool} --arguments '{{…}}' (schema: alta mcp tool describe --server {server} --tool {tool})",
            SearchKind.Mcp when Value("server") is { } server => $"alta mcp activate {server}",
            SearchKind.Tool when Value("path") is { } path => $"alta {path} --help",
            SearchKind.Tool when Value("state") == "loaded" => "already in your tools",
            SearchKind.Tool when Value("activate") is { } activate => activate,
            SearchKind.Tool when Value("prompt") is { } prompt => $"alta session set_agent --prompt-id {prompt}",
            SearchKind.Doc when Value("api") is { } type => $"alta plugin api {type}",
            SearchKind.Doc when Value("page") is { } page => Value("anchor") is { } anchor ? $"alta documentation read {page} (section #{anchor})" : $"alta documentation read {page}",
            SearchKind.Plugin => "alta plugin list",
            SearchKind.Setting when Value("id") is { } setting => $"alta settings get {setting} (show it to the user: alta settings open {setting})",
            SearchKind.Script when Value("name") is { } name => $"alta script show {name} --declarations (run it with workflow(\"{name}\", args) in exec, or alta script run {name} --arg <key>=<value>)",
            _ => $"alta search show {hit.Ref}",
        };
    }

    private static string Bold(string text, IReadOnlyList<SearchMark> marks)
    {
        if (marks.Count == 0) return text;
        var builder = new StringBuilder(text.Length + marks.Count * 4);
        var position = 0;
        foreach (var mark in marks.OrderBy(static mark => mark.Start))
        {
            if (mark.Start < position || mark.Start + mark.Length > text.Length) continue;
            builder.Append(text, position, mark.Start - position).Append("**").Append(text, mark.Start, mark.Length).Append("**");
            position = mark.Start + mark.Length;
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }

    private static async Task<SearchScope> SearchScopeAsync(AltaCommandContext context, IReadOnlySet<SearchKind>? kinds, bool includeCurrent)
    {
        var scope = new SearchScope { Kinds = kinds };
        var caller = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (caller is null) return scope;
        if (!includeCurrent) scope = scope with { ExceptSessionId = caller };
        if (context.Services.Get<SpaceCatalog>() is { } spaces && await CallerSpaceAsync(context, spaces).ConfigureAwait(false) is { } space)
        {
            scope = scope with { SpaceId = space.Id };
            if (!string.Equals(space.Id, SpaceDescriptor.DefaultId, StringComparison.Ordinal) && context.Services.Get<ProjectCatalog>() is { } projects)
            {
                var all = await projects.LoadAsync(context.CancellationToken).ConfigureAwait(false);
                scope = scope with { Projects = all.Where(project => project.Spaces.Contains(space.Id, StringComparer.Ordinal)).Select(static project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) };
            }
        }

        if (await CallerSpaceFilterAsync(context).ConfigureAwait(false) is { } filter)
        {
            scope = scope with
            {
                CatalogFilter = hit =>
                {
                    var extra = ParseExtra(hit.Extra);
                    return hit.Kind switch
                    {
                        SearchKind.Skill when extra.TryGetValue("name", out var name) => filter.Allows(SpaceResourceKind.Skill, name),
                        SearchKind.Mcp when extra.TryGetValue("server", out var server) => filter.Allows(SpaceResourceKind.McpServer, server),
                        SearchKind.Plugin when extra.TryGetValue("id", out var id) => filter.Allows(SpaceResourceKind.Plugin, id),
                        // A setting of a plugin is in the spaces of its plugin.
                        SearchKind.Setting when extra.TryGetValue("plugin", out var plugin) => filter.Allows(SpaceResourceKind.Plugin, plugin),
                        // A script of a project is in the spaces of its sessions; yours and the samples as the user assigned them.
                        SearchKind.Script when extra.TryGetValue("name", out var name) && !(extra.TryGetValue("scope", out var where) && where == "project")
                            => filter.Allows(SpaceResourceKind.Script, name),
                        _ => true,
                    };
                },
            };
        }

        return scope;
    }

    private static async Task<string?> ResolveSearchProjectAsync(AltaCommandContext context, string value)
    {
        if (value == ".") return NormalizeOptionalText(context.Caller.SourceProjectId);
        if (context.Services.Get<ProjectCatalog>() is not { } catalog) return value;
        var projects = await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var full = Path.IsPathRooted(value) ? Path.GetFullPath(value) : null;
        return projects.FirstOrDefault(project => string.Equals(project.Id, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.Slug, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.Name, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.DisplayName, value, StringComparison.OrdinalIgnoreCase)
            || (full is not null && !string.IsNullOrEmpty(project.ProjectPath) && string.Equals(Path.GetFullPath(project.ProjectPath), full, StringComparison.OrdinalIgnoreCase)))?.Id;
    }

    private static bool TryParseKinds(string? text, out IReadOnlySet<SearchKind>? kinds, out string? unknown)
    {
        kinds = null;
        unknown = null;
        if (NormalizeOptionalText(text) is not { } value || value is "all" or "*") return true;
        var set = new HashSet<SearchKind>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SearchNames.TryParseKind(part, out var kind))
            {
                unknown = part;
                return false;
            }

            set.Add(kind);
        }

        kinds = set;
        return true;
    }

    private static bool TryParseTime(string? text, out DateTimeOffset? time)
    {
        time = null;
        if (NormalizeOptionalText(text) is not { } value) return true;
        if (value.Length >= 2 && char.IsLetter(value[^1]) && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            var span = char.ToLowerInvariant(value[^1]) switch
            {
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                'w' => TimeSpan.FromDays(amount * 7),
                'm' => TimeSpan.FromDays(amount * 30),
                'y' => TimeSpan.FromDays(amount * 365),
                _ => (TimeSpan?)null,
            };
            if (span is null) return false;
            time = DateTimeOffset.UtcNow - span.Value;
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }

    private static async Task<int> HandleSearchShowAsync(AltaCommandContext context, string? reference, string? aroundText, bool full)
    {
        const string CommandPath = "alta search show";
        if (!TryGetSearch(context, out var search)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(reference) is not { } value) return UsageError(context, "usage.missingRef", "Give the `ref` of a hit.", CommandPath);
        var around = 0;
        if (NormalizeOptionalText(aroundText) is { } aroundValue && (!int.TryParse(aroundValue, NumberStyles.None, CultureInfo.InvariantCulture, out around) || around > 10))
            return UsageError(context, "usage.invalidContext", "--context is a number of turns from 0 to 10.", CommandPath);
        var shown = await search.ShowAsync(value, around, full, context.CancellationToken).ConfigureAwait(false);
        if (shown is null) return NotFound(context, "search.notFound", $"The index holds nothing named '{value}'. Search again: refs change when a journal is rewritten.");
        // An agent only reads what it may reach: another space's sessions and skills are refused like a miss.
        var scope = await SearchScopeAsync(context, null, includeCurrent: true).ConfigureAwait(false);
        if (!await IsVisibleAsync(scope, shown.Hit, await SandboxSearchFilterAsync(context).ConfigureAwait(false)).ConfigureAwait(false)) return NotFound(context, "search.notFound", $"The index holds nothing named '{value}' in your space.");
        var hit = shown.Hit;
        var record = HitRecord(context, hit, 0);
        record["type"] = "alta.search.item";
        record.Remove("rank");
        record.Remove("snippet");
        if (shown.Turns.Count == 0)
        {
            var text = shown.Text.Length > SearchShowCharacters ? shown.Text[..SearchShowCharacters] : shown.Text;
            record["text"] = text;
            record["truncated"] = shown.Text.Length > SearchShowCharacters;
        }
        else
        {
            record["turns"] = shown.Turns.Select((turn, index) => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runId"] = turn.RunId,
                ["at"] = turn.At.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["hit"] = index == shown.HitTurn ? true : null,
                ["messages"] = turn.Messages.Select(static message => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = SearchNames.Of(message.Role),
                    ["contentId"] = message.ContentId,
                    ["text"] = message.Text.Length > SearchShowCharacters ? message.Text[..SearchShowCharacters] + "…" : message.Text,
                }).ToArray(),
            }.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)).ToArray();
        }

        WriteCanvasRecord(context, record);
        return AltaExitCodes.Success;
    }

    // An agent's run takes tools; any other caller has no session tools to load them into.
    private static bool CanLoadTools(AltaCommandContext context) => context.Caller is { Kind: "agent", RunTools: not null };

    // `alta search load <ref>…`: the MCP tools of the hits are activated one by one for the calling session by the MCP
    // plugin (`alta mcp activate <server>/<tool>`), which registers them in the running turn and keeps them in the session.
    private static async Task<int> HandleSearchLoadAsync(AltaCommandContext context, IReadOnlyList<string> references)
    {
        if (!TryGetSearch(context, out var search)) return AltaExitCodes.ServiceUnavailable;
        if (!CanLoadTools(context))
            return Unsupported(context, "search.loadNeedsAgent", "Only an agent loads tools, into its own session. Call an MCP tool found with `alta mcp tool call --server <id> --tool <name>`.");
        if (ReviewsCommands(context))
            return PermissionDenied(context, "search.loadDenied",
                "The user reviews the commands of this session: it does not load tools. Call an MCP tool found once with `alta mcp tool call --server <id> --tool <name> --arguments '{…}'`.");

        var scope = await SearchScopeAsync(context, null, includeCurrent: true).ConfigureAwait(false);
        var items = new List<Dictionary<string, object?>>();
        var mcpIds = new List<(string Id, Dictionary<string, object?> Item)>();
        foreach (var reference in references)
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ref"] = reference };
            items.Add(item);
            var shown = await search.ShowAsync(reference, 0, false, context.CancellationToken).ConfigureAwait(false);
            if (shown is null || !IsVisible(scope, shown.Hit))
            {
                item["status"] = "not_found";
                item["hint"] = "Search again: `alta search \"<what it does>\" --kind tool,mcp`.";
                continue;
            }

            var extra = ParseExtra(shown.Hit.Extra);
            string? Value(string name) => extra.TryGetValue(name, out var value) ? value : null;
            item["kind"] = SearchNames.Of(shown.Hit.Kind);
            switch (shown.Hit.Kind)
            {
                case SearchKind.Mcp when Value("server") is { } server && Value("tool") is { } tool:
                    if (Value("state") == "disabled")
                    {
                        item["status"] = "disabled";
                        item["hint"] = "The MCP policy turns this tool off.";
                        break;
                    }

                    item["server"] = server;
                    item["tool"] = tool;
                    mcpIds.Add((server + "/" + tool, item));
                    break;
                case SearchKind.Mcp when Value("server") is { } server:
                    item["server"] = server;
                    mcpIds.Add((server, item));
                    break;
                case SearchKind.Tool when Value("activate") is { } activate:
                    item["status"] = "activate";
                    item["next"] = activate;
                    item["hint"] = $"These tools come together: run `{activate}`.";
                    break;
                case SearchKind.Tool when Value("state") == "loaded":
                    item["status"] = "already_loaded";
                    item["hint"] = "It is already in your tools: call it.";
                    break;
                case SearchKind.Tool when Value("path") is { } path:
                    item["status"] = "not_a_tool";
                    item["next"] = $"alta {path} --help";
                    item["hint"] = "An alta command is run through the alta tool, not loaded.";
                    break;
                default:
                    item["status"] = "not_a_tool";
                    item["next"] = NextCommand(shown.Hit, extra, canLoad: false);
                    break;
            }
        }

        string? toolsAvailable = null;
        if (mcpIds.Count > 0)
        {
            var registry = context.Services.Get<AltaCommandRegistry>();
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var exitCode = registry is null
                ? AltaExitCodes.ServiceUnavailable
                : (await registry.InvokeAsync(["mcp", "activate", .. mcpIds.Select(static id => id.Id)], context with { Stdout = stdout, Stderr = stderr }).ConfigureAwait(false)).ExitCode;
            JsonElement? activation = null;
            string? error = null;
            // The records of the activation are read; its diagnostics (a server that did not start) are passed on.
            foreach (var line in stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                var type = record.TryGetProperty("type", out var value) ? value.GetString() : null;
                if (type == "alta.mcp.activate") activation = record.Clone();
                else if (type == "alta.mcp.error") error ??= record.TryGetProperty("message", out var message) ? message.GetString() : null;
                else context.Stdout.WriteLine(line);
            }

            error ??= FirstErrorMessage(stderr.ToString());
            foreach (var (_, item) in mcpIds)
            {
                if (exitCode == AltaExitCodes.Success && activation is not null)
                {
                    item["status"] = "loaded";
                    if (item.TryGetValue("tool", out var tool) && tool is string name && item["server"] is string server)
                        item["alias"] = activation.Value.TryGetProperty("tools", out var aliases) && aliases.ValueKind == JsonValueKind.Array
                            ? aliases.EnumerateArray().Select(static alias => alias.GetString()).FirstOrDefault(alias => alias is not null && alias.EndsWith("__" + name, StringComparison.Ordinal))
                            : null;
                }
                else
                {
                    item["status"] = registry is null ? "unavailable" : "failed";
                    item["hint"] = registry is null ? "MCP tools cannot be loaded in this application." : error;
                }
            }

            if (activation is { } done && done.TryGetProperty("toolsAvailable", out var available)) toolsAvailable = available.GetString();
        }

        var loaded = items.Count(static item => item["status"] is "loaded");
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.search.load",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["items"] = items,
            ["loadedCount"] = loaded,
            ["toolsAvailable"] = loaded > 0 ? toolsAvailable ?? "next_run" : null,
            ["note"] = loaded == 0 ? null
                : toolsAvailable == "now"
                    ? "The tools are registered for this turn: call them in your next step. Do not end the turn. The session keeps them, also when it is resumed."
                    : "The tools come with the next run of this session, which keeps them.",
        });
        return loaded > 0 || items.Count(static item => item["status"] is "already_loaded" or "activate") > 0 ? AltaExitCodes.Success
            : items.All(static item => item["status"] is "not_found") ? AltaExitCodes.NotFound : AltaExitCodes.Failure;
    }

    // The message of the first JSONL error of a nested command.
    private static string? FirstErrorMessage(string stderr)
    {
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) return message.GetString();
            }
            catch (JsonException)
            {
                return line;
            }
        }

        return null;
    }

    // A sandboxed session finds the conversations of itself and of the sessions it created only, and what belongs to its
    // own project or to none (plans and tasks of the others, their files): the others hold what its sandbox keeps
    // from it. Null for a caller that is not sandboxed.
    private static async Task<Func<SearchHit, Task<bool>>?> SandboxSearchFilterAsync(AltaCommandContext context)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId
            || await AltaSandboxGate.OfAsync(context, callerId).ConfigureAwait(false) is null)
            return null;
        var ownProject = NormalizeOptionalText(context.Caller.SourceProjectId);
        return async hit => (NormalizeOptionalText(hit.ProjectId) is not { } project || string.Equals(project, ownProject, StringComparison.OrdinalIgnoreCase))
                            && (NormalizeOptionalText(hit.SessionId) is not { } sessionId || await DescendsFromAsync(context, sessionId, callerId).ConfigureAwait(false));
    }

    private static async Task<bool> IsVisibleAsync(SearchScope scope, SearchHit hit, Func<SearchHit, Task<bool>>? sandbox)
        => IsVisible(scope, hit) && (sandbox is null || await sandbox(hit).ConfigureAwait(false));

    private static bool IsVisible(SearchScope scope, SearchHit hit)
    {
        if (scope.SpaceId is { } space && hit.SpaceId is { } hitSpace && !string.Equals(space, hitSpace, StringComparison.OrdinalIgnoreCase)) return false;
        if (scope.Projects is { } projects && hit.ProjectId is { } project && !projects.Contains(project)) return false;
        return !SearchNames.CatalogKinds.Contains(hit.Kind) || scope.CatalogFilter is not { } filter || filter(hit);
    }

    private static async Task<int> HandleSearchStatusAsync(AltaCommandContext context)
    {
        if (!TryGetSearch(context, out var search)) return AltaExitCodes.ServiceUnavailable;
        var status = await search.GetStatusAsync(context.CancellationToken).ConfigureAwait(false);
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.search.status",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["enabled"] = status.Enabled,
            ["ready"] = status.Indexer.Ready,
            ["indexing"] = status.Indexer.Busy,
            ["journalsDone"] = status.Indexer.JournalsTotal > 0 ? status.Indexer.JournalsDone : null,
            ["journalsTotal"] = status.Indexer.JournalsTotal > 0 ? status.Indexer.JournalsTotal : null,
            ["items"] = status.Counts.Items.OrderBy(static pair => pair.Key).ToDictionary(static pair => SearchNames.Of(pair.Key), static pair => pair.Value, StringComparer.Ordinal),
            ["sessions"] = status.Counts.Sessions,
            ["chunks"] = status.Counts.Chunks,
            ["bytes"] = status.Counts.Bytes,
            ["meaning"] = MeaningName(status.Meaning),
            ["coverage"] = status.Meaning is SearchMeaningState.Off ? null : Math.Round(status.Coverage, 3),
            ["lastPass"] = status.Indexer.LastPass?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["lastError"] = status.Indexer.LastError,
        });
        return AltaExitCodes.Success;
    }

    private static async Task<int> HandleSearchReindexAsync(AltaCommandContext context, string? kindsText)
    {
        const string CommandPath = "alta search reindex";
        // The index is the one of every session, and a reindex of the MCP servers starts them all, outside the sandbox.
        if (await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is { } sandbox)
        {
            return PermissionDenied(context, "sandbox.refused", AltaSandboxGate.Refusal(sandbox, "rebuild the index of search"));
        }

        if (!TryGetSearch(context, out _)) return AltaExitCodes.ServiceUnavailable;
        if (context.Services.Get<SearchIndexer>() is not { } indexer || context.Services.Get<SearchIndexStore>() is not { } store)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable, "The indexer of search is not running.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!TryParseKinds(kindsText, out var kinds, out var unknown)) return UsageError(context, "usage.invalidKind", $"Unknown kind '{unknown}'.", CommandPath);
        if (kinds is null)
        {
            await store.ClearAsync(context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The sources of these kinds are forgotten, so that the pass reads them whole again.
            var sources = await store.ListSourcesAsync("", context.CancellationToken).ConfigureAwait(false);
            var keys = sources.Select(static source => source.Key).Where(key => kinds.Any(kind => SourceMayHold(key, kind))).ToList();
            await store.DeleteSourcesAsync(keys, context.CancellationToken).ConfigureAwait(false);
        }

        // The tools of MCP servers are known from their lists: asked for, every enabled server is listed once, which
        // starts it. Only `--kind mcp` does it, never a reindex of everything.
        int? listedServers = null;
        if (kinds is not null && kinds.Contains(SearchKind.Mcp) && context.Services.Get<AltaCommandDispatcher>() is { } dispatcher)
        {
            var listing = await dispatcher.InvokeAsync(["mcp", "tool", "search", "--refresh"], caller: context.Caller, cwd: context.Cwd,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);
            if (listing.ExitCode == AltaExitCodes.Success) listedServers = listing.Stdout.Split('\n').Count(static line => line.Contains("\"alta.mcp.tool.search\"", StringComparison.Ordinal));
        }

        indexer.SignalPass();
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.search.reindex",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["kinds"] = kinds?.Select(SearchNames.Of).ToArray(),
            ["mcpListed"] = listedServers is not null,
            ["started"] = true,
            ["hint"] = "Indexing runs in the background: `alta search status` says how far it is.",
        });
        return AltaExitCodes.Success;
    }

    private static bool SourceMayHold(string key, SearchKind kind) => kind switch
    {
        SearchKind.Message or SearchKind.Session or SearchKind.Note => key.StartsWith("journal:", StringComparison.Ordinal),
        SearchKind.Plan or SearchKind.Task => key.StartsWith("plans:", StringComparison.Ordinal),
        SearchKind.Skill => key == "skills",
        SearchKind.Tool => key == "tools",
        SearchKind.Mcp => key == "mcp" || key.StartsWith("mcp:", StringComparison.Ordinal),
        SearchKind.Plugin => key == "plugins",
        SearchKind.Command => key == "commands",
        SearchKind.Doc => key == "docs",
        SearchKind.Script => key == "scripts",
        SearchKind.Setting => key == "settings",
        _ => false,
    };

    private static bool TryGetSearch(AltaCommandContext context, out SearchService search)
    {
        search = context.Services.Get<SearchService>()!;
        if (search is not null) return true;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable, "Search is not available in this application.");
        return false;
    }
}
