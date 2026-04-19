namespace CodeAlta.Catalog;

/// <summary>
/// Stores local thread UI state and optional host-owned internal thread linkage metadata.
/// </summary>
public sealed class WorkThreadCatalog
{
    private readonly CatalogOptions _options;
    private readonly WorkThreadYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkThreadCatalog"/> class.
    /// </summary>
    /// <param name="options">Catalog options.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public WorkThreadCatalog(CatalogOptions options, WorkThreadYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new WorkThreadYamlSerializer();
    }

    /// <summary>
    /// Loads all host-owned internal thread linkage records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The internal thread descriptors.</returns>
    public async Task<IReadOnlyList<WorkThreadDescriptor>> LoadInternalAsync(CancellationToken cancellationToken = default)
    {
        var root = _options.InternalThreadsRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<WorkThreadDescriptor>();
        foreach (var markdownPath in Directory.EnumerateFiles(root, "readme.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeThreadMarkdown(markdown);
            descriptor.SourcePath = markdownPath;
            descriptor.Validate();
            results.Add(descriptor);
        }

        return results
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

    /// <summary>
    /// Saves a host-owned internal thread linkage record.
    /// </summary>
    /// <param name="thread">The internal thread descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveInternalAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.Kind != WorkThreadKind.InternalThread)
        {
            throw new InvalidOperationException("Only internal thread descriptors are persisted by the thread catalog.");
        }

        thread.Validate();

        var directory = Path.Combine(_options.InternalThreadsRoot, GetInternalDirectoryName(thread.ThreadId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "readme.md");
        var markdown = _serializer.SerializeThreadMarkdown(thread);
        await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);
        thread.SourcePath = path;
    }

    /// <summary>
    /// Loads the local thread view state.
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
    /// Saves the local thread view state.
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

    private string GetViewStatePath()
    {
        return _options.UiStatePath;
    }

    private static string GetInternalDirectoryName(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[threadId.Length];
        for (var index = 0; index < threadId.Length; index++)
        {
            var character = threadId[index];
            buffer[index] = invalidCharacters.Contains(character) ? '-' : character;
        }

        return new string(buffer);
    }
}
