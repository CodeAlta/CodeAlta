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
    private readonly bool _readOnly;

    // The project files as this catalog read them, read and parsed again only when a file changes; forgotten after each
    // write of the catalog. The descriptors kept are never handed out: each load gets copies.
    private readonly ParsedFileCache<ProjectSource> _sources = new();
    private readonly Func<string, string, ProjectSource> _parseSource;

    // The projects of folders the catalog does not list, which this instance works in without registering them. They
    // live as long as the catalog, never in a file, and a project file for the same folder takes their place.
    private readonly object _transientGate = new();
    private readonly List<ProjectDescriptor> _transient = [];

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
        _parseSource = ParseSource;
    }

    /// <summary>
    /// Gets the catalog layout options.
    /// </summary>
    public CatalogOptions Options => _options;

    /// <summary>
    /// Gets or initializes whether the catalog leaves the project files as they are, for a process that must not write
    /// the shared state of the global root (a headless run beside CodeAlta Desktop). Default false.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsurePersistedAsync"/> then keeps a project it does not know as a transient project
    /// (<see cref="GetOrAddTransientAsync"/>), <see cref="DeleteAsync"/> only forgets a transient project, and the other
    /// methods that write a project file throw <see cref="InvalidOperationException"/>. It is true whenever the options say
    /// so (<see cref="CatalogOptions.ReadOnlyGlobalState"/>).
    /// </remarks>
    public bool IsReadOnly
    {
        get => _readOnly || _options.ReadOnlyGlobalState;
        init => _readOnly = value;
    }

    /// <summary>
    /// Loads all known projects from the portable catalog, with the transient projects of this catalog
    /// (<see cref="GetOrAddTransientAsync"/>) whose folder has no project file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The known projects, descriptors the caller owns and may change.</returns>
    /// <remarks>
    /// The folder is listed at each call, and a project file is read and parsed again only when it changed since this
    /// catalog last did (see "Reads and the cache of the parsed files" in <c>doc/catalog-and-config.md</c>).
    /// </remarks>
    public async Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var projectsRoot = _options.ProjectsRoot;
        var results = new List<ProjectDescriptor>();
        if (Directory.Exists(projectsRoot))
        {
            foreach (var markdownPath in EnumerateProjectMarkdownPaths(projectsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = await _sources.ReadAsync(markdownPath, _parseSource, cancellationToken).ConfigureAwait(false);
                // A relative path is resolved against the current folder of the process, as each load always did.
                results.Add(source.Relative ? ParseProject(markdownPath, source.Markdown, out _) : Copy(source.Descriptor));
            }
        }

        var persisted = results
            .GroupBy(static project => NormalizePath(project.ProjectPath), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static project => IsFlatProjectMarkdownPath(project.SourcePath))
                .ThenBy(static project => project.SourcePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToList();
        lock (_transientGate)
        {
            if (_transient.Count > 0)
            {
                var folders = persisted.Select(static project => NormalizePath(project.ProjectPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                persisted.AddRange(_transient.Where(project => !folders.Contains(project.ProjectPath)).Select(Copy));
            }
        }

        return persisted
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets the project of a folder: the one the catalog lists, or a transient project of this catalog, created for the
    /// folder when it has none. A transient project is kept in memory only, as long as the catalog, and is listed by
    /// <see cref="LoadAsync"/> until a project file names the same folder.
    /// </summary>
    /// <param name="projectPath">The folder, an absolute path; it need not exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A copy of the project, which the caller owns.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectPath"/> is blank or not an absolute path.</exception>
    public async Task<ProjectDescriptor> GetOrAddTransientAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Path.IsPathFullyQualified(projectPath.Trim()))
        {
            throw new ArgumentException("A project folder is an absolute path.", nameof(projectPath));
        }

        var normalizedPath = NormalizePath(projectPath);
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(project =>
            string.Equals(NormalizePath(project.ProjectPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var projectName = ProjectPathNameFormatter.InferName(normalizedPath);
        var displayName = ProjectPathNameFormatter.InferDisplayName(normalizedPath);
        var descriptor = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = EnsureUniqueSlug(Slugify(projectName), projects),
            Name = projectName,
            DisplayName = displayName,
            ProjectPath = normalizedPath,
            DefaultBranch = "main",
            MarkdownBody = $"# {displayName}\n\nTransient project context for the current host process.",
        };
        return AddTransient(descriptor);
    }

    // Keeps a project the catalog does not list, or the transient project already kept for its folder.
    private ProjectDescriptor AddTransient(ProjectDescriptor project)
    {
        lock (_transientGate)
        {
            var kept = _transient.FirstOrDefault(candidate => string.Equals(candidate.ProjectPath, project.ProjectPath, StringComparison.OrdinalIgnoreCase));
            if (kept is null)
            {
                kept = Copy(project);
                kept.SourcePath = null;
                kept.Archived = false;
                _transient.Add(kept);
            }

            return Copy(kept);
        }
    }

    // Forgets the transient project of a folder; false when the catalog keeps none for it.
    private bool RemoveTransient(ProjectDescriptor project)
    {
        var folder = NormalizePath(project.ProjectPath);
        lock (_transientGate)
        {
            return _transient.RemoveAll(candidate => string.Equals(candidate.Id, project.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ProjectPath, folder, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The catalog of projects is read-only in this process: no project file is written.");
        }
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
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    public async Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ThrowIfReadOnly();
        project.Validate();

        Directory.CreateDirectory(_options.ProjectsRoot);
        var markdownPath = GetProjectMarkdownPath(project.Slug);
        var markdown = _serializer.SerializeProjectMarkdown(project);
        try
        {
            await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sources.Invalidate();
        }

        project.SourcePath = markdownPath;
    }

    /// <summary>
    /// Ensures that a project descriptor has a persisted catalog file.
    /// </summary>
    /// <param name="project">The project descriptor to persist when no existing descriptor has the same path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The persisted descriptor for the project path. A read-only catalog (<see cref="IsReadOnly"/>) writes nothing: it
    /// returns the project it lists for the path as it is, archived or not, and otherwise keeps the project as a
    /// transient project (<see cref="GetOrAddTransientAsync"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="project"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="project"/> is invalid.</exception>
    public async Task<ProjectDescriptor> EnsurePersistedAsync(ProjectDescriptor project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();

        project.ProjectPath = NormalizePath(project.ProjectPath);
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(candidate =>
            string.Equals(NormalizePath(candidate.ProjectPath), project.ProjectPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Archived && !IsReadOnly)
            {
                existing.Archived = false;
                await SaveAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        if (projects.Any(candidate =>
                !string.Equals(candidate.Id, project.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Slug, project.Slug, StringComparison.OrdinalIgnoreCase)))
        {
            project.Slug = EnsureUniqueSlug(project.Slug, projects);
        }

        if (IsReadOnly)
        {
            return AddTransient(project);
        }

        await SaveAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <summary>
    /// Deletes a project descriptor from the portable catalog.
    /// </summary>
    /// <param name="project">The project descriptor to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when at least one project file was deleted; otherwise <see langword="false"/>. A
    /// read-only catalog (<see cref="IsReadOnly"/>) deletes no file: it forgets the transient project, and returns
    /// whether it kept one.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="project"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="project"/> is invalid.</exception>
    public async Task<bool> DeleteAsync(ProjectDescriptor project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        var forgotten = RemoveTransient(project);
        if (IsReadOnly)
        {
            return forgotten;
        }

        var normalizedProjectPath = NormalizePath(project.ProjectPath);
        var markdownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectsRoot = _options.ProjectsRoot;
        if (Directory.Exists(projectsRoot))
        {
            foreach (var markdownPath in EnumerateProjectMarkdownPaths(projectsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
                var descriptor = _serializer.DeserializeProjectMarkdown(markdown);
                descriptor.ProjectPath = NormalizePath(descriptor.ProjectPath);
                descriptor.SourcePath = markdownPath;
                descriptor.Validate();
                if (string.Equals(descriptor.Id, project.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePath(descriptor.ProjectPath), normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    markdownPaths.Add(markdownPath);
                }
            }
        }

        if (markdownPaths.Count == 0)
        {
            AddFallbackProjectMarkdownPath(markdownPaths, project.SourcePath);
            AddFallbackProjectMarkdownPath(markdownPaths, GetProjectMarkdownPath(project.Slug));
        }

        var deleted = false;
        try
        {
            foreach (var markdownPath in markdownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(markdownPath))
                {
                    continue;
                }

                File.Delete(markdownPath);
                deleted = true;
            }
        }
        finally
        {
            if (deleted) _sources.Invalidate();
        }

        if (deleted)
        {
            project.SourcePath = null;
        }

        return deleted || forgotten;
    }

    /// <summary>
    /// Ensures that a project exists in the catalog for the specified path.
    /// </summary>
    /// <param name="projectPath">The local project path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The existing or newly created project descriptor.</returns>
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    public async Task<ProjectDescriptor> UpsertFromPathAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        ThrowIfReadOnly();

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

        var projectName = ProjectPathNameFormatter.InferName(normalizedPath);
        var displayName = ProjectPathNameFormatter.InferDisplayName(normalizedPath);
        var baseSlug = Slugify(projectName);
        var slug = EnsureUniqueSlug(baseSlug, projects);
        var descriptor = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = slug,
            Name = projectName,
            DisplayName = displayName,
            ProjectPath = normalizedPath,
            DefaultBranch = "main",
            MarkdownBody = $"# {displayName}\n\nAutomatically discovered from local usage.",
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

    /// <summary>Copies a project descriptor, with its lists and its checkout rule.</summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <returns>An equal descriptor that shares nothing that can be changed with <paramref name="descriptor"/>.</returns>
    internal static ProjectDescriptor Copy(ProjectDescriptor descriptor) => descriptor.Copy();

    // A save of a project file by a conditional edit (rename, archive, spaces), after which the files are read again.
    private async Task<TextFileSaveResult> SaveSourceAsync(TextFileSaveRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _projectNameFiles.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sources.Invalidate();
        }
    }

    // A project file as a load reads it: parsed, its path normalized, then validated. Relative: the path written in the
    // file is not a full one, so its normalized form depends on the current folder of the process.
    private ProjectDescriptor ParseProject(string markdownPath, string markdown, out bool relative)
    {
        var descriptor = _serializer.DeserializeProjectMarkdown(markdown);
        relative = !Path.IsPathFullyQualified(descriptor.ProjectPath.Trim());
        descriptor.ProjectPath = NormalizePath(descriptor.ProjectPath);
        descriptor.SourcePath = markdownPath;
        descriptor.Validate();
        return descriptor;
    }

    private ProjectSource ParseSource(string markdownPath, string markdown)
        => new(ParseProject(markdownPath, markdown, out var relative), markdown, relative);

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

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    private void AddFallbackProjectMarkdownPath(HashSet<string> markdownPaths, string? markdownPath)
    {
        if (string.IsNullOrWhiteSpace(markdownPath) ||
            !File.Exists(markdownPath) ||
            !IsInsideGlobalRoot(NormalizePath(markdownPath), NormalizePath(_options.ProjectsRoot)))
        {
            return;
        }

        markdownPaths.Add(markdownPath);
    }

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

    // A project file as the catalog keeps it: its descriptor, never handed out, and its text.
    private sealed record ProjectSource(ProjectDescriptor Descriptor, string Markdown, bool Relative);
}
