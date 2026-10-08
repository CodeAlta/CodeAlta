using System.Text;
using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Catalog;

/// <summary>
/// Serializes and deserializes project and machine metadata.
/// </summary>
public sealed class CatalogYamlSerializer
{
    private sealed class ProjectFrontMatter
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("repo_url")]
        public string? LegacyRepoUrl { get; set; }

        [JsonPropertyName("default_branch")]
        public string? DefaultBranch { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("archived")]
        public bool? Archived { get; set; }

        [JsonPropertyName("checkout")]
        public CheckoutFrontMatter? Checkout { get; set; }

        // Left out of a file whose project is in no space: such a file stays as it was before spaces.
        [JsonPropertyName("spaces")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Spaces { get; set; }
    }

    private sealed class SpaceFrontMatter
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("icon")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Icon { get; set; }

        [JsonPropertyName("color")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }

        [JsonPropertyName("order")]
        public int? Order { get; set; }
    }

    private sealed class CheckoutFrontMatter
    {
        [JsonPropertyName("path_template")]
        public string? PathTemplate { get; set; }
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
        descriptor.Spaces ??= [];
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
        profile.ProjectOverrides ??= new Dictionary<string, MachineProjectOverride>(StringComparer.OrdinalIgnoreCase);
        return profile;
    }

    /// <summary>
    /// Serializes a project descriptor to YAML.
    /// </summary>
    /// <param name="descriptor">The project descriptor.</param>
    /// <returns>Serialized YAML text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string SerializeProject(ProjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return YamlSerializer.Serialize(descriptor);
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
        var projectPath = frontMatter.Path ?? frontMatter.LegacyRepoUrl ?? string.Empty;
        var inferredDisplayName = ProjectPathNameFormatter.InferDisplayName(projectPath);
        var displayName = frontMatter.DisplayName ??
            (!string.IsNullOrWhiteSpace(inferredDisplayName) &&
             !ProjectPathNameFormatter.IsValidProjectName(inferredDisplayName)
                ? inferredDisplayName
                : frontMatter.Name ?? inferredDisplayName);

        return new ProjectDescriptor
        {
            Id = frontMatter.Id ?? string.Empty,
            Slug = frontMatter.Slug ?? string.Empty,
            Name = frontMatter.Name ?? ProjectPathNameFormatter.InferName(projectPath),
            DisplayName = displayName,
            Description = frontMatter.Description,
            ProjectPath = projectPath,
            DefaultBranch = frontMatter.DefaultBranch ?? "main",
            Tags = frontMatter.Tags ?? [],
            Spaces = SpaceDescriptor.NormalizeIds(frontMatter.Spaces),
            Archived = frontMatter.Archived ?? false,
            Checkout = new CheckoutRule
            {
                PathTemplate = frontMatter.Checkout?.PathTemplate ?? string.Empty,
            },
            MarkdownBody = document.Body,
        };
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
            Name = descriptor.Name,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            Path = descriptor.ProjectPath,
            DefaultBranch = descriptor.DefaultBranch,
            Tags = descriptor.Tags,
            Archived = descriptor.Archived,
            Checkout = string.IsNullOrWhiteSpace(descriptor.Checkout.PathTemplate)
                ? null
                : new CheckoutFrontMatter { PathTemplate = descriptor.Checkout.PathTemplate },
            Spaces = SpaceDescriptor.NormalizeIds(descriptor.Spaces) is { Count: > 0 } spaces ? spaces : null,
        };

        return SerializeMarkdown(frontMatter, descriptor.MarkdownBody, descriptor.DisplayName);
    }

    /// <summary>
    /// Deserializes a space from the Markdown file that describes it: its front matter, and its body, which
    /// is the description of the space.
    /// </summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The space, not yet validated.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">Thrown when the text has no front matter.</exception>
    public SpaceDescriptor DeserializeSpaceMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = ParseFrontMatter(markdown);
        var frontMatter = YamlSerializer.Deserialize<SpaceFrontMatter>(document.FrontMatter) ?? new SpaceFrontMatter();
        var description = document.Body.Trim();
        return new SpaceDescriptor
        {
            Id = frontMatter.Id ?? string.Empty,
            Name = frontMatter.Name?.Trim() ?? string.Empty,
            Icon = string.IsNullOrWhiteSpace(frontMatter.Icon) ? null : frontMatter.Icon.Trim().ToLowerInvariant(),
            Color = string.IsNullOrWhiteSpace(frontMatter.Color) ? null : frontMatter.Color.Trim(),
            Order = frontMatter.Order ?? 0,
            Description = description.Length == 0 ? null : description,
        };
    }

    /// <summary>
    /// Serializes a space to the Markdown file that describes it.
    /// </summary>
    /// <param name="descriptor">The space.</param>
    /// <returns>Markdown text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string SerializeSpaceMarkdown(SpaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var yaml = YamlSerializer.Serialize(new SpaceFrontMatter
        {
            Id = descriptor.Id,
            Kind = "space",
            Name = descriptor.Name,
            Icon = descriptor.Icon,
            Color = descriptor.Color,
            Order = descriptor.Order,
        }).Trim();
        var description = descriptor.Description?.Trim();
        return string.IsNullOrEmpty(description) ? $"---\n{yaml}\n---\n" : $"---\n{yaml}\n---\n\n{description}\n";
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
