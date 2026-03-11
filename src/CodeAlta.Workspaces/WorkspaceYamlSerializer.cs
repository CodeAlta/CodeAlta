using System.Text;
using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Workspaces;

/// <summary>
/// Serializes and deserializes workspace and project metadata.
/// </summary>
public sealed class WorkspaceYamlSerializer
{
    private sealed class WorkspaceFrontMatter
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("project_refs")]
        public List<string>? ProjectRefs { get; set; }

        [JsonPropertyName("checkout")]
        public CheckoutFrontMatter? Checkout { get; set; }
    }

    private sealed class ProjectFrontMatter
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("repo_url")]
        public string? RepoUrl { get; set; }

        [JsonPropertyName("default_branch")]
        public string? DefaultBranch { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("checkout")]
        public CheckoutFrontMatter? Checkout { get; set; }
    }

    private sealed class CheckoutFrontMatter
    {
        [JsonPropertyName("path_template")]
        public string? PathTemplate { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceYamlSerializer"/> class.
    /// </summary>
    public WorkspaceYamlSerializer()
    {
    }

    /// <summary>
    /// Deserializes a workspace descriptor from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The workspace descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public WorkspaceDescriptor DeserializeWorkspace(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var descriptor = YamlSerializer.Deserialize<WorkspaceDescriptor>(yaml) ?? new WorkspaceDescriptor();

        descriptor.Projects ??= [];
        descriptor.ProjectRefs ??= [];
        descriptor.Tags ??= [];
        return descriptor;
    }

    /// <summary>
    /// Deserializes a project descriptor from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The project descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public ProjectDescriptor DeserializeProject(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var descriptor = YamlSerializer.Deserialize<ProjectDescriptor>(yaml) ?? new ProjectDescriptor();
        descriptor.Tags ??= [];
        return descriptor;
    }

    /// <summary>
    /// Deserializes a machine profile from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The machine profile.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public MachineProfile DeserializeMachineProfile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var profile = YamlSerializer.Deserialize<MachineProfile>(yaml) ?? new MachineProfile();
        profile.WorkspaceCheckoutRoots ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.ProjectOverrides ??= new Dictionary<string, MachineProjectOverride>(StringComparer.OrdinalIgnoreCase);
        return profile;
    }

    /// <summary>
    /// Serializes a workspace descriptor to YAML.
    /// </summary>
    /// <param name="descriptor">The workspace descriptor.</param>
    /// <returns>Serialized YAML text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string SerializeWorkspace(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return YamlSerializer.Serialize(descriptor);
    }

    /// <summary>
    /// Deserializes a workspace descriptor from markdown frontmatter.
    /// </summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The workspace descriptor.</returns>
    public WorkspaceDescriptor DeserializeWorkspaceMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = ParseFrontMatter(markdown);
        var frontMatter = YamlSerializer.Deserialize<WorkspaceFrontMatter>(document.FrontMatter) ?? new WorkspaceFrontMatter();

        return new WorkspaceDescriptor
        {
            Id = frontMatter.Id ?? string.Empty,
            Slug = frontMatter.Slug ?? string.Empty,
            DisplayName = frontMatter.DisplayName ?? string.Empty,
            Description = frontMatter.Description,
            Tags = frontMatter.Tags ?? [],
            ProjectRefs = frontMatter.ProjectRefs ?? [],
            Projects = [],
            MarkdownBody = document.Body,
            DefaultCheckoutRoot = frontMatter.Checkout?.PathTemplate ?? string.Empty,
        };
    }

    /// <summary>
    /// Deserializes a project descriptor from markdown frontmatter.
    /// </summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The project descriptor.</returns>
    public ProjectDescriptor DeserializeProjectMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = ParseFrontMatter(markdown);
        var frontMatter = YamlSerializer.Deserialize<ProjectFrontMatter>(document.FrontMatter) ?? new ProjectFrontMatter();

        return new ProjectDescriptor
        {
            Id = frontMatter.Id ?? string.Empty,
            Slug = frontMatter.Slug ?? string.Empty,
            DisplayName = frontMatter.DisplayName ?? string.Empty,
            Description = frontMatter.Description,
            RepoUrl = frontMatter.RepoUrl ?? string.Empty,
            DefaultBranch = frontMatter.DefaultBranch ?? "main",
            Tags = frontMatter.Tags ?? [],
            Checkout = new CheckoutRule
            {
                PathTemplate = frontMatter.Checkout?.PathTemplate ?? string.Empty,
            },
            MarkdownBody = document.Body,
        };
    }

    /// <summary>
    /// Serializes a workspace descriptor to markdown frontmatter.
    /// </summary>
    /// <param name="descriptor">The workspace descriptor.</param>
    /// <returns>Markdown text.</returns>
    public string SerializeWorkspaceMarkdown(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var frontMatter = new WorkspaceFrontMatter
        {
            Id = descriptor.Id,
            Kind = "workspace",
            Slug = descriptor.Slug,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            Tags = descriptor.Tags,
            ProjectRefs = descriptor.ProjectRefs.Count > 0
                ? descriptor.ProjectRefs
                : descriptor.Projects.Select(static x => x.Id).ToList(),
            Checkout = string.IsNullOrWhiteSpace(descriptor.DefaultCheckoutRoot)
                ? null
                : new CheckoutFrontMatter { PathTemplate = descriptor.DefaultCheckoutRoot },
        };

        return SerializeMarkdown(frontMatter, descriptor.MarkdownBody, descriptor.DisplayName);
    }

    /// <summary>
    /// Serializes a project descriptor to markdown frontmatter.
    /// </summary>
    /// <param name="descriptor">The project descriptor.</param>
    /// <returns>Markdown text.</returns>
    public string SerializeProjectMarkdown(ProjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var frontMatter = new ProjectFrontMatter
        {
            Id = descriptor.Id,
            Kind = "project",
            Slug = descriptor.Slug,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            RepoUrl = descriptor.RepoUrl,
            DefaultBranch = descriptor.DefaultBranch,
            Tags = descriptor.Tags,
            Checkout = string.IsNullOrWhiteSpace(descriptor.Checkout.PathTemplate)
                ? null
                : new CheckoutFrontMatter { PathTemplate = descriptor.Checkout.PathTemplate },
        };

        return SerializeMarkdown(frontMatter, descriptor.MarkdownBody, descriptor.DisplayName);
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

    private static string SerializeMarkdown<T>(T frontMatter, string? body, string? title)
    {
        var yaml = YamlSerializer.Serialize(frontMatter).Trim();
        var markdownBody = string.IsNullOrWhiteSpace(body)
            ? $"# {title}"
            : body!.Trim();

        return $"---\n{yaml}\n---\n\n{markdownBody}\n";
    }
}
