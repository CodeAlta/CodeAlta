using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Catalog.Roles;

/// <summary>
/// Loads and normalizes agent role profiles from markdown files.
/// </summary>
public sealed class RoleProfileStore
{
    /// <summary>
    /// Lists role profiles from the provided root directories.
    /// </summary>
    /// <param name="roots">Root directories to scan recursively for <c>*.agent.md</c> role files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered role profiles.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="roots"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<RoleProfile>> LoadAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var result = new List<RoleProfile>();
        foreach (var root in roots.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.agent.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var profile = Parse(path, content);
                result.Add(profile);
            }
        }

        if (result.Count == 0)
        {
            result.AddRange(GetBuiltInProfiles());
        }

        return result
            .GroupBy(static x => x.RoleId, StringComparer.OrdinalIgnoreCase)
            .Select(static x => x.First())
            .OrderBy(static x => x.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads agent role profiles from the global catalog and active project overlays.
    /// </summary>
    /// <param name="globalCatalogRoot">The global CodeAlta catalog root.</param>
    /// <param name="projectRoots">Active project roots.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered profiles.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="globalCatalogRoot"/> is empty.</exception>
    public Task<IReadOnlyList<RoleProfile>> LoadCatalogAgentsAsync(
        string globalCatalogRoot,
        IReadOnlyList<string>? projectRoots = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(globalCatalogRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(globalCatalogRoot));
        }

        var roots = new List<string>
        {
            Path.Combine(globalCatalogRoot, "agents"),
        };

        if (projectRoots is not null)
        {
            foreach (var projectRoot in projectRoots.Where(static x => !string.IsNullOrWhiteSpace(x)))
            {
                roots.Add(Path.Combine(projectRoot, ".alta", "agents"));
            }
        }

        return LoadAsync(roots, cancellationToken);
    }

    /// <summary>
    /// Gets a profile by role id.
    /// </summary>
    /// <param name="roots">Root directories to scan recursively for <c>*.agent.md</c> role files.</param>
    /// <param name="roleId">Role id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile when found; otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="roleId"/> is empty.</exception>
    public async Task<RoleProfile?> GetByIdAsync(
        IReadOnlyList<string> roots,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new ArgumentException("Role id is required.", nameof(roleId));
        }

        var roles = await LoadAsync(roots, cancellationToken).ConfigureAwait(false);
        return roles.FirstOrDefault(x =>
            string.Equals(x.RoleId, roleId, StringComparison.OrdinalIgnoreCase));
    }

    private RoleProfile Parse(string sourcePath, string content)
    {
        if (TrySplitFrontmatter(content, out var frontmatterText, out var body))
        {
            var frontmatter = YamlSerializer.Deserialize<RoleFrontmatter>(frontmatterText) ?? new RoleFrontmatter();
            var agentKey = GetAgentKeyFromPath(sourcePath);
            var id = Coalesce(frontmatter.Name, agentKey);
            var name = Coalesce(frontmatter.Name, id);
            var description = Coalesce(frontmatter.Description, $"{name} role profile.");
            return new RoleProfile
            {
                RoleId = id,
                Name = name,
                Description = description,
                Instructions = body.Trim(),
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = frontmatter.Tools ?? frontmatter.AllowedTools ?? [],
                    Denied = frontmatter.DeniedTools ?? [],
                },
                DefaultBackend = frontmatter.CodeAlta?.DefaultBackend ?? frontmatter.DefaultBackend,
                DefaultModel = frontmatter.Model ?? frontmatter.DefaultModel,
                DefaultReasoningEffort = frontmatter.CodeAlta?.DefaultReasoningEffort ?? frontmatter.DefaultReasoningEffort,
                Scope = frontmatter.CodeAlta?.Scope,
                Tags = frontmatter.CodeAlta?.Tags ?? [],
                DisableModelInvocation = frontmatter.DisableModelInvocation ?? false,
                UserInvocable = frontmatter.UserInvocable ?? true,
                IsBuiltIn = frontmatter.CodeAlta?.BuiltIn ?? false,
                SourcePath = sourcePath,
            };
        }

        return ParseWithoutFrontmatter(sourcePath, content);
    }

    private static RoleProfile ParseWithoutFrontmatter(string sourcePath, string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = lines.FirstOrDefault(static x => x.StartsWith("# ", StringComparison.Ordinal));
        var name = heading is null
            ? GetAgentKeyFromPath(sourcePath)
            : heading[2..].Trim();
        var description = lines
            .Select(static x => x.Trim())
            .FirstOrDefault(static x => x.Length > 0 && !x.StartsWith("#", StringComparison.Ordinal))
            ?? $"{name} role profile.";

        return new RoleProfile
        {
            RoleId = GetAgentKeyFromPath(sourcePath),
            Name = name,
            Description = description,
            Instructions = content.Trim(),
            ToolsPolicy = new RoleToolsPolicy(),
            SourcePath = sourcePath,
        };
    }

    private static bool TrySplitFrontmatter(string contents, out string frontmatter, out string body)
    {
        const string delimiter = "---";
        frontmatter = string.Empty;
        body = contents;

        if (!contents.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var reader = new StringReader(contents);
        _ = reader.ReadLine();

        var builder = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.Trim(), delimiter, StringComparison.Ordinal))
            {
                frontmatter = string.Join('\n', builder);
                body = reader.ReadToEnd() ?? string.Empty;
                return true;
            }

            builder.Add(line);
        }

        return false;
    }

    private static IReadOnlyList<RoleProfile> GetBuiltInProfiles()
    {
        return
        [
            new RoleProfile
            {
                RoleId = "coordinator",
                Name = "Coordinator",
                Description = "Coordinates work inside a thread and emits structured schedules when needed.",
                Instructions = "Coordinate scoped requests and emit one valid codealta_schedule block when coordination is required.",
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = [],
                },
                DefaultBackend = "codex",
                Scope = "workspace",
                IsBuiltIn = true,
                SourcePath = "builtin://coordinator",
            },
            new RoleProfile
            {
                RoleId = "general",
                Name = "General",
                Description = "General coding agent for direct scoped work.",
                Instructions = "Handle the assigned scoped coding work directly and report concrete outcomes.",
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = [],
                },
                DefaultBackend = "codex",
                Scope = "project",
                IsBuiltIn = true,
                SourcePath = "builtin://general",
            },
        ];
    }

    private static string Coalesce(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string GetAgentKeyFromPath(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        if (fileName.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".agent.md".Length];
        }

        return Path.GetFileNameWithoutExtension(sourcePath);
    }

    private sealed class RoleFrontmatter
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("tools")]
        public List<string>? Tools { get; set; }

        [JsonPropertyName("tools_allowed")]
        public List<string>? AllowedTools { get; set; }

        [JsonPropertyName("tools_denied")]
        public List<string>? DeniedTools { get; set; }

        [JsonPropertyName("default_backend")]
        public string? DefaultBackend { get; set; }

        [JsonPropertyName("default_model")]
        public string? DefaultModel { get; set; }

        [JsonPropertyName("default_reasoning_effort")]
        public string? DefaultReasoningEffort { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("disable-model-invocation")]
        public bool? DisableModelInvocation { get; set; }

        [JsonPropertyName("user-invocable")]
        public bool? UserInvocable { get; set; }

        [JsonPropertyName("codealta")]
        public CodeAltaFrontmatter? CodeAlta { get; set; }
    }

    private sealed class CodeAltaFrontmatter
    {
        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("default_backend")]
        public string? DefaultBackend { get; set; }

        [JsonPropertyName("default_reasoning_effort")]
        public string? DefaultReasoningEffort { get; set; }

        [JsonPropertyName("builtin")]
        public bool? BuiltIn { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }
    }
}


