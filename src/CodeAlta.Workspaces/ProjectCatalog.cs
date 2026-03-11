using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Catalog;

/// <summary>
/// Loads and persists project descriptors from the global CodeAlta catalog.
/// </summary>
public sealed partial class ProjectCatalog
{
    private readonly CatalogOptions _options;
    private readonly WorkspaceYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectCatalog"/> class.
    /// </summary>
    /// <param name="options">Catalog layout options.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public ProjectCatalog(CatalogOptions options, WorkspaceYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new WorkspaceYamlSerializer();
    }

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
        foreach (var markdownPath in Directory.EnumerateFiles(projectsRoot, "readme.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeProjectMarkdown(markdown);
            descriptor.SourcePath = markdownPath;
            descriptor.Validate();
            results.Add(descriptor);
        }

        return results
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

        var directory = Path.Combine(_options.ProjectsRoot, project.Slug);
        Directory.CreateDirectory(directory);
        var markdownPath = Path.Combine(directory, "readme.md");
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
            return existing;
        }

        var directory = new DirectoryInfo(normalizedPath);
        var baseSlug = Slugify(directory.Name);
        var slug = EnsureUniqueSlug(baseSlug, projects);
        var descriptor = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = slug,
            DisplayName = directory.Name,
            ProjectPath = normalizedPath,
            DefaultBranch = "main",
            MarkdownBody = $"# {directory.Name}\n\nAutomatically discovered from local usage.",
        };

        await SaveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return descriptor;
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
        return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

        if (!WorkspaceKeyValidator.IsValid(slug))
        {
            slug = "project";
        }

        return slug;
    }

    [GeneratedRegex("^-+|-+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrimHyphensRegex();
}
