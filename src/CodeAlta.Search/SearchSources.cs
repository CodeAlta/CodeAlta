using CodeAlta.Catalog.Search;

namespace CodeAlta.Search;

/// <summary>A project, as search needs to know it.</summary>
/// <param name="ProjectId">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its folder.</param>
/// <param name="Archived">Whether it is archived.</param>
public sealed record SearchProjectInfo(string ProjectId, string Name, string Path, bool Archived)
{
    /// <summary>Gets the spaces it belongs to besides the default one.</summary>
    public IReadOnlyList<string> Spaces { get; init; } = [];
}

/// <summary>A session, as search needs to know it when a query runs.</summary>
/// <param name="SessionId">Its id.</param>
/// <param name="Title">Its title now.</param>
/// <param name="ProjectId">Its project, or null for a chat.</param>
/// <param name="SpaceId">Its space, or null for the default one.</param>
/// <param name="Archived">Whether it is archived.</param>
public sealed record SearchSessionInfo(string SessionId, string Title, string? ProjectId, string? SpaceId, bool Archived);

/// <summary>What the application tells search: its settings, its projects and its sessions.</summary>
public interface ISearchHost
{
    /// <summary>Gets the settings now.</summary>
    /// <returns>The settings.</returns>
    SearchSettings GetSettings();

    /// <summary>Gets the projects of the catalog.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The projects.</returns>
    ValueTask<IReadOnlyList<SearchProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken);

    /// <summary>Gets the sessions the application lists, by id.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The sessions.</returns>
    ValueTask<IReadOnlyDictionary<string, SearchSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Gets whether a project's own configuration keeps it out of search.</summary>
    /// <param name="project">The project.</param>
    /// <returns>True when it does.</returns>
    bool IsProjectExcludedByItself(SearchProjectInfo project);

    /// <summary>
    /// Gets the recent use of the skills by their name, for a project and a space: about the number of their uses in the last days,
    /// the older ones weighing less. Null, the default, when the application does not know.
    /// </summary>
    /// <param name="projectId">The project the query is about; null for none.</param>
    /// <param name="spaceId">The space of the query.</param>
    /// <returns>The scores by name of skill, or null.</returns>
    IReadOnlyDictionary<string, double>? GetSkillUsage(string? projectId, string spaceId) => null;
}

/// <summary>What a source gives when it changed.</summary>
/// <param name="Stamp">What says whether it changed, kept to compare at the next check.</param>
/// <param name="Documents">Everything it holds now.</param>
public sealed record SearchSourceRead(string Stamp, IReadOnlyList<SearchDocument> Documents);

/// <summary>A source of catalog content: skills, tools, plugins, the guide, the plans of a project.</summary>
public interface ISearchSource
{
    /// <summary>Gets its key in the index, unique: <c>skills</c>, <c>docs</c>, <c>plans:&lt;project&gt;</c>…</summary>
    string Key { get; }

    /// <summary>Gets the kinds it gives, for the check before a query of those kinds.</summary>
    IReadOnlyList<SearchKind> Kinds { get; }

    /// <summary>Reads the source when it changed.</summary>
    /// <param name="previousStamp">The stamp of the last reading, or null.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>What it holds, or null when its stamp says it did not change.</returns>
    ValueTask<SearchSourceRead?> ReadAsync(string? previousStamp, CancellationToken cancellationToken);
}

/// <summary>A source whose content the application gives as a list, compared by the hash of its texts.</summary>
public sealed class ListSearchSource : ISearchSource
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<SearchDocument>>> _read;

    /// <summary>Creates a source.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="kinds">Its kinds.</param>
    /// <param name="read">Gives its documents now: computed in memory, so read at every check.</param>
    public ListSearchSource(string key, IReadOnlyList<SearchKind> kinds, Func<CancellationToken, ValueTask<IReadOnlyList<SearchDocument>>> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(read);
        Key = key;
        Kinds = kinds;
        _read = read;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public IReadOnlyList<SearchKind> Kinds { get; }

    /// <inheritdoc />
    public async ValueTask<SearchSourceRead?> ReadAsync(string? previousStamp, CancellationToken cancellationToken)
    {
        var documents = await _read(cancellationToken).ConfigureAwait(false);
        var stamp = Convert.ToHexStringLower(SearchText.Hash(string.Join("\u0001", documents.Select(static document =>
            document.Ref + "\u0000" + document.Title + "\u0000" + document.Text + "\u0000" + document.Extra + "\u0000" + document.ProjectId))));
        return stamp == previousStamp ? null : new SearchSourceRead(stamp, documents);
    }
}

/// <summary>
/// A source of Markdown files in folders, such as the plans and the tasks of a project: its stamp is the names,
/// lengths and write times of the files, so a check reads no file; a changed stamp reads them all, and the hash of
/// each text decides which items are written again.
/// </summary>
public sealed class FolderSearchSource : ISearchSource
{
    private readonly IReadOnlyList<(string Folder, SearchKind Kind, string RefPrefix)> _folders;
    private readonly Func<string, SearchKind, string, SearchDocument?> _document;

    /// <summary>Creates a source.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="folders">The folders, the kind of their files and the prefix of their refs.</param>
    /// <param name="document">Makes the document of a file from its path, its kind and its text, or null to leave it out.</param>
    public FolderSearchSource(string key, IReadOnlyList<(string Folder, SearchKind Kind, string RefPrefix)> folders, Func<string, SearchKind, string, SearchDocument?> document)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(document);
        Key = key;
        _folders = folders;
        _document = document;
        Kinds = [.. folders.Select(static folder => folder.Kind).Distinct()];
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public IReadOnlyList<SearchKind> Kinds { get; }

    /// <inheritdoc />
    public async ValueTask<SearchSourceRead?> ReadAsync(string? previousStamp, CancellationToken cancellationToken)
    {
        var files = new List<(string Path, SearchKind Kind)>();
        var stamp = new System.Text.StringBuilder();
        foreach (var (folder, kind, _) in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                stamp.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
                files.Add((file, kind));
            }
        }

        var value = stamp.ToString();
        if (value == previousStamp) return null;
        var documents = new List<SearchDocument>(files.Count);
        foreach (var (path, kind) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try { text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            if (_document(path, kind, text) is { } document) documents.Add(document);
        }

        return new SearchSourceRead(value, documents);
    }
}
