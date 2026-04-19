using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Catalog;

/// <summary>
/// Loads and persists project descriptors from the global CodeAlta catalog.
/// </summary>
public sealed partial class ProjectCatalog
{
    private readonly CatalogOptions _options;
    private readonly CatalogYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectCatalog"/> class.
    /// </summary>
    /// <param name="options">Catalog layout options.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public ProjectCatalog(CatalogOptions options, CatalogYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new CatalogYamlSerializer();
    }

    /// <summary>
    /// Gets the catalog layout options.
    /// </summary>
    public CatalogOptions Options => _options;

    /// <summary>
    /// Loads all known projects from the portable catalog.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The known projects.</returns>
    public async Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var projectsRoot = _options.ProjectsRoot;
        if (!Directory.Exists(projectsRoot))
        {
            return [];
        }

        var results = new List<ProjectDescriptor>();
        foreach (var markdownPath in EnumerateProjectMarkdownPaths(projectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeProjectMarkdown(markdown);
            descriptor.ProjectPath = NormalizePath(descriptor.ProjectPath);
            descriptor.SourcePath = markdownPath;
            descriptor.Validate();
            results.Add(descriptor);
        }

        return results
            .GroupBy(static project => NormalizePath(project.ProjectPath), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static project => IsFlatProjectMarkdownPath(project.SourcePath))
                .ThenBy(static project => project.SourcePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets a project by identifier.
    /// </summary>
    /// <param name="projectId">The project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching project when found; otherwise <see langword="null"/>.</returns>
    public async Task<ProjectDescriptor?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a project by slug.
    /// </summary>
    /// <param name="projectSlug">The normalized project slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching project when found; otherwise <see langword="null"/>.</returns>
    public async Task<ProjectDescriptor?> GetBySlugAsync(string projectSlug, CancellationToken cancellationToken = default)
    {
        CatalogSlugValidator.Validate(projectSlug, nameof(projectSlug));

        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project =>
            string.Equals(project.Slug, projectSlug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a project by local project path.
    /// </summary>
    /// <param name="projectPath">The local project path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching project when found; otherwise <see langword="null"/>.</returns>
    public async Task<ProjectDescriptor?> GetByPathAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        var normalizedPath = NormalizePath(projectPath);
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project =>
            string.Equals(NormalizePath(project.ProjectPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves a project descriptor to the portable catalog.
    /// </summary>
    /// <param name="project">The project descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();

        Directory.CreateDirectory(_options.ProjectsRoot);
        var markdownPath = GetProjectMarkdownPath(project.Slug);
        var markdown = _serializer.SerializeProjectMarkdown(project);
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);
        project.SourcePath = markdownPath;
    }

    /// <summary>
    /// Ensures that a project exists in the catalog for the specified path.
    /// </summary>
    /// <param name="projectPath">The local project path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The existing or newly created project descriptor.</returns>
    public async Task<ProjectDescriptor> UpsertFromPathAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        var normalizedPath = NormalizePath(projectPath);
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(project =>
            string.Equals(NormalizePath(project.ProjectPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Archived)
            {
                existing.Archived = false;
                await SaveAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        var directory = new DirectoryInfo(normalizedPath);
        var baseSlug = Slugify(directory.Name);
        var slug = EnsureUniqueSlug(baseSlug, projects);
        var descriptor = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = slug,
            Name = directory.Name,
            DisplayName = directory.Name,
            ProjectPath = normalizedPath,
            DefaultBranch = "main",
            MarkdownBody = $"# {directory.Name}\n\nAutomatically discovered from local usage.",
        };

        await SaveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>
    /// Imports known project folders from external working-directory history.
    /// </summary>
    /// <param name="workingDirectories">Candidate working directories.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projects that were resolved or imported.</returns>
    public async Task<IReadOnlyList<ProjectDescriptor>> ImportWorkingDirectoriesAsync(
        IEnumerable<string?> workingDirectories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectories);

        var results = new List<ProjectDescriptor>();
        var knownProjects = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var globalRoot = NormalizePath(_options.GlobalRoot);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in workingDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var normalizedPath = NormalizePath(rawPath);
            if (!uniquePaths.Add(normalizedPath))
            {
                continue;
            }

            if (IsInsideGlobalRoot(normalizedPath, globalRoot) || !Directory.Exists(normalizedPath))
            {
                continue;
            }

            var existing = knownProjects.FirstOrDefault(project =>
                string.Equals(NormalizePath(project.ProjectPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                results.Add(existing);
                continue;
            }

            var imported = await UpsertFromPathAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            knownProjects.Add(imported);
            results.Add(imported);
        }

        return results;
    }

    /// <summary>
    /// Loads a machine profile by machine id.
    /// </summary>
    /// <param name="machineId">The machine id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The machine profile when found; otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="machineId"/> is empty.</exception>
    public async Task<MachineProfile?> LoadMachineProfileAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new ArgumentException("Machine id is required.", nameof(machineId));
        }

        var profilePath = Path.Combine(_options.MachinesRoot, $"{machineId}.yaml");
        if (!File.Exists(profilePath))
        {
            return null;
        }

        var yaml = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
        var profile = _serializer.DeserializeMachineProfile(yaml);
        profile.Validate();
        return profile;
    }

    private static string EnsureUniqueSlug(string baseSlug, IReadOnlyList<ProjectDescriptor> projects)
    {
        var candidate = baseSlug;
        var suffix = 2;
        var usedSlugs = projects
            .Select(static project => project.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (usedSlugs.Contains(candidate))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInsideGlobalRoot(string path, string globalRoot)
    {
        if (string.Equals(path, globalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = globalRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateProjectMarkdownPaths(string projectsRoot)
    {
        foreach (var markdownPath in Directory.EnumerateFiles(projectsRoot, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return markdownPath;
        }

        foreach (var legacyDirectory in Directory.EnumerateDirectories(projectsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var markdownPath = Path.Combine(legacyDirectory, "readme.md");
            if (File.Exists(markdownPath))
            {
                yield return markdownPath;
            }
        }
    }

    private string GetProjectMarkdownPath(string projectSlug)
        => Path.Combine(_options.ProjectsRoot, $"{projectSlug}.md");

    private static bool IsFlatProjectMarkdownPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(Path.GetFileName(path), "readme.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousHyphen = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousHyphen = false;
                continue;
            }

            if (previousHyphen)
            {
                continue;
            }

            builder.Append('-');
            previousHyphen = true;
        }

        var slug = TrimHyphensRegex().Replace(builder.ToString(), string.Empty);
        if (slug.Length < 2)
        {
            slug = "project";
        }

        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('-', '_', '.');
        }

        if (!CatalogSlugValidator.IsValid(slug))
        {
            slug = "project";
        }

        return slug;
    }

    [GeneratedRegex("^-+|-+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrimHyphensRegex();
}
