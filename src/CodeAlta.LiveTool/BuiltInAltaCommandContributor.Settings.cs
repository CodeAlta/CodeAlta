using System.Text.Json;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// The `alta settings` commands: the settings of CodeAlta as one catalog. Listing, reading and opening a setting for the
// user change nothing; a change is the exception to "user settings stay the user's", so it is bounded: only what the
// user asked for, only the settings agents may change, and the window shows each change with Undo.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] SettingsPolicies =
    [
        Read("settings list"),
        Read("settings get"),
        // It changes nothing but what the window shows.
        Read("settings open"),
        Mutating("settings set"),
        Mutating("settings reset"),
    ];

    private static Command CreateSettingsCommand(AltaCommandContext context)
    {
        var group = Group("settings", "Find the settings of CodeAlta, read them, show them to the user in Settings, and change those agents may change when the user asks.");
        group.Add(CreateSettingsListCommand(context));
        group.Add(CreateSettingsGetCommand(context));
        group.Add(CreateSettingsSetCommand(context, reset: false));
        group.Add(CreateSettingsSetCommand(context, reset: true));
        group.Add(CreateSettingsOpenCommand(context));
        AddHelpText(
            group,
            "A setting has an id: the key path of a setting of `config.toml` (`sessions.title` is `[sessions] title`), else named after its page of Settings (`general.keep_awake`, `appearance.theme`); a plugin's are `plugins.<id>.<key>`.",
            "`alta settings open <id>` shows a setting to the user: Settings opens on its page and the setting glows. Prefer it to describing where a setting is.",
            "The settings are the user's. Change one only when the user asks for it. Agents read every setting but change only those listed with `agents: readWrite`: what protects the user (permissions, review before commit, sandbox, tools, Code Mode, the MCP server, deletions) is changed by the user alone, in the window. A list of items (providers, MCP servers, prompts, skills, plugins, spaces, windows) is changed with its own commands, named by `managedBy`.",
            "Examples: `alta settings list title`; `alta settings get sessions.title`; `alta settings open appearance.theme`; `alta settings set sessions.prompt_suggestions off`.");
        return group;
    }

    private static Command CreateSettingsListCommand(AltaCommandContext context)
    {
        string? words = null, page = null;
        var changed = false;
        var detailed = false;
        var command = Leaf("list", "List the settings, or those that match words.");
        command.Add("<words>?", "Words the label, the help, the page or the id of a setting all contain; quote several words.", value => words = value);
        command.Add("page=", "Only the settings of a page of Settings, by its id (`sessions`, `appearance`).", value => page = value);
        command.Add("changed", "Only the settings whose value is not their default.", _ => changed = true);
        command.Add("detailed", "Give each setting whole, with its value.", _ => detailed = true);
        command.Add(async (_, _) => await HandleSettingsListAsync(context, words, page, changed, detailed).ConfigureAwait(false));
        AddHelpText(command,
            "A record names the setting, its label, its page, its kind and what agents may do with it (`agents`: `readWrite` or `read`). `managedBy` names the commands of a list of items.",
            "Examples: `alta settings list`; `alta settings list \"keep awake\"`; `alta settings list --page sessions --detailed`; `alta settings list --changed`.");
        return command;
    }

    private static Command CreateSettingsGetCommand(AltaCommandContext context)
    {
        var ids = new List<string>();
        string? project = null;
        var command = Leaf("get", "Read the value of one or more settings.");
        command.Add("<id>*", "The ids of the settings, as `alta settings list` prints them.", value => { if (value is not null) ids.Add(value); });
        command.Add("project=", "The project whose value is read, for a setting a project can have.", value => project = value);
        command.Add(async (_, _) => await HandleSettingsGetAsync(context, ids, project).ConfigureAwait(false));
        AddHelpText(command,
            "`source` says where the value comes from: `default`, `user`, `project` or `window` (a setting the window keeps, such as the theme).",
            "Examples: `alta settings get sessions.title`; `alta settings get appearance.theme general.language`.");
        return command;
    }

    private static Command CreateSettingsSetCommand(AltaCommandContext context, bool reset)
    {
        string? id = null, text = null, project = null;
        var fromStdin = false;
        var show = false;
        var command = Leaf(reset ? "reset" : "set", reset ? "Give a setting back its default value." : "Change the value of a setting.");
        command.Add("<id>", "The id of the setting, as `alta settings list` prints it.", value => id = value);
        if (!reset)
        {
            command.Add("<value>?", "The value: `true`/`false` (or `on`/`off`), a number, one of the choices, a text, or `default`.", value => text = value);
            command.Add("stdin", "Read the value from standard input, as JSON (a list as an array of texts).", _ => fromStdin = true);
        }

        command.Add("project=", "The project whose value changes, for a setting a project can have.", value => project = value);
        command.Add("show", "Open the setting in Settings after the change, so that the user sees it.", _ => show = true);
        command.Add(async (_, _) => await HandleSettingsSetAsync(context, id, reset ? "default" : text, fromStdin, project, show, reset).ConfigureAwait(false));
        AddHelpText(command,
            "Only when the user asks for it. The window tells the user of the change, with a way to undo it.",
            "Agents change only the settings listed with `agents: readWrite`. A setting that protects the user, a setting a page changes after asking (a download, a consent), a value with a structure of its own and a list of items are refused, with where the user changes them.",
            reset ? "Example: `alta settings reset sessions.title`." : "Examples: `alta settings set sessions.title first_prompt`; `alta settings set appearance.theme light --show`; `alta settings set appearance.code_font --stdin` with `[\"Cascadia Code\"]`.");
        return command;
    }

    private static Command CreateSettingsOpenCommand(AltaCommandContext context)
    {
        string? id = null, page = null;
        var command = Leaf("open", "Show a setting to the user: Settings opens on its page, and the setting glows.");
        command.Add("<id>?", "The id of the setting, as `alta settings list` prints it.", value => id = value);
        command.Add("page=", "A page of Settings to open without a setting: its id (`sessions`), `spaces/<space id>/<section>` or `plugin/<plugin key>/<canvas>`.", value => page = value);
        command.Add((_, _) => ValueTask.FromResult(HandleSettingsOpen(context, id, page)));
        AddHelpText(command,
            "It changes nothing: the user sees where the setting is and changes it there.",
            "Examples: `alta settings open appearance.theme`; `alta settings open --page providers`.");
        return command;
    }

    private static async Task<int> HandleSettingsListAsync(AltaCommandContext context, string? words, string? page, bool changed, bool detailed)
    {
        if (!TryGetSettings(context, out var settings)) return AltaExitCodes.ServiceUnavailable;
        var terms = (NormalizeOptionalText(words) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pageId = NormalizeOptionalText(page);
        var visible = await VisibleSettingsAsync(context, settings).ConfigureAwait(false);
        var matched = visible
            .Where(setting => pageId is null || string.Equals(setting.Page, pageId, StringComparison.OrdinalIgnoreCase))
            .Select(setting => (Setting: setting, Rank: SettingRank(setting, terms)))
            .Where(static entry => entry.Rank >= 0)
            .OrderBy(static entry => entry.Rank)
            .Select(static entry => entry.Setting)
            .ToList();
        var count = 0;
        foreach (var setting in matched)
        {
            AltaSettingRead? read = null;
            if (changed || detailed)
            {
                if (AltaSettingKinds.IsScalar(setting.Kind) || setting.Kind == AltaSettingKinds.Complex)
                    read = await settings.GetAsync(setting.Id, null, context.CancellationToken).ConfigureAwait(false);
                if (changed && read is not { Status: AltaSettingStatus.Ok, Source: not "default" }) continue;
            }

            WriteSettingRecord(context, setting, read, detailed);
            count++;
        }

        WriteSummary(context, "alta.settings.summary", count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async Task<int> HandleSettingsGetAsync(AltaCommandContext context, IReadOnlyList<string> ids, string? projectRef)
    {
        const string CommandPath = "alta settings get";
        if (!TryGetSettings(context, out var settings)) return AltaExitCodes.ServiceUnavailable;
        if (ids.Count == 0) return UsageError(context, "usage.missingSetting", "Name the setting: `alta settings get <id>`; `alta settings list` lists them.", CommandPath);
        var visible = await VisibleSettingsAsync(context, settings).ConfigureAwait(false);
        var (projectId, projectExit) = await SettingsProjectAsync(context, projectRef).ConfigureAwait(false);
        if (projectExit != AltaExitCodes.Success) return projectExit;
        var exitCode = AltaExitCodes.Success;
        foreach (var id in ids)
        {
            if (visible.FirstOrDefault(setting => string.Equals(setting.Id, id.Trim(), StringComparison.Ordinal)) is not { } setting)
            {
                exitCode = Math.Max(exitCode, NotFound(context, "settings.notFound", $"No setting is named {id}. `alta settings list <words>` finds one."));
                continue;
            }

            var read = await settings.GetAsync(setting.Id, projectId, context.CancellationToken).ConfigureAwait(false);
            if (read.Status != AltaSettingStatus.Ok)
            {
                exitCode = Math.Max(exitCode, SettingRefusal(context, setting, read.Status, read.Message));
                continue;
            }

            AltaJsonlWriter.WriteRecord(context.Stdout, ValueRecord(context, setting, read, projectId));
        }

        return exitCode;
    }

    private static async Task<int> HandleSettingsSetAsync(AltaCommandContext context, string? idRef, string? text, bool fromStdin, string? projectRef, bool show, bool reset)
    {
        var commandPath = reset ? "alta settings reset" : "alta settings set";
        if (!TryGetSettings(context, out var settings)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(idRef) is not { } id) return UsageError(context, "usage.missingSetting", $"Name the setting: `{commandPath} <id>`.", commandPath);
        var visible = await VisibleSettingsAsync(context, settings).ConfigureAwait(false);
        if (visible.FirstOrDefault(setting => string.Equals(setting.Id, id, StringComparison.Ordinal)) is not { } setting)
            return NotFound(context, "settings.notFound", $"No setting is named {id}. `alta settings list <words>` finds one.");
        if (setting.Kind == AltaSettingKinds.Collection) return SettingRefusal(context, setting, AltaSettingStatus.Managed, null);
        if (setting.Kind == AltaSettingKinds.Complex) return SettingRefusal(context, setting, AltaSettingStatus.Complex, null);
        // A caller of the live tool is never the window: what protects the user stays the user's.
        if (setting.Agents != AltaSettingAccess.ReadWrite) return SettingRefusal(context, setting, AltaSettingStatus.ReadOnly, null);
        if (!setting.Writable) return SettingRefusal(context, setting, AltaSettingStatus.PageOnly, null);

        JsonElement? value;
        if (fromStdin)
        {
            var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stdin)) return UsageError(context, "usage.missingValue", "--stdin reads the value as JSON from standard input, which is empty.", commandPath);
            try { value = ParseSettingJson(stdin); }
            catch (JsonException) { return UsageError(context, "usage.invalidValue", "The value read from standard input is not JSON.", commandPath); }
        }
        else if (NormalizeOptionalText(text) is not { } given)
        {
            return UsageError(context, "usage.missingValue", $"Give the value: `{commandPath} {setting.Id} <value>`, or `default`.", commandPath);
        }
        else
        {
            value = string.Equals(given, "default", StringComparison.OrdinalIgnoreCase) && setting.Kind != AltaSettingKinds.Text ? null : SettingValueFromText(setting, given);
        }

        if (NormalizeOptionalText(projectRef) is not null && !setting.Scopes.Contains("project"))
            return UsageError(context, "usage.noProjectScope", $"{setting.Id} has no value per project: leave out --project.", commandPath);
        var (projectId, projectExit) = await SettingsProjectAsync(context, projectRef).ConfigureAwait(false);
        if (projectExit != AltaExitCodes.Success) return projectExit;

        var write = await settings.SetAsync(setting.Id, value, projectId, OriginOf(context), context.CancellationToken).ConfigureAwait(false);
        if (write.Status != AltaSettingStatus.Ok)
        {
            return write.Status == AltaSettingStatus.Invalid
                ? UsageError(context, "usage.invalidValue", write.Message ?? $"The value does not fit {setting.Id}.", commandPath)
                : SettingRefusal(context, setting, write.Status, write.Message);
        }

        var opened = show && settings.Open(setting.Page, setting.Id, context.Caller.SourceSessionId);
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.settings.changed",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = setting.Id,
            ["value"] = write.Value,
            ["previous"] = write.Previous,
            ["projectId"] = projectId,
            ["applies"] = AppliesText(setting.Applies),
            ["shown"] = show ? opened : null,
            ["undo"] = write.Previous is { } previous ? $"alta settings set {setting.Id} {UndoText(previous)}" : null,
        });
        return AltaExitCodes.Success;
    }

    private static int HandleSettingsOpen(AltaCommandContext context, string? idRef, string? pageRef)
    {
        const string CommandPath = "alta settings open";
        if (!TryGetSettings(context, out var settings)) return AltaExitCodes.ServiceUnavailable;
        var id = NormalizeOptionalText(idRef);
        var page = NormalizeOptionalText(pageRef);
        if (id is null && page is null) return UsageError(context, "usage.missingSetting", "Name the setting, or the page with --page.", CommandPath);
        AltaSettingDescriptor? setting = null;
        if (id is not null && (setting = settings.List().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))) is null)
            return NotFound(context, "settings.notFound", $"No setting is named {id}. `alta settings list <words>` finds one.");
        if (page is not null && (page.Length > 200 || page.Any(char.IsControl)))
            return UsageError(context, "usage.invalidPage", "The page is the id of a page of Settings, `spaces/<space id>/<section>` or `plugin/<plugin key>/<canvas>`.", CommandPath);
        if (!settings.Open(page, setting?.Id, context.Caller.SourceSessionId))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable, "No CodeAlta window is open to show Settings.");
            return AltaExitCodes.ServiceUnavailable;
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.settings.opened",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = setting?.Id,
            ["page"] = page ?? setting?.Page,
        });
        return AltaExitCodes.Success;
    }

    // The settings a caller reaches: every built-in one, and those of the plugins of its space.
    private static async Task<IReadOnlyList<AltaSettingDescriptor>> VisibleSettingsAsync(AltaCommandContext context, IAltaSettings settings)
    {
        var all = settings.List();
        if (!all.Any(static setting => setting.Plugin is not null)) return all;
        var filter = await CallerSpaceFilterAsync(context).ConfigureAwait(false);
        return filter is null ? all : [.. all.Where(setting => setting.Plugin is not { } plugin || filter.Allows(SpaceResourceKind.Plugin, plugin))];
    }

    // The project of a value per project: one of the space of the caller.
    private static async Task<(string? ProjectId, int ExitCode)> SettingsProjectAsync(AltaCommandContext context, string? projectRef)
    {
        if (NormalizeOptionalText(projectRef) is not { } reference) return (null, AltaExitCodes.Success);
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog)) return (null, AltaExitCodes.ServiceUnavailable);
        var project = await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false);
        if (project is null || project.Archived) return (null, NotFound(context, "project.notFound", $"Project '{reference}' was not found."));
        if (await CallerSpaceFilterAsync(context).ConfigureAwait(false) is { } filter
            && !string.Equals(filter.SpaceId, SpaceDescriptor.DefaultId, StringComparison.Ordinal) && !project.Spaces.Contains(filter.SpaceId, StringComparer.Ordinal))
            return (null, PermissionDenied(context, "space.notOwnSpace", $"Project '{reference}' is not in the space of this session."));
        return (project.Id, AltaExitCodes.Success);
    }

    // Who changes a setting, for the window that shows the change.
    private static AltaSettingOrigin OriginOf(AltaCommandContext context)
    {
        var caller = context.Caller;
        return caller.Kind switch
        {
            "agent" => new(AltaSettingOrigins.Agent, NormalizeOptionalText(caller.SourceSessionId), null),
            "plugin" => new(AltaSettingOrigins.Plugin, NormalizeOptionalText(caller.SourceSessionId), caller.PluginRuntimeKey),
            "mcp" => new(AltaSettingOrigins.Mcp, null, null),
            _ => new(NormalizeOptionalText(caller.SourceSessionId) is { } session ? AltaSettingOrigins.Agent : AltaSettingOrigins.Other, NormalizeOptionalText(caller.SourceSessionId), null),
        };
    }

    // The rank of a setting for words: the label first, then the section and the page, the keywords, the help and the id; -1 when a word is missing.
    private static int SettingRank(AltaSettingDescriptor setting, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        var rank = 0;
        foreach (var term in terms)
        {
            bool In(string? text) => text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
            var found = In(setting.Label) ? 0 : In(setting.Section) || In(setting.PagePath) ? 1 : setting.Keywords.Any(In) ? 2 : In(setting.Help) ? 3 : In(setting.Id) || In(setting.PluginName) ? 4 : -1;
            if (found < 0) return -1;
            rank += found;
        }

        return rank;
    }

    private static void WriteSettingRecord(AltaCommandContext context, AltaSettingDescriptor setting, AltaSettingRead? read, bool detailed)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = detailed ? "alta.settings.setting" : "alta.settings.ref",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = setting.Id,
            ["label"] = setting.Label,
            ["page"] = setting.PagePath,
            ["kind"] = setting.Kind,
            ["agents"] = setting.Agents,
            ["managedBy"] = setting.ManagedBy,
            ["plugin"] = setting.PluginName ?? setting.Plugin,
            ["available"] = setting.Available ? null : false,
        };
        if (detailed)
        {
            record["section"] = setting.Section;
            record["help"] = setting.Help;
            record["pageId"] = setting.Page;
            record["choices"] = ChoiceValues(setting, read);
            record["minimum"] = setting.Minimum;
            record["maximum"] = setting.Maximum;
            record["default"] = setting.Default;
            record["value"] = read is { Status: AltaSettingStatus.Ok } ? read.Value : null;
            record["source"] = read is { Status: AltaSettingStatus.Ok } ? read.Source : null;
            record["writable"] = setting.Writable ? null : false;
            record["applies"] = AppliesText(setting.Applies);
            record["scopes"] = setting.Scopes.Count == 1 && setting.Scopes[0] == "user" ? null : setting.Scopes;
        }

        WriteCanvasRecord(context, record);
    }

    private static Dictionary<string, object?> ValueRecord(AltaCommandContext context, AltaSettingDescriptor setting, AltaSettingRead read, string? projectId)
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.settings.value",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = setting.Id,
            ["value"] = read.Value,
            ["default"] = setting.Default,
            ["source"] = read.Source,
            ["projectId"] = projectId,
            ["kind"] = setting.Kind,
            ["choices"] = ChoiceValues(setting, read),
            ["agents"] = setting.Agents,
        }.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private static string[]? ChoiceValues(AltaSettingDescriptor setting, AltaSettingRead? read)
    {
        var choices = setting.Choices.Count > 0 ? setting.Choices : read?.Choices ?? [];
        return choices.Count > 0 ? [.. choices.Select(static choice => choice.Value)] : null;
    }

    private static string AppliesText(string applies) => applies switch
    {
        "nextPrompt" => "next prompt",
        "nextStart" => "next start",
        _ => "now",
    };

    // How a value is written back on the command line to undo a change.
    private static string UndoText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { } text && !text.Contains(' ') && text.Length > 0 ? text : $"\"{value.GetString()}\"",
        JsonValueKind.Array or JsonValueKind.Object => "--stdin",
        _ => value.GetRawText(),
    };

    // The value a command gives as text: on and off, true and false, a number, a JSON value, or else the text itself.
    internal static JsonElement SettingValueFromText(AltaSettingDescriptor setting, string text)
    {
        var trimmed = text.Trim();
        switch (setting.Kind)
        {
            case AltaSettingKinds.Boolean when trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase):
                return ParseSettingJson("true");
            case AltaSettingKinds.Boolean when trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("no", StringComparison.OrdinalIgnoreCase):
                return ParseSettingJson("false");
            case AltaSettingKinds.Choice or AltaSettingKinds.Text or AltaSettingKinds.Path:
                // A quoted text is JSON; anything else is the text as it is.
                if (trimmed.Length > 1 && trimmed[0] == '"' && trimmed[^1] == '"')
                {
                    try { return ParseSettingJson(trimmed); }
                    catch (JsonException) { }
                }

                return SettingString(setting.Kind == AltaSettingKinds.Choice ? trimmed : text);
            default:
                try { return ParseSettingJson(trimmed); }
                catch (JsonException) { return SettingString(trimmed); }
        }
    }

    private static JsonElement SettingString(string text)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(text.Length + 8);
        using (var writer = new Utf8JsonWriter(buffer)) writer.WriteStringValue(text);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseSettingJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The refusal of a read or a change, with where the user does it instead.
    private static int SettingRefusal(AltaCommandContext context, AltaSettingDescriptor setting, string status, string? message)
    {
        var where = $"Settings › {setting.PagePath}";
        switch (status)
        {
            case AltaSettingStatus.ReadOnly:
                return PermissionDenied(context, "settings.readOnly",
                    message ?? $"Only the user changes {setting.Id}: it protects them or what agents may do. Show it to them with `alta settings open {setting.Id}`; they change it in {where}.");
            case AltaSettingStatus.PageOnly:
                return Unsupported(context, "settings.pageOnly", message ?? $"{setting.Id} is changed in {where}, which asks the user first. Show it with `alta settings open {setting.Id}`.");
            case AltaSettingStatus.Managed:
                return Unsupported(context, "settings.managedByCommand", setting.ManagedBy is { } commands
                    ? $"{setting.Id} is a list of items: use `{commands}` (`{commands} --help`), or show it with `alta settings open {setting.Id}`."
                    : $"{setting.Id} is a list of items, changed in {where}. Show it with `alta settings open {setting.Id}`.");
            case AltaSettingStatus.Complex:
                return Unsupported(context, "settings.complex", message ?? $"{setting.Id} has a structure of its own: it is changed in {where}. Show it with `alta settings open {setting.Id}`.");
            case AltaSettingStatus.NotFound:
                return NotFound(context, "settings.notFound", message ?? $"No setting is named {setting.Id}.");
            case AltaSettingStatus.Unavailable:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "settings.unavailable", AltaExitCodes.ServiceUnavailable, message ?? $"{setting.Id} is not available here.");
                return AltaExitCodes.ServiceUnavailable;
            default:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "settings.failed", AltaExitCodes.Failure, message ?? $"{setting.Id} could not be read or changed.");
                return AltaExitCodes.Failure;
        }
    }

    private static bool TryGetSettings(AltaCommandContext context, out IAltaSettings settings)
    {
        settings = context.Services.Get<IAltaSettings>()!;
        if (settings is not null) return true;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaSettings' is unavailable.");
        return false;
    }

}
