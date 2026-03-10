using System.Text;
using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Workspaces;

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

        [JsonPropertyName("workspace_ref")]
        public string? WorkspaceRef { get; set; }

        [JsonPropertyName("project_refs")]
        public List<string>? ProjectRefs { get; set; }

        [JsonPropertyName("scope_mode")]
        public string? ScopeMode { get; set; }

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
            WorkspaceRef = frontMatter.WorkspaceRef,
            ProjectRefs = frontMatter.ProjectRefs ?? [],
            ScopeMode = ParseScopeMode(frontMatter.ScopeMode),
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
                WorkThreadKind.Global => "global",
                WorkThreadKind.WorkspaceThread => "workspace_thread",
                _ => throw new InvalidOperationException($"Unsupported thread kind '{descriptor.Kind}'."),
            },
            WorkspaceRef = descriptor.WorkspaceRef,
            ProjectRefs = descriptor.ProjectRefs.Count == 0 ? null : descriptor.ProjectRefs,
            ScopeMode = descriptor.ScopeMode switch
            {
                WorkThreadScopeMode.SingleProject => "single_project",
                WorkThreadScopeMode.MultiProject => "multi_project",
                WorkThreadScopeMode.AllProjects => "all_projects",
                _ => throw new InvalidOperationException($"Unsupported scope mode '{descriptor.ScopeMode}'."),
            },
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
            "global" => WorkThreadKind.Global,
            "workspace_thread" => WorkThreadKind.WorkspaceThread,
            _ => WorkThreadKind.WorkspaceThread,
        };
    }

    private static WorkThreadScopeMode ParseScopeMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "single_project" => WorkThreadScopeMode.SingleProject,
            "multi_project" => WorkThreadScopeMode.MultiProject,
            "all_projects" => WorkThreadScopeMode.AllProjects,
            _ => WorkThreadScopeMode.AllProjects,
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
