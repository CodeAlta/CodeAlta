using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>
/// Stores local session UI state and legacy host-owned internal session metadata.
/// </summary>
public sealed class SessionViewCatalog
{
    private readonly CatalogOptions _options;
    private readonly SessionViewYamlSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionViewCatalog"/> class.
    /// </summary>
    /// <param name="options">Catalog options.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public SessionViewCatalog(CatalogOptions options, SessionViewYamlSerializer? serializer = null)
        : this(options, new AgentSessionJournalFile(), serializer)
    {
    }

    internal SessionViewCatalog(
        CatalogOptions options,
        AgentSessionJournalFile journalFile,
        SessionViewYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalFile);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new SessionViewYamlSerializer();
        JournalStore = new SessionViewJournalStore(options, journalFile);
    }

    /// <summary>
    /// Gets the session journal metadata store.
    /// </summary>
    public SessionViewJournalStore JournalStore { get; }

    /// <summary>
    /// Loads all legacy host-owned internal session records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The internal session descriptors.</returns>
    public async Task<IReadOnlyList<SessionViewDescriptor>> LoadInternalAsync(CancellationToken cancellationToken = default)
    {
        var root = _options.InternalSessionsRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<SessionViewDescriptor>();
        foreach (var markdownPath in Directory.EnumerateFiles(root, "readme.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var descriptor = _serializer.DeserializeSessionMarkdown(markdown);
            descriptor.SourcePath = markdownPath;
            descriptor.Validate();
            results.Add(descriptor);
        }

        return results
            .OrderByDescending(static session => session.LastActiveAt)
            .ToArray();
    }

    /// <summary>
    /// Saves a legacy host-owned internal session record.
    /// </summary>
    /// <param name="session">The internal session descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveInternalAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Kind != SessionViewKind.InternalSession)
        {
            throw new InvalidOperationException("Only internal session descriptors are persisted by the session catalog.");
        }

        session.Validate();

        var directory = Path.Combine(_options.InternalSessionsRoot, GetInternalDirectoryName(session.SessionId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "readme.md");
        var markdown = _serializer.SerializeSessionMarkdown(session);
        await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);
        session.SourcePath = path;
    }

    /// <summary>
    /// Loads the local session view state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The view state, or an empty one when the file is missing.</returns>
    public async Task<SessionViewViewState> LoadViewStateAsync(CancellationToken cancellationToken = default)
    {
        var path = GetViewStatePath();
        if (!File.Exists(path))
        {
            return new SessionViewViewState();
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var viewState = _serializer.DeserializeViewState(yaml);
        viewState.Validate();
        return viewState;
    }

    /// <summary>
    /// Saves the local session view state.
    /// </summary>
    /// <param name="viewState">The view state to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveViewStateAsync(SessionViewViewState viewState, CancellationToken cancellationToken = default)
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

    private static string GetInternalDirectoryName(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[sessionId.Length];
        for (var index = 0; index < sessionId.Length; index++)
        {
            var character = sessionId[index];
            buffer[index] = invalidCharacters.Contains(character) ? '-' : character;
        }

        return new string(buffer);
    }
}
