namespace CodeAlta.Workspaces;

/// <summary>
/// Loads and saves durable work-thread metadata and machine-local thread restoration state.
/// </summary>
public sealed class WorkThreadCatalog
{
    private readonly WorkspaceCatalog _workspaceCatalog;
    private readonly WorkspaceCatalogOptions _options;
    private readonly WorkThreadYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkThreadCatalog"/> class.
    /// </summary>
    /// <param name="workspaceCatalog">Workspace catalog used for workspace validation and path resolution.</param>
    /// <param name="options">Catalog options.</param>
    /// <param name="serializer">Optional serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when required dependencies are missing.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="WorkspaceCatalogOptions.GlobalRepoRoot"/> is empty.</exception>
    public WorkThreadCatalog(
        WorkspaceCatalog workspaceCatalog,
        WorkspaceCatalogOptions options,
        WorkThreadYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceCatalog);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.GlobalRepoRoot))
        {
            throw new ArgumentException("Global repository root is required.", nameof(options));
        }

        _workspaceCatalog = workspaceCatalog;
        _options = options;
        _serializer = serializer ?? new WorkThreadYamlSerializer();
    }

    /// <summary>
    /// Loads all durable work threads.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded thread descriptors.</returns>
    public async Task<IReadOnlyList<WorkThreadDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<WorkThreadDescriptor>();
        var workspaces = await _workspaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspaceDirectory = Path.GetDirectoryName(workspace.SourcePath);
            if (string.IsNullOrWhiteSpace(workspaceDirectory))
            {
                continue;
            }

            var threadsDirectory = Path.Combine(workspaceDirectory, "threads");
            if (!Directory.Exists(threadsDirectory))
            {
                continue;
            }

            foreach (var threadPath in Directory.EnumerateFiles(threadsDirectory, "readme.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = await LoadThreadDescriptorAsync(threadPath, cancellationToken).ConfigureAwait(false);
                results.Add(descriptor);
            }
        }

        var globalThreadPath = Path.Combine(_options.GlobalRepoRoot, "threads", "global", "readme.md");
        if (File.Exists(globalThreadPath))
        {
            var descriptor = await LoadThreadDescriptorAsync(globalThreadPath, cancellationToken).ConfigureAwait(false);
            results.Add(descriptor);
        }

        return results
            .OrderByDescending(static x => x.LastActiveAt)
            .ThenBy(static x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads a single thread by id.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching thread when found; otherwise <see langword="null"/>.</returns>
    public async Task<WorkThreadDescriptor?> GetByIdAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        var threads = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return threads.FirstOrDefault(x => string.Equals(x.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves a durable work-thread descriptor.
    /// </summary>
    /// <param name="thread">The thread descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        thread.Validate();

        var workspaces = await _workspaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetByIdAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        ValidateThreadScope(thread, workspaces, existing);

        var path = ResolveThreadPath(thread, workspaces);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var markdown = _serializer.SerializeThreadMarkdown(thread);
        await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);
        thread.SourcePath = path;
    }

    /// <summary>
    /// Loads the machine-local thread view state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The view state, or an empty one when the file is missing.</returns>
    public async Task<WorkThreadViewState> LoadViewStateAsync(CancellationToken cancellationToken = default)
    {
        var path = GetViewStatePath();
        if (!File.Exists(path))
        {
            return new WorkThreadViewState();
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var viewState = _serializer.DeserializeViewState(yaml);
        viewState.Validate();
        return viewState;
    }

    /// <summary>
    /// Saves the machine-local thread view state.
    /// </summary>
    /// <param name="viewState">The view state to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveViewStateAsync(WorkThreadViewState viewState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        viewState.Validate();

        var path = GetViewStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var yaml = _serializer.SerializeViewState(viewState);
        await File.WriteAllTextAsync(path, yaml, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateThreadScope(
        WorkThreadDescriptor thread,
        IReadOnlyList<WorkspaceDescriptor> workspaces,
        WorkThreadDescriptor? existing)
    {
        if (thread.Kind == WorkThreadKind.Global)
        {
            return;
        }

        var workspace = workspaces.FirstOrDefault(x => string.Equals(x.Id, thread.WorkspaceRef, StringComparison.OrdinalIgnoreCase));
        if (workspace is null)
        {
            throw new InvalidDataException($"Thread '{thread.ThreadId}' references unknown workspace '{thread.WorkspaceRef}'.");
        }

        if (existing is not null
            && existing.Kind == WorkThreadKind.WorkspaceThread
            && existing.IsWorkspaceLocked
            && !string.Equals(existing.WorkspaceRef, thread.WorkspaceRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Thread '{thread.ThreadId}' cannot change workspace after the first prompt.");
        }

        if (thread.ScopeMode == WorkThreadScopeMode.AllProjects)
        {
            return;
        }

        var validProjectRefs = workspace.Projects
            .Select(static x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var invalidProjectRef = thread.ProjectRefs.FirstOrDefault(projectRef => !validProjectRefs.Contains(projectRef));
        if (invalidProjectRef is not null)
        {
            throw new InvalidDataException(
                $"Thread '{thread.ThreadId}' references project '{invalidProjectRef}' outside workspace '{workspace.Slug}'.");
        }
    }

    private string ResolveThreadPath(WorkThreadDescriptor thread, IReadOnlyList<WorkspaceDescriptor> workspaces)
    {
        if (thread.Kind == WorkThreadKind.Global)
        {
            return Path.Combine(_options.GlobalRepoRoot, "threads", "global", "readme.md");
        }

        var workspace = workspaces.First(x => string.Equals(x.Id, thread.WorkspaceRef, StringComparison.OrdinalIgnoreCase));
        var workspaceDirectory = Path.GetDirectoryName(workspace.SourcePath)
            ?? throw new InvalidOperationException($"Workspace '{workspace.Slug}' does not have a source path.");
        return Path.Combine(workspaceDirectory, "threads", thread.ThreadId, "readme.md");
    }

    private async Task<WorkThreadDescriptor> LoadThreadDescriptorAsync(string path, CancellationToken cancellationToken)
    {
        var markdown = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var descriptor = _serializer.DeserializeThreadMarkdown(markdown);
        descriptor.SourcePath = path;
        descriptor.Validate();
        return descriptor;
    }

    private string GetViewStatePath()
    {
        return Path.Combine(_options.GlobalRepoRoot, "machine", "ui-state.yaml");
    }
}
