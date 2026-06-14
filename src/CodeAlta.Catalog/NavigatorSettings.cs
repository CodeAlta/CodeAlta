using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes persisted machine-local navigator preferences.
/// </summary>
public sealed class NavigatorSettings
{
    /// <summary>
    /// Gets the default number of recent sessions shown per project.
    /// </summary>
    public const int DefaultRecentSessionsPerProject = 10;

    /// <summary>
    /// Gets or sets the project sort mode.
    /// </summary>
    [JsonPropertyName("sort_mode")]
    public NavigatorProjectSortMode SortMode { get; set; } = NavigatorProjectSortMode.Date;

    /// <summary>
    /// Gets or sets the number of recent sessions shown per project.
    /// </summary>
    [JsonPropertyName("recent_sessions_per_project")]
    public int RecentSessionsPerProject { get; set; } = DefaultRecentSessionsPerProject;

    /// <summary>
    /// Gets or sets the preferred UI color scheme name. When empty or unknown, the default theme is used.
    /// </summary>
    [JsonPropertyName("theme_scheme_name")]
    public string? ThemeSchemeName { get; set; }

    /// <summary>
    /// Gets or sets the preferred UI language name (e.g., "en", "zh-CN").
    /// When null or empty, the system culture is used as a fallback.
    /// </summary>
    [JsonPropertyName("language_name")]
    public string? LanguageName { get; set; }

    /// <summary>
    /// Gets or sets whether shell command and file change permission requests
    /// are automatically approved without showing an approval dialog.
    /// </summary>
    [JsonPropertyName("auto_approve")]
    public bool AutoApprove { get; set; }

    /// <summary>
    /// Validates the settings.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a value is out of range.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(SortMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SortMode), SortMode, "Navigator sort mode is invalid.");
        }

        if (RecentSessionsPerProject is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecentSessionsPerProject),
                RecentSessionsPerProject,
                "Navigator recent session count must be between 1 and 50.");
        }

        if (ThemeSchemeName is { Length: > 256 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(ThemeSchemeName),
                ThemeSchemeName,
                "Navigator theme scheme name must be 256 characters or fewer.");
        }

        if (LanguageName is { Length: > 16 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(LanguageName),
                LanguageName,
                "Navigator language name must be 16 characters or fewer.");
        }
    }
}
