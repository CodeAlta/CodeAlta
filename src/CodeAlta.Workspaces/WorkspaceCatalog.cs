namespace CodeAlta.Workspaces;

/// <summary>
/// Loads and saves workspace/project descriptors from the portable catalog layout.
/// </summary>
public sealed class WorkspaceCatalog
{
    private readonly WorkspaceCatalogOptions _options;
    private readonly WorkspaceYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceCatalog"/> class.
    /// </summary>
    /// <param name="options">Catalog options.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="WorkspaceCatalogOptions.GlobalRepoRoot"/> is empty.</exception>
    public WorkspaceCatalog(WorkspaceCatalogOptions options, WorkspaceYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.GlobalRepoRoot))
        {
            throw new ArgumentException("Global repository root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new WorkspaceYamlSerializer();
    }

    /// <summary>
    /// Loads all workspaces from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All loaded workspace descriptors.</returns>
    public async Task<IReadOnlyList<WorkspaceDescriptor>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var workspaceRoot = _options.WorkspacesRoot;
        if (!Directory.Exists(workspaceRoot))
        {
            return [];
        }

        var projectsById = await LoadProjectsByIdAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<WorkspaceDescriptor>();

        foreach (var markdownPath in Directory.EnumerateFiles(workspaceRoot, "readme.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsThreadMetadataPath(markdownPath))
            {
                continue;
            }

            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeWorkspaceMarkdown(markdown);
            descriptor.SourcePath = markdownPath;

            var workspaceDirectory = Path.GetDirectoryName(markdownPath)!;
            var expectedKey = Path.GetFileName(workspaceDirectory);
            if (!string.Equals(descriptor.Key, expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Workspace key '{descriptor.Key}' does not match folder '{expectedKey}'.");
            }

            foreach (var projectRef in descriptor.ProjectRefs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!projectsById.TryGetValue(projectRef, out var project))
                {
                    throw new InvalidDataException(
                        $"Workspace '{descriptor.Key}' references unknown project '{projectRef}'.");
                }

                descriptor.Projects.Add(CloneProject(project));
            }

            descriptor.Validate();
            results.Add(descriptor);
        }

        return results
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads a single workspace by key.
    /// </summary>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching workspace descriptor, or <see langword="null"/> when not found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workspaceKey"/> is invalid.</exception>
    public async Task<WorkspaceDescriptor?> GetByKeyAsync(
        string workspaceKey,
        CancellationToken cancellationToken = default)
    {
        WorkspaceKeyValidator.Validate(workspaceKey, nameof(workspaceKey));

        var items = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(x => string.Equals(x.Key, workspaceKey, StringComparison.OrdinalIgnoreCase));
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

        var profilePath = Path.Combine(_options.GlobalRepoRoot, "machines", $"{machineId}.yaml");
        if (!File.Exists(profilePath))
        {
            return null;
        }

        var yaml = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
        var profile = _serializer.DeserializeMachineProfile(yaml);
        profile.Validate();
        return profile;
    }

    /// <summary>
    /// Loads all projects from the catalog.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All project descriptors.</returns>
    public async Task<IReadOnlyList<ProjectDescriptor>> LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        return (await LoadProjectsByIdAsync(cancellationToken).ConfigureAwait(false))
            .Values
            .OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Saves a workspace descriptor to disk.
    /// </summary>
    /// <param name="workspace">The workspace descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveWorkspaceAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.Validate();

        var directory = Path.Combine(_options.WorkspacesRoot, workspace.Key);
        Directory.CreateDirectory(directory);
        var markdownPath = Path.Combine(directory, "readme.md");
        var markdown = _serializer.SerializeWorkspaceMarkdown(workspace);
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);
        workspace.SourcePath = markdownPath;
    }

    /// <summary>
    /// Saves a project descriptor to disk.
    /// </summary>
    /// <param name="project">The project descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveProjectAsync(ProjectDescriptor project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();

        var directory = Path.Combine(_options.ProjectsRoot, project.Key);
        Directory.CreateDirectory(directory);
        var markdownPath = Path.Combine(directory, "readme.md");
        var markdown = _serializer.SerializeProjectMarkdown(project);
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);
        project.SourcePath = markdownPath;
    }

    private async Task<Dictionary<string, ProjectDescriptor>> LoadProjectsByIdAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, ProjectDescriptor>(StringComparer.OrdinalIgnoreCase);
        var projectRoot = _options.ProjectsRoot;
        if (!Directory.Exists(projectRoot))
        {
            return results;
        }

        foreach (var markdownPath in Directory.EnumerateFiles(projectRoot, "readme.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeProjectMarkdown(markdown);
            descriptor.SourcePath = markdownPath;

            var expectedKey = Path.GetFileName(Path.GetDirectoryName(markdownPath)!);
            if (!string.Equals(descriptor.Key, expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Project key '{descriptor.Key}' does not match folder '{expectedKey}'.");
            }

            descriptor.Validate();
            results[descriptor.Id] = descriptor;
        }

        return results;
    }

    private static ProjectDescriptor CloneProject(ProjectDescriptor project)
    {
        return new ProjectDescriptor
        {
            Id = project.Id,
            Key = project.Key,
            DisplayName = project.DisplayName,
            Description = project.Description,
            RepoUrl = project.RepoUrl,
            DefaultBranch = project.DefaultBranch,
            Checkout = new CheckoutRule
            {
                PathTemplate = project.Checkout.PathTemplate,
            },
            Tags = [.. project.Tags],
            SourcePath = project.SourcePath,
            MarkdownBody = project.MarkdownBody,
        };
    }

    private static bool IsThreadMetadataPath(string markdownPath)
    {
        var segments = markdownPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(static segment => string.Equals(segment, "threads", StringComparison.OrdinalIgnoreCase));
    }
}
