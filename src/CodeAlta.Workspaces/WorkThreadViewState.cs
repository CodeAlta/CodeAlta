using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Describes the machine-local UI state for restoring open work threads.
/// </summary>
public sealed class WorkThreadViewState
{
    /// <summary>
    /// Gets or sets the ordered open thread identifiers.
    /// </summary>
    [JsonPropertyName("open_thread_ids")]
    public List<string> OpenThreadIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the selected thread identifier.
    /// </summary>
    [JsonPropertyName("selected_thread_id")]
    public string? SelectedThreadId { get; set; }

    /// <summary>
    /// Gets or sets the last update time.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Validates the view state.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the data is invalid.</exception>
    public void Validate()
    {
        var duplicateThreadId = OpenThreadIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .Where(static x => x.Count() > 1)
            .Select(static x => x.Key)
            .FirstOrDefault();

        if (duplicateThreadId is not null)
        {
            throw new ArgumentException($"Open thread ids contain duplicate entry '{duplicateThreadId}'.", nameof(OpenThreadIds));
        }

        if (!string.IsNullOrWhiteSpace(SelectedThreadId)
            && !OpenThreadIds.Contains(SelectedThreadId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Selected thread id must be present in open_thread_ids.", nameof(SelectedThreadId));
        }
    }
}
