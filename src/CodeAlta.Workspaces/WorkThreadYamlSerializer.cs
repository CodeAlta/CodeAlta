using System.Text;
using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Catalog;

/// <summary>
/// Serializes and deserializes work-thread metadata.
/// </summary>
public sealed class WorkThreadYamlSerializer
{
    private sealed class WorkThreadFrontMatter
    {
        [JsonPropertyName("thread_id")]
        public string? ThreadId { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("backend_id")]
        public string? BackendId { get; set; }

        [JsonPropertyName("backend_session_id")]
        public string? BackendSessionId { get; set; }

        [JsonPropertyName("project_ref")]
        public string? ProjectRef { get; set; }

        [JsonPropertyName("parent_thread_id")]
        public string? ParentThreadId { get; set; }

        [JsonPropertyName("working_directory")]
        public string? WorkingDirectory { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("last_active_at")]
        public DateTimeOffset? LastActiveAt { get; set; }

        [JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAt { get; set; }

        [JsonPropertyName("latest_summary")]
        public string? LatestSummary { get; set; }
    }

    private sealed class WorkThreadViewStateDocument
    {
        [JsonPropertyName("open_thread_ids")]
        public List<string>? OpenThreadIds { get; set; }

        [JsonPropertyName("selected_thread_id")]
        public string? SelectedThreadId { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Deserializes a work-thread descriptor from markdown frontmatter.
    /// </summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The parsed descriptor.</returns>
    public WorkThreadDescriptor DeserializeThreadMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = ParseFrontMatter(markdown);
        var frontMatter = YamlSerializer.Deserialize<WorkThreadFrontMatter>(document.FrontMatter) ?? new WorkThreadFrontMatter();

        return new WorkThreadDescriptor
        {
            ThreadId = frontMatter.ThreadId ?? string.Empty,
            Kind = ParseKind(frontMatter.Kind),
            BackendId = frontMatter.BackendId ?? string.Empty,
            BackendSessionId = frontMatter.BackendSessionId ?? string.Empty,
            ProjectRef = frontMatter.ProjectRef,
            ParentThreadId = frontMatter.ParentThreadId,
            WorkingDirectory = frontMatter.WorkingDirectory ?? string.Empty,
            Title = frontMatter.Title ?? string.Empty,
            Status = ParseStatus(frontMatter.Status),
            CreatedAt = frontMatter.CreatedAt ?? default,
            UpdatedAt = frontMatter.UpdatedAt ?? default,
            LastActiveAt = frontMatter.LastActiveAt ?? default,
            StartedAt = frontMatter.StartedAt,
            LatestSummary = frontMatter.LatestSummary,
            MarkdownBody = document.Body,
        };
    }

    /// <summary>
    /// Serializes a work-thread descriptor to markdown frontmatter.
    /// </summary>
    /// <param name="descriptor">The descriptor to serialize.</param>
    /// <returns>Markdown text.</returns>
    public string SerializeThreadMarkdown(WorkThreadDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var frontMatter = new WorkThreadFrontMatter
        {
            ThreadId = descriptor.ThreadId,
            Kind = descriptor.Kind switch
            {
                WorkThreadKind.GlobalThread => "global_thread",
                WorkThreadKind.ProjectThread => "project_thread",
                WorkThreadKind.InternalThread => "internal_thread",
                _ => throw new InvalidOperationException($"Unsupported thread kind '{descriptor.Kind}'."),
            },
            BackendId = descriptor.BackendId,
            BackendSessionId = descriptor.BackendSessionId,
            ProjectRef = descriptor.ProjectRef,
            ParentThreadId = descriptor.ParentThreadId,
            WorkingDirectory = descriptor.WorkingDirectory,
            Title = descriptor.Title,
            Status = descriptor.Status switch
            {
                WorkThreadStatus.Draft => "draft",
                WorkThreadStatus.Active => "active",
                WorkThreadStatus.Waiting => "waiting",
                WorkThreadStatus.Blocked => "blocked",
                WorkThreadStatus.Background => "background",
                WorkThreadStatus.Completed => "completed",
                WorkThreadStatus.Archived => "archived",
                _ => throw new InvalidOperationException($"Unsupported thread status '{descriptor.Status}'."),
            },
            CreatedAt = descriptor.CreatedAt,
            UpdatedAt = descriptor.UpdatedAt,
            LastActiveAt = descriptor.LastActiveAt,
            StartedAt = descriptor.StartedAt,
            LatestSummary = descriptor.LatestSummary,
        };

        return SerializeMarkdown(frontMatter, descriptor.MarkdownBody, descriptor.Title);
    }

    /// <summary>
    /// Deserializes machine-local work-thread view state from YAML.
    /// </summary>
    /// <param name="yaml">The YAML content.</param>
    /// <returns>The parsed view state.</returns>
    public WorkThreadViewState DeserializeViewState(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var document = YamlSerializer.Deserialize<WorkThreadViewStateDocument>(yaml) ?? new WorkThreadViewStateDocument();
        return new WorkThreadViewState
        {
            OpenThreadIds = document.OpenThreadIds ?? [],
            SelectedThreadId = document.SelectedThreadId,
            UpdatedAt = document.UpdatedAt ?? default,
        };
    }

    /// <summary>
    /// Serializes machine-local work-thread view state to YAML.
    /// </summary>
    /// <param name="viewState">The view state.</param>
    /// <returns>The serialized YAML.</returns>
    public string SerializeViewState(WorkThreadViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        var document = new WorkThreadViewStateDocument
        {
            OpenThreadIds = viewState.OpenThreadIds,
            SelectedThreadId = viewState.SelectedThreadId,
            UpdatedAt = viewState.UpdatedAt,
        };

        return YamlSerializer.Serialize(document);
    }

    private static WorkThreadKind ParseKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "global_thread" or "global" => WorkThreadKind.GlobalThread,
            "project_thread" => WorkThreadKind.ProjectThread,
            "internal_thread" => WorkThreadKind.InternalThread,
            _ => WorkThreadKind.ProjectThread,
        };
    }

    private static WorkThreadStatus ParseStatus(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "draft" => WorkThreadStatus.Draft,
            "active" => WorkThreadStatus.Active,
            "waiting" => WorkThreadStatus.Waiting,
            "blocked" => WorkThreadStatus.Blocked,
            "background" => WorkThreadStatus.Background,
            "completed" => WorkThreadStatus.Completed,
            "archived" => WorkThreadStatus.Archived,
            _ => WorkThreadStatus.Draft,
        };
    }

    private static (string FrontMatter, string Body) ParseFrontMatter(string markdown)
    {
        using var reader = new StringReader(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Markdown metadata must start with YAML frontmatter.");
        }

        var frontMatter = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                var body = reader.ReadToEnd();
                return (frontMatter.ToString(), body.TrimStart('\n'));
            }

            frontMatter.AppendLine(line);
        }

        throw new InvalidDataException("Markdown metadata frontmatter was not closed.");
    }

    private static string SerializeMarkdown<T>(T frontMatter, string? body, string title)
    {
        var yaml = YamlSerializer.Serialize(frontMatter).Trim();
        var markdownBody = string.IsNullOrWhiteSpace(body)
            ? $"# {title}"
            : body!.Trim();

        return $"---\n{yaml}\n---\n\n{markdownBody}\n";
    }
}

