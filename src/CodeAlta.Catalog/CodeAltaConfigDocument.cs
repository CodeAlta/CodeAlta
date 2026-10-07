using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Represents the top-level CodeAlta TOML configuration document.
/// </summary>
public sealed class CodeAltaConfigDocument
{
    /// <summary>
    /// Gets or sets chat-level defaults and preferences.
    /// </summary>
    [JsonPropertyName("chat")]
    public CodeAltaChatSettingsDocument? Chat { get; set; }

    /// <summary>
    /// Gets or sets configured provider definitions keyed by provider key.
    /// </summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, CodeAltaProviderDocument>? Providers { get; set; }

    /// <summary>
    /// Gets or sets skill enablement settings.
    /// </summary>
    [JsonPropertyName("skills")]
    public CodeAltaSkillSettingsDocument? Skills { get; set; }

    /// <summary>
    /// Gets or sets plugin configuration keyed by plugin package id or built-in plugin id.
    /// </summary>
    [JsonPropertyName("plugins")]
    public Dictionary<string, CodeAltaPluginSettingsDocument>? Plugins { get; set; }

    /// <summary>
    /// Gets or sets where the git worktrees CodeAlta creates are placed. Read from the user's file only.
    /// </summary>
    [JsonPropertyName("worktrees")]
    public CodeAltaWorktreeSettingsDocument? Worktrees { get; set; }

    /// <summary>
    /// Gets or sets the choices for work items: the tasks agents propose, and plans. Read from the user's file only.
    /// </summary>
    [JsonPropertyName("work_items")]
    public CodeAltaWorkItemSettingsDocument? WorkItems { get; set; }
}

/// <summary>
/// Represents the choices for work items: the tasks agents propose, and plans.
/// </summary>
public sealed class CodeAltaWorkItemSettingsDocument
{
    /// <summary>
    /// Gets or sets whether agents may propose follow-up tasks; true when absent.
    /// </summary>
    [JsonPropertyName("propose")]
    public bool? Propose { get; set; }

    /// <summary>
    /// Gets or sets whether a session shows the tasks and plans it proposed as cards; true when absent.
    /// </summary>
    [JsonPropertyName("notify")]
    public bool? Notify { get; set; }

    /// <summary>
    /// Gets or sets what becomes of a completed task: <c>delete</c> (the default) or <c>keep</c>.
    /// </summary>
    [JsonPropertyName("completed_tasks")]
    public string? CompletedTasks { get; set; }

    /// <summary>
    /// Gets or sets what becomes of a dismissed task: <c>delete</c> (the default) or <c>keep</c>.
    /// </summary>
    [JsonPropertyName("dismissed_tasks")]
    public string? DismissedTasks { get; set; }

    /// <summary>
    /// Gets or sets what becomes of a plan once it is done: <c>keep</c> (the default) or <c>delete</c>.
    /// </summary>
    [JsonPropertyName("completed_plans")]
    public string? CompletedPlans { get; set; }

    /// <summary>
    /// Gets or sets the way of starting that comes first: <c>worktree</c> (the default), <c>session</c> or <c>here</c>.
    /// </summary>
    [JsonPropertyName("start")]
    public string? Start { get; set; }
}

/// <summary>
/// Represents the choice of where the git worktrees CodeAlta creates are placed.
/// </summary>
public sealed class CodeAltaWorktreeSettingsDocument
{
    /// <summary>
    /// Gets or sets the kind of place: <c>global</c> (under the CodeAlta folder of the user, the default),
    /// <c>project</c> (inside the repository, ignored by git) or <c>custom</c> (under <see cref="Folder"/>).
    /// </summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets the folder of the <c>custom</c> location: an absolute path, in which a leading <c>~</c>
    /// stands for the folder of the user.
    /// </summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; set; }
}

/// <summary>
/// Represents skill enablement configuration settings.
/// </summary>
public sealed class CodeAltaSkillSettingsDocument
{
    /// <summary>
    /// Gets or sets normalized skill names disabled by this configuration scope.
    /// </summary>
    [JsonPropertyName("disabled")]
    public List<string>? Disabled { get; set; }
}

/// <summary>
/// Represents plugin-specific configuration settings.
/// </summary>
public sealed class CodeAltaPluginSettingsDocument
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

/// <summary>
/// Represents chat-specific configuration settings.
/// </summary>
public sealed class CodeAltaChatSettingsDocument
{
    /// <summary>
    /// Gets or sets the default provider key.
    /// </summary>
    [JsonPropertyName("default_provider")]
    public string? DefaultProvider { get; set; }
}
