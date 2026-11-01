using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Logging;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Settings;

/// <summary>How the host reads and changes one setting it keeps.</summary>
/// <param name="Read">Reads the value now.</param>
/// <param name="Write">Changes it to a value already checked against the setting; returns why it was refused, or null. Null for a setting read only.</param>
internal sealed record DesktopSettingBinding(Func<JsonElement> Read, Func<JsonElement, string?>? Write)
{
    /// <summary>Gets whether the setting is there now; null when it always is.</summary>
    public Func<bool>? Available { get; init; }

    /// <summary>Gets the choices of a choice whose choices change; null when the catalog lists them.</summary>
    public Func<IReadOnlyList<AltaSettingChoice>>? Choices { get; init; }
}

/// <summary>What the bindings of the built-in settings read and change: the stores the pages of Settings already use.</summary>
internal sealed class DesktopSettingSources
{
    /// <summary>Gets the configuration of the user.</summary>
    public required CodeAltaConfigStore Config { get; init; }

    /// <summary>Gets the preferences of the window the host keeps; null where there is no window.</summary>
    public DesktopShell? Shell { get; init; }

    /// <summary>Gets the windows of the application; null where there is none.</summary>
    public Windows.DesktopWindows? Windows { get; init; }

    /// <summary>Gets the settings of the local search; null where search is not there.</summary>
    public Search.DesktopSearchHost? Search { get; init; }

    /// <summary>Gets the tasks and the plans, whose choices it keeps; null where they are not there.</summary>
    public WorkItemService? WorkItems { get; init; }

    /// <summary>Gets what applies log levels once they are saved; null where the log takes them at the next start.</summary>
    public Action? LoggingSaved { get; init; }
}

/// <summary>
/// The bindings of the built-in settings the host keeps, by id. Each reads and writes through the store and the rules
/// the page of the setting uses, so that a change made by <c>alta settings</c> or by the search of Settings is the same
/// as one made on the page.
/// </summary>
internal static class DesktopSettingBindings
{
    /// <summary>Creates the bindings over the stores; a store that is missing leaves its settings unavailable.</summary>
    internal static FrozenDictionary<string, DesktopSettingBinding> Create(DesktopSettingSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var config = sources.Config;
        var shell = sources.Shell;
        var map = new Dictionary<string, DesktopSettingBinding>(StringComparer.Ordinal);
        void Add(string id, Func<JsonElement> read, Func<JsonElement, string?>? write, Func<bool>? available = null)
            => map[id] = new DesktopSettingBinding(read, write) { Available = available };
        void Bool(string id, Func<bool> read, Action<bool>? write, Func<bool>? available = null)
            => Add(id, () => SettingValues.Of(read()), write is null ? null : value => { write(value.GetBoolean()); return null; }, available);

        // The preferences of the window that the host keeps (preferences.json, windows.json).
        bool HasShell() => shell is not null;
        Add("appearance.session_width", () => SettingValues.Of(shell!.SessionWidth),
            value => shell!.SetSessionWidth(value.GetInt32()) ? null : "The width is a percentage from 40 to 100.", HasShell);
        Bool("general.reconnect_remote_control", () => shell!.ReconnectRemoteControl, value => shell!.SetReconnectRemoteControl(value), HasShell);
        Add("general.on_close", () => SettingValues.Of(CloseName(shell!.OnClose)), value => { shell!.SetOnClose(CloseOf(value.GetString())); return null; }, HasShell);
        Bool("general.keep_awake", () => shell!.KeepAwake, value => shell!.SetKeepAwake(value), HasShell);
        Add("general.external_editor", () => SettingValues.Write(writer =>
        {
            var editor = shell!.ExternalEditor;
            writer.WriteStartObject();
            writer.WriteString("id", editor.Id);
            writer.WriteString("program", editor.Program);
            writer.WriteString("arguments", editor.Arguments);
            writer.WriteEndObject();
        }), null, HasShell);
        Add("permissions.default_mode", () => SettingValues.Of(shell!.ReviewPermissions ? "default" : "bypassPermissions"),
            value => { shell!.SetReviewPermissions(value.GetString() == "default"); return null; }, HasShell);
        Add("permissions.created_sessions", () => SettingValues.Of(shell!.InheritPermissions ? "creator" : "bypassPermissions"),
            value => { shell!.SetInheritPermissions(value.GetString() == "creator"); return null; }, HasShell);
        Bool("mcp_host.server", () => shell!.McpServer, null, HasShell);
        Bool("mcp_host.tailnet", () => shell!.McpTailnet, null, HasShell);
        var windows = sources.Windows;
        Bool("windows.restore_at_startup", () => windows!.RestoreAtStartup, value => windows!.SetRestoreAtStartup(value), () => windows is not null);

        // The configuration of the user (config.toml).
        Add("sessions.title", () => SettingValues.Of(CodeAlta.Agent.AgentSessionTitles.NameOf(config.LoadGlobalSessionTitleMode())),
            value => CodeAlta.Agent.AgentSessionTitles.ParseMode(value.GetString()) is { } mode ? Do(() => config.SaveGlobalSessionTitleMode(mode)) : "Unknown title mode.");
        Bool("sessions.prompt_suggestions", config.LoadGlobalPromptSuggestions, config.SaveGlobalPromptSuggestions);
        Bool("review.before_commit", config.LoadGlobalReviewBeforeCommit, config.SaveGlobalReviewBeforeCommit);
        Bool("commits.co_author", config.LoadGlobalCommitCoAuthor, config.SaveGlobalCommitCoAuthor);
        Add("code_mode.mode", () => SettingValues.Of(config.LoadCodeModeMode()), value => Do(() => config.SaveCodeModeMode(value.GetString()!)));
        Add("tools.disabled", () => SettingValues.Of(config.LoadGlobalDisabledTools().Order(StringComparer.Ordinal)),
            value => Do(() => config.SaveGlobalDisabledTools(value.EnumerateArray().Select(static item => item.GetString()!))));
        Add("sandbox.all_sessions", () => SettingValues.Of(config.LoadGlobalSandbox().AllSessions ?? "off"), null);

        Add("worktrees.location", () => SettingValues.Of(WorktreeLocation(config)), value => SaveWorktrees(config, value.GetString(), WorktreeFolder(config)));
        Add("worktrees.folder", () => SettingValues.Of(WorktreeFolder(config) ?? ""), value =>
        {
            var folder = value.GetString();
            return SaveWorktrees(config, string.IsNullOrWhiteSpace(folder) && WorktreeLocation(config) == WorktreeSettings.CustomName ? WorktreeSettings.GlobalName : WorktreeLocation(config), folder);
        });

        Bool("speech.enabled", () => config.LoadGlobalSpeechSettings().Enabled, value => config.SaveGlobalSpeechSettings(config.LoadGlobalSpeechSettings() with { Enabled = value }));
        Add("speech.model", () => SettingValues.Of(config.LoadGlobalSpeechSettings().Model), null);
        Add("speech.send", () => SettingValues.Of(config.LoadGlobalSpeechSettings().Send),
            value => Do(() => config.SaveGlobalSpeechSettings(config.LoadGlobalSpeechSettings() with { Send = value.GetString()! })));

        Bool("storage.clear_scratchpad_on_startup", () => config.LoadGlobalStorageSettings().ClearScratchpadOnStartup,
            value => config.SaveGlobalStorageSettings(config.LoadGlobalStorageSettings() with { ClearScratchpadOnStartup = value }));
        Add("storage.session_retention.enabled", () => SettingValues.Of(config.LoadGlobalStorageSettings().SessionRetention.Enabled),
            value => SaveRetention(config, retention => retention with { Enabled = value.GetBoolean() }));
        Add("storage.session_retention.amount", () => SettingValues.Of(config.LoadGlobalStorageSettings().SessionRetention.Amount),
            value => SaveRetention(config, retention => retention with { Amount = value.GetInt32() }));
        Add("storage.session_retention.unit", () => SettingValues.Of(config.LoadGlobalStorageSettings().SessionRetention.Unit),
            value => SaveRetention(config, retention => retention with { Unit = value.GetString()! }));

        Add("logging.level", () => SettingValues.Of(LoggingSettings.LevelName(config.LoadGlobalLoggingSettings().Level)), value =>
        {
            if (!LoggingSettings.TryParseLevel(value.GetString(), out var level) || !LoggingSettings.Levels.Contains(level)) return "Unknown log level.";
            var refused = Do(() => config.SaveGlobalLoggingSettings(config.LoadGlobalLoggingSettings() with { Level = level }));
            if (refused is null) sources.LoggingSaved?.Invoke();
            return refused;
        });

        Bool("models.fallback.enabled", () => Models(config).Fallback.Enabled, value => SaveFallback(config, fallback => fallback with { Enabled = value }));
        Bool("models.fallback.on_quota", () => Models(config).Fallback.OnQuota, value => SaveFallback(config, fallback => fallback with { OnQuota = value }));
        Bool("models.fallback.on_unavailable", () => Models(config).Fallback.OnUnavailable, value => SaveFallback(config, fallback => fallback with { OnUnavailable = value }));
        Bool("models.fallback.resume", () => Models(config).Fallback.Resume, value => SaveFallback(config, fallback => fallback with { Resume = value }));
        Add("models.fallback.max_resumes", () => SettingValues.Of(Models(config).Fallback.MaxResumes),
            value => { SaveFallback(config, fallback => fallback with { MaxResumes = value.GetInt32() }); return null; });
        Add("models.tiers", () => SettingValues.Write(writer =>
        {
            var tiers = Models(config).Tiers;
            writer.WriteStartObject();
            foreach (var (name, targets) in new[] { ("complex", tiers.Complex), ("standard", tiers.Standard), ("quick", tiers.Quick) })
            {
                writer.WriteStartArray(name);
                foreach (var target in targets) writer.WriteStringValue(target.Format());
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }), null);

        var search = sources.Search;
        bool HasSearch() => search is not null;
        {
            Bool("search.enabled", () => search!.GetSettings().Enabled, value => search!.UpdateSettings(current => current with { Enabled = value }), HasSearch);
            Bool("search.sessions", () => search!.GetSettings().Sessions, value => search!.UpdateSettings(current => current with { Sessions = value }), HasSearch);
            Bool("search.semantic", () => search!.GetSettings().Semantic, null, HasSearch);
            Add("search.model", () => SettingValues.Of(search!.GetSettings().Model), null, HasSearch);
            Add("search.parallelism", () => SettingValues.Of(search!.GetSettings().Parallelism),
                value => { search!.UpdateSettings(current => current with { Parallelism = value.GetInt32() }); return null; }, HasSearch);
            Add("search.excluded", () => SettingValues.Write(writer =>
            {
                var settings = search!.GetSettings();
                writer.WriteStartObject();
                writer.WriteStartArray("projects");
                foreach (var project in settings.ExcludedProjects) writer.WriteStringValue(project);
                writer.WriteEndArray();
                writer.WriteStartArray("spaces");
                foreach (var space in settings.ExcludedSpaces) writer.WriteStringValue(space);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }), null, HasSearch);
        }

        var items = sources.WorkItems;
        bool HasItems() => items is not null;
        {
            Bool("work_items.propose", () => items!.Settings.Propose, value => items!.SaveSettings(items!.Settings with { Propose = value }), HasItems);
            Bool("work_items.notify", () => items!.Settings.Notify, value => items!.SaveSettings(items!.Settings with { Notify = value }), HasItems);
            Add("work_items.start", () => SettingValues.Of(WorkItemSettings.NameOf(items!.Settings.Start)),
                value => WorkItemSettings.ParseStart(value.GetString()) is { } start ? Do(() => items!.SaveSettings(items!.Settings with { Start = start })) : "Unknown start.", HasItems);
            Add("work_items.completed_tasks", () => SettingValues.Of(WorkItemSettings.NameOf(items!.Settings.CompletedTasks)),
                value => WorkItemSettings.ParseClosing(value.GetString()) is { } closing ? Do(() => items!.SaveSettings(items!.Settings with { CompletedTasks = closing })) : "Unknown choice.", HasItems);
            Add("work_items.dismissed_tasks", () => SettingValues.Of(WorkItemSettings.NameOf(items!.Settings.DismissedTasks)),
                value => WorkItemSettings.ParseClosing(value.GetString()) is { } closing ? Do(() => items!.SaveSettings(items!.Settings with { DismissedTasks = closing })) : "Unknown choice.", HasItems);
            Add("work_items.completed_plans", () => SettingValues.Of(WorkItemSettings.NameOf(items!.Settings.CompletedPlans)),
                value => WorkItemSettings.ParseClosing(value.GetString()) is { } closing ? Do(() => items!.SaveSettings(items!.Settings with { CompletedPlans = closing })) : "Unknown choice.", HasItems);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Runs a save; the refusal of the store is the reason a change did not happen.
    private static string? Do(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return exception.Message;
        }
    }

    private static string CloseName(DesktopCloseBehavior behavior) => behavior switch
    {
        DesktopCloseBehavior.KeepRunning => "keep",
        DesktopCloseBehavior.Exit => "exit",
        _ => "ask",
    };

    private static DesktopCloseBehavior CloseOf(string? name) => name switch
    {
        "keep" => DesktopCloseBehavior.KeepRunning,
        "exit" => DesktopCloseBehavior.Exit,
        _ => DesktopCloseBehavior.Ask,
    };

    private static CodeAlta.Catalog.ModelTiers.ModelSettings Models(CodeAltaConfigStore config) => config.LoadGlobalModelSettings();

    private static void SaveFallback(CodeAltaConfigStore config, Func<CodeAlta.Catalog.ModelTiers.ModelFallbackSettings, CodeAlta.Catalog.ModelTiers.ModelFallbackSettings> change)
    {
        var settings = config.LoadGlobalModelSettings();
        config.SaveGlobalModelSettings(settings with { Fallback = change(settings.Fallback) });
    }

    private static string? SaveRetention(CodeAltaConfigStore config, Func<SessionRetentionSettings, SessionRetentionSettings> change)
    {
        var storage = config.LoadGlobalStorageSettings();
        var retention = change(storage.SessionRetention);
        if (retention.GetValidationError() is { } error) return error;
        return Do(() => config.SaveGlobalStorageSettings(storage with { SessionRetention = retention }));
    }

    private static string WorktreeLocation(CodeAltaConfigStore config) => config.LoadGlobal().Worktrees?.Location is { Length: > 0 } location ? location : WorktreeSettings.GlobalName;

    private static string? WorktreeFolder(CodeAltaConfigStore config) => config.LoadGlobal().Worktrees?.Folder;

    // The rules of the Worktrees page: a folder of the user's own needs a folder, and a folder must be one CodeAlta can use.
    private static string? SaveWorktrees(CodeAltaConfigStore config, string? location, string? folder)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (location is not (WorktreeSettings.GlobalName or WorktreeSettings.ProjectName or WorktreeSettings.CustomName)) return "Unknown place for worktrees.";
        if (folder is not null && WorktreeSettings.ResolveFolder(folder) is null) return "The folder for worktrees is not a full path.";
        if (location == WorktreeSettings.CustomName && folder is null) return "A folder you choose needs a folder for worktrees.";
        return Do(() => config.SaveGlobalWorktreeSettings(location, folder));
    }
}