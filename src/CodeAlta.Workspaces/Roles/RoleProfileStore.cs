using System.Text.Json.Serialization;
using SharpYaml;

namespace CodeAlta.Workspaces.Roles;

/// <summary>
/// Loads and normalizes role profiles from markdown files.
/// </summary>
public sealed class RoleProfileStore
{
    /// <summary>
    /// Lists role profiles from the provided root directories.
    /// </summary>
    /// <param name="roots">Root directories to scan recursively for <c>*.md</c> role files.</param>
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

            foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
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
    /// Gets a profile by role id.
    /// </summary>
    /// <param name="roots">Root directories to scan recursively for <c>*.md</c> role files.</param>
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
            var id = Coalesce(frontmatter.Id, Path.GetFileNameWithoutExtension(sourcePath));
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
                    Allowed = frontmatter.AllowedTools ?? [],
                    Denied = frontmatter.DeniedTools ?? [],
                },
                DefaultBackend = frontmatter.DefaultBackend,
                DefaultModel = frontmatter.DefaultModel,
                DefaultReasoningEffort = frontmatter.DefaultReasoningEffort,
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
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : heading[2..].Trim();
        var description = lines
            .Select(static x => x.Trim())
            .FirstOrDefault(static x => x.Length > 0 && !x.StartsWith("#", StringComparison.Ordinal))
            ?? $"{name} role profile.";

        return new RoleProfile
        {
            RoleId = Path.GetFileNameWithoutExtension(sourcePath),
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
                RoleId = "global",
                Name = "Global",
                Description = "Coordinates cross-workspace planning and routing.",
                Instructions = "Coordinate global requests and route tasks to scoped agents.",
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = ["codealta.tasks", "codealta.search", "codealta.workspaces"],
                },
                DefaultBackend = "codex",
                SourcePath = "builtin://global",
            },
            new RoleProfile
            {
                RoleId = "planner.workspace",
                Name = "Planner Workspace",
                Description = "Breaks goals into durable tasks.",
                Instructions = "Create and maintain task trees with clear acceptance criteria.",
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = ["codealta.tasks", "codealta.artifacts"],
                },
                DefaultBackend = "codex",
                SourcePath = "builtin://planner.workspace",
            },
            new RoleProfile
            {
                RoleId = "builder.project",
                Name = "Builder Project",
                Description = "Executes project-scoped tasks.",
                Instructions = "Implement changes, validate with build/tests, and report verification.",
                ToolsPolicy = new RoleToolsPolicy
                {
                    Allowed = ["codealta.tasks", "codealta.search", "codealta.artifacts"],
                },
                DefaultBackend = "codex",
                SourcePath = "builtin://builder.project",
            },
        ];
    }

    private static string Coalesce(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed class RoleFrontmatter
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

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
    }
}

