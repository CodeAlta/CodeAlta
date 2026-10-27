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

        // Left out of a file whose project has no label.
        [JsonPropertyName("labels")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Labels { get; set; }

        [JsonPropertyName("sandbox")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Sandbox { get; set; }
    }

    private sealed class LabelFrontMatter
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

        [JsonPropertyName("parent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Parent { get; set; }

        [JsonPropertyName("favorite")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Favorite { get; set; }

        [JsonPropertyName("space")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Space { get; set; }
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

        // The settings of a space: each is left out of a file that does not set it, which stays as it was.
        [JsonPropertyName("new_sessions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceNewSessionsFrontMatter? NewSessions { get; set; }

        [JsonPropertyName("identity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceIdentityFrontMatter? Identity { get; set; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceToolsFrontMatter? Tools { get; set; }
    }

    private sealed class SpaceNewSessionsFrontMatter
    {
        [JsonPropertyName("agent_prompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AgentPrompt { get; set; }

        [JsonPropertyName("tiers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceTiersFrontMatter? Tiers { get; set; }

        [JsonPropertyName("fallback")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceFallbackFrontMatter? Fallback { get; set; }

        [JsonPropertyName("sandbox")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Sandbox { get; set; }
    }

    // The fallback of a space, as [models.fallback] writes it: enabled, the failures it falls back on, and the resume
    // after a limit.
    private sealed class SpaceFallbackFrontMatter
    {
        [JsonPropertyName("enabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Enabled { get; set; }

        [JsonPropertyName("on")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? On { get; set; }

        [JsonPropertyName("resume")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Resume { get; set; }

        [JsonPropertyName("max_resumes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxResumes { get; set; }
    }

    private sealed class SpaceTiersFrontMatter
    {
        [JsonPropertyName("complex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Complex { get; set; }

        [JsonPropertyName("standard")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Standard { get; set; }

        [JsonPropertyName("quick")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Quick { get; set; }
    }

    private sealed class SpaceIdentityFrontMatter
    {
        [JsonPropertyName("git")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceGitFrontMatter? Git { get; set; }

        [JsonPropertyName("github")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceGitHubFrontMatter? GitHub { get; set; }

        [JsonPropertyName("jira")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SpaceJiraFrontMatter? Jira { get; set; }

        // The profiles of other command line tools, by name, in the order of the file.
        [JsonPropertyName("cli")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, SpaceCliFrontMatter?>? Cli { get; set; }
    }

    private sealed class SpaceCliFrontMatter
    {
        [JsonPropertyName("env")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string?>? Env { get; set; }

        [JsonPropertyName("check")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Check { get; set; }

        [JsonPropertyName("login")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Login { get; set; }
    }

    private sealed class SpaceGitFrontMatter
    {
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Email { get; set; }

        [JsonPropertyName("ssh_key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SshKey { get; set; }

        [JsonPropertyName("sign")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Sign { get; set; }

        [JsonPropertyName("signing_key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SigningKey { get; set; }

        [JsonPropertyName("signoff")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? SignOff { get; set; }
    }

    private sealed class SpaceGitHubFrontMatter
    {
        [JsonPropertyName("config_dir")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ConfigDir { get; set; }
    }

    private sealed class SpaceJiraFrontMatter
    {
        [JsonPropertyName("site")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Site { get; set; }

        [JsonPropertyName("email")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Email { get; set; }

        [JsonPropertyName("token_env")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TokenEnv { get; set; }
    }

    private sealed class SpaceToolsFrontMatter
    {
        [JsonPropertyName("disabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Disabled { get; set; }
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
        descriptor.Labels ??= [];
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
            Labels = LabelDescriptor.NormalizeIds(frontMatter.Labels),
            Archived = frontMatter.Archived ?? false,
            Sandbox = SpaceNewSessions.IsSandboxName(frontMatter.Sandbox?.Trim()) ? frontMatter.Sandbox!.Trim() : null,
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
            Labels = LabelDescriptor.NormalizeIds(descriptor.Labels) is { Count: > 0 } labels ? labels : null,
            Sandbox = SpaceNewSessions.IsSandboxName(descriptor.Sandbox?.Trim()) ? descriptor.Sandbox!.Trim() : null,
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
            Settings = ReadSpaceSettings(frontMatter),
        };
    }

    // The settings as the file writes them, before any validation: a target of a tier that cannot be read is left out.
    private static SpaceSettings ReadSpaceSettings(SpaceFrontMatter frontMatter)
    {
        static IReadOnlyList<ModelTiers.ModelTierTarget> Targets(List<string>? values)
            => [.. (values ?? []).Select(static value => ModelTiers.ModelTierTarget.TryParse(value, out var target, out _) ? target : null).OfType<ModelTiers.ModelTierTarget>()];
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var sessions = frontMatter.NewSessions;
        var tiers = sessions?.Tiers is { } written ? new SpaceModelTiers(Targets(written.Complex), Targets(written.Standard), Targets(written.Quick)) : null;
        // A failure the fallback does not know is left out; without "on", it falls back on both.
        var on = sessions?.Fallback?.On?.Select(static trigger => trigger?.Trim().ToLowerInvariant()).ToArray();
        var fallback = sessions?.Fallback is { } own
            ? new ModelTiers.ModelFallbackSettings(own.Enabled ?? false,
                on is null || on.Contains(ModelTiers.ModelFallbackSettings.QuotaTrigger),
                on is null || on.Contains(ModelTiers.ModelFallbackSettings.UnavailableTrigger))
            {
                Resume = own.Resume ?? false,
                MaxResumes = ModelTiers.ModelFallbackSettings.ClampMaxResumes(own.MaxResumes),
            }
            : null;
        var identity = frontMatter.Identity;
        var git = identity?.Git is { } g
            ? new SpaceGitIdentity(Text(g.Name), Text(g.Email), Text(g.SshKey), SpaceGitIdentity.ParseSign(g.Sign), Text(g.SigningKey), g.SignOff)
            : null;
        return new SpaceSettings(
            sessions is null ? null : new SpaceNewSessions(Text(sessions.AgentPrompt), tiers, fallback, Text(sessions.Sandbox)),
            identity is null ? null : new SpaceIdentity(git,
                identity.GitHub is { } github ? new SpaceGitHubIdentity(Text(github.ConfigDir)) : null,
                identity.Jira is { } jira ? new SpaceJiraIdentity(Text(jira.Site), Text(jira.Email), Text(jira.TokenEnv)) : null,
                identity.Cli is { Count: > 0 } cli
                    ? [.. cli.Select(static entry => new SpaceCliProfile(entry.Key?.Trim() ?? string.Empty,
                        [.. (entry.Value?.Env ?? []).Select(static variable => new KeyValuePair<string, string>(variable.Key?.Trim() ?? string.Empty, variable.Value?.Trim() ?? string.Empty))],
                        Text(entry.Value?.Check), Text(entry.Value?.Login)))]
                    : null),
            frontMatter.Tools is { } tools ? new SpaceTools([.. (tools.Disabled ?? []).Select(static name => name?.Trim() ?? string.Empty)]) : null);
    }

    // What the file writes of the settings: nothing for what is not set.
    private static void WriteSpaceSettings(SpaceFrontMatter frontMatter, SpaceSettings settings)
    {
        static List<string>? Targets(IReadOnlyList<ModelTiers.ModelTierTarget> targets) => targets.Count == 0 ? null : [.. targets.Select(static target => target.Format())];

        if (settings.NewSessions is { IsEmpty: false } sessions)
        {
            frontMatter.NewSessions = new SpaceNewSessionsFrontMatter
            {
                AgentPrompt = sessions.AgentPrompt,
                Tiers = sessions.Tiers is { IsEmpty: false } tiers
                    ? new SpaceTiersFrontMatter { Complex = Targets(tiers.Complex), Standard = Targets(tiers.Standard), Quick = Targets(tiers.Quick) }
                    : null,
                // Written whole, "enabled: false" included: a fallback in the file is the space's own.
                Fallback = sessions.Fallback is { } fallback
                    ? new SpaceFallbackFrontMatter
                    {
                        Enabled = fallback.Enabled,
                        On = fallback.OnQuota && fallback.OnUnavailable ? null : [.. fallback.Triggers()],
                        Resume = fallback.Resume ? true : null,
                        MaxResumes = fallback.MaxResumes == ModelTiers.ModelFallbackSettings.DefaultMaxResumes ? null : fallback.MaxResumes,
                    }
                    : null,
                Sandbox = sessions.Sandbox,
            };
        }

        if (settings.Identity is { IsEmpty: false } identity)
        {
            frontMatter.Identity = new SpaceIdentityFrontMatter
            {
                Git = identity.Git is { IsEmpty: false } git
                    ? new SpaceGitFrontMatter
                    {
                        Name = git.Name, Email = git.Email, SshKey = git.SshKey, Sign = SpaceGitIdentity.FormatSign(git.Sign),
                        SigningKey = git.SigningKey, SignOff = git.SignOff,
                    }
                    : null,
                GitHub = identity.GitHub is { IsEmpty: false } github ? new SpaceGitHubFrontMatter { ConfigDir = github.ConfigDir } : null,
                Jira = identity.Jira is { IsEmpty: false } jira ? new SpaceJiraFrontMatter { Site = jira.Site, Email = jira.Email, TokenEnv = jira.TokenEnv } : null,
                Cli = identity.Cli is { Count: > 0 } cli
                    ? cli.ToDictionary(static profile => profile.Name, static profile => (SpaceCliFrontMatter?)new SpaceCliFrontMatter
                    {
                        Env = profile.Environment.ToDictionary(static variable => variable.Key, static variable => (string?)variable.Value, StringComparer.Ordinal),
                        Check = profile.Check,
                        Login = profile.Login,
                    }, StringComparer.Ordinal)
                    : null,
            };
        }

        if (settings.Tools is { IsEmpty: false } tools)
        {
            frontMatter.Tools = new SpaceToolsFrontMatter { Disabled = [.. tools.Disabled] };
        }
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
        var frontMatter = new SpaceFrontMatter
        {
            Id = descriptor.Id,
            Kind = "space",
            Name = descriptor.Name,
            Icon = descriptor.Icon,
            Color = descriptor.Color,
            Order = descriptor.Order,
        };
        WriteSpaceSettings(frontMatter, descriptor.Settings);
        var yaml = YamlSerializer.Serialize(frontMatter).Trim();
        var description = descriptor.Description?.Trim();
        return string.IsNullOrEmpty(description) ? $"---\n{yaml}\n---\n" : $"---\n{yaml}\n---\n\n{description}\n";
    }

    /// <summary>
    /// Deserializes a label from the Markdown file that describes it: its front matter, and its body, which is the
    /// description of the label.
    /// </summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The label, not yet validated.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">Thrown when the text has no front matter.</exception>
    public LabelDescriptor DeserializeLabelMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = ParseFrontMatter(markdown);
        var frontMatter = YamlSerializer.Deserialize<LabelFrontMatter>(document.FrontMatter) ?? new LabelFrontMatter();
        var description = document.Body.Trim();
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new LabelDescriptor
        {
            Id = frontMatter.Id ?? string.Empty,
            Name = frontMatter.Name?.Trim() ?? string.Empty,
            Icon = Text(frontMatter.Icon)?.ToLowerInvariant(),
            Color = Text(frontMatter.Color),
            Order = frontMatter.Order ?? 0,
            Parent = Text(frontMatter.Parent),
            Favorite = frontMatter.Favorite ?? false,
            Space = Text(frontMatter.Space),
            Description = description.Length == 0 ? null : description,
        };
    }

    /// <summary>Serializes a label to the Markdown file that describes it.</summary>
    /// <param name="descriptor">The label.</param>
    /// <returns>Markdown text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string SerializeLabelMarkdown(LabelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var frontMatter = new LabelFrontMatter
        {
            Id = descriptor.Id,
            Kind = "label",
            Name = descriptor.Name,
            Icon = descriptor.Icon,
            Color = descriptor.Color,
            Order = descriptor.Order,
            Parent = descriptor.Parent,
            Favorite = descriptor.Favorite ? true : null,
            // A sub-label is in the space of its top-level label: its own file names none.
            Space = descriptor.Parent is null ? descriptor.Space : null,
        };
        var yaml = YamlSerializer.Serialize(frontMatter).Trim();
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
