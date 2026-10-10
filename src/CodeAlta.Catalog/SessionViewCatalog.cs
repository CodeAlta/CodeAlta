using System.Text;
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
        SessionViewYamlSerializer? serializer = null,
        ApplicationDatabase? database = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalFile);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _serializer = serializer ?? new SessionViewYamlSerializer();
        JournalStore = new SessionViewJournalStore(options, journalFile, database);
    }

    /// <summary>
    /// Gets the session journal metadata store.
    /// </summary>
    public SessionViewJournalStore JournalStore { get; }

    /// <summary>Gets the text codec shared by UI-state saves and cooperating file editors/stores.</summary>
    public TextFileCodec TextFiles { get; } = new();

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
    /// <exception cref="IOException">Reading failed.</exception>
    /// <exception cref="InvalidDataException">YAML has an unsupported shape.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="DecoderFallbackException">The file contains invalid Unicode bytes.</exception>
    /// <exception cref="SharpYaml.YamlException">The YAML is malformed.</exception>
    /// <exception cref="ArgumentException">Known state is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<SessionViewViewState> LoadViewStateAsync(CancellationToken cancellationToken = default)
    {
        TextFileSnapshot file;
        try
        {
            file = await TextFiles.LoadAsync(GetViewStatePath(), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new SessionViewViewState();
        }
        catch (DirectoryNotFoundException)
        {
            return new SessionViewViewState();
        }

        var viewState = _serializer.DeserializeViewState(file.Text);
        viewState.Validate();
        viewState.Revision = file.Revision;
        return viewState;
    }

    /// <summary>
    /// Gives a catalog that has no view state yet the preferences of another one: the provider, model,
    /// agent prompt and reasoning chosen per project, and the navigator settings (sort order, theme, language).
    /// </summary>
    /// <remarks>
    /// Used by an instance with a separate state root, so its first session in a project uses what the
    /// user chose for that project. Open sessions, the selection and layouts are not copied: they name
    /// sessions this catalog does not have.
    /// </remarks>
    /// <param name="source">The catalog to read the preferences from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the view state was created; false when this catalog already had one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <inheritdoc cref="LoadViewStateAsync"/>
    public async Task<bool> SeedViewStateFromAsync(SessionViewCatalog source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (File.Exists(GetViewStatePath())) return false;
        var theirs = await source.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        var seed = new SessionViewViewState
        {
            ProjectPreferences = new(theirs.ProjectPreferences, StringComparer.OrdinalIgnoreCase),
            Navigator = theirs.Navigator,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var saved = await SaveViewStateAsync(seed, cancellationToken).ConfigureAwait(false);
        return !saved.IsConflict;
    }

    /// <summary>
    /// Conditionally saves against the state's loaded revision (missing for a new state).
    /// Repeated saves must use the returned acknowledgment, not reuse the original baseline.
    /// </summary>
    /// <param name="viewState">The view state to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An acknowledgment or conflict; failure throws without advancing the baseline.</returns>
    /// <inheritdoc cref="SaveViewStateAsync(SessionViewStateSaveRequest, CancellationToken)"/>
    public Task<SessionViewStateSaveResult> SaveViewStateAsync(SessionViewViewState viewState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        return SaveViewStateAsync(CreateViewStateSaveRequest(viewState, viewState.Revision), cancellationToken);
    }

    /// <summary>Synchronously freezes UI-owned mutable state before entering asynchronous persistence.</summary>
    /// <exception cref="ArgumentNullException">The state or revision is null.</exception>
    /// <exception cref="ArgumentException">Known state is invalid.</exception>
    /// <exception cref="InvalidDataException">Retained YAML has an unsupported or invalid shape.</exception>
    /// <exception cref="SharpYaml.YamlException">Retained YAML is malformed.</exception>
    public SessionViewStateSaveRequest CreateViewStateSaveRequest(SessionViewViewState viewState, TextFileRevision expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        viewState.Validate();
        return new SessionViewStateSaveRequest(_serializer.SerializeViewState(viewState), expectedRevision);
    }

    /// <summary>Saves a frozen YAML snapshot against an explicitly acknowledged raw-byte revision.</summary>
    /// <remarks>Uses staged replacement through the shared codec. External writers still have the
    /// codec's documented final-check/commit race; this is not cross-process atomic compare-and-swap.</remarks>
    /// <exception cref="ArgumentNullException">The request or a required value is null.</exception>
    /// <exception cref="ArgumentException">Known state is invalid.</exception>
    /// <exception cref="InvalidDataException">YAML has an unsupported shape.</exception>
    /// <exception cref="SharpYaml.YamlException">YAML is malformed.</exception>
    /// <exception cref="IOException">Reading, staging or replacing the file failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    public async Task<SessionViewStateSaveResult> SaveViewStateAsync(SessionViewStateSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var state = _serializer.DeserializeViewState(request.Yaml);
        state.Validate();
        // Requests are data, not permission to bypass migration/transient-field omission.
        var yaml = _serializer.SerializeViewState(state);
        var saved = await TextFiles.SaveAsync(new TextFileSaveRequest(
            GetViewStatePath(), yaml, Encoding.UTF8, false, request.ExpectedRevision), cancellationToken).ConfigureAwait(false);
        return new SessionViewStateSaveResult(saved.IsConflict ? null : saved.CurrentRevision, saved.CurrentRevision);
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
