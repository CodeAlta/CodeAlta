using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes persisted machine-local navigator preferences.
/// </summary>
public sealed class NavigatorSettings
{
    /// <summary>
    /// Gets the default number of recent threads shown per project.
    /// </summary>
    public const int DefaultRecentThreadsPerProject = 10;

    /// <summary>
    /// Gets or sets the project sort mode.
    /// </summary>
    [JsonPropertyName("sort_mode")]
    public NavigatorProjectSortMode SortMode { get; set; } = NavigatorProjectSortMode.Date;

    /// <summary>
    /// Gets or sets the number of recent threads shown per project.
    /// </summary>
    [JsonPropertyName("recent_threads_per_project")]
    public int RecentThreadsPerProject { get; set; } = DefaultRecentThreadsPerProject;

    /// <summary>
    /// Gets or sets the preferred UI color scheme name. When empty or unknown, the default theme is used.
    /// </summary>
    [JsonPropertyName("theme_scheme_name")]
    public string? ThemeSchemeName { get; set; }

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

        if (RecentThreadsPerProject is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecentThreadsPerProject),
                RecentThreadsPerProject,
                "Navigator recent thread count must be between 1 and 50.");
        }

        if (ThemeSchemeName is { Length: > 256 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(ThemeSchemeName),
                ThemeSchemeName,
                "Navigator theme scheme name must be 256 characters or fewer.");
        }
    }
}
