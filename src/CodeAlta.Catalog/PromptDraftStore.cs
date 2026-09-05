using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>Owns legacy plain-text prompt drafts under the catalog's global saved-prompts root.</summary>
/// <remarks>
/// Share one instance for cooperating saves and deletes. Revisions identify bytes, not timestamps;
/// external writers still have a final-check/commit race. Images are not stored here.
/// </remarks>
public sealed class PromptDraftStore
{
    private readonly string _root;
    private readonly TextFileCodec _files;

    /// <summary>The durable scope key for a new global-session draft.</summary>
    public const string GlobalDraftScopeKey = "__draft__:global";

    /// <summary>Creates a store without reading or creating directories.</summary>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentException">The global root is blank.</exception>
    public PromptDraftStore(CatalogOptions options)
        : this(options, new TextFileCodec())
    {
    }

    /// <summary>Creates a store sharing the application's text-file save/delete gate.</summary>
    /// <exception cref="ArgumentNullException">Options or the text-file codec are null.</exception>
    /// <exception cref="ArgumentException">The global root is blank.</exception>
    public PromptDraftStore(CatalogOptions options, TextFileCodec files)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GlobalRoot);
        _root = options.PromptDraftsRoot;
        _files = files;
    }

    /// <summary>Gets a new-session draft's durable key; project drafts also live in the global root.</summary>
    public static string GetDraftScopeKey(string? projectId, bool isGlobal)
        => !isGlobal && !string.IsNullOrWhiteSpace(projectId)
            ? "__draft__:project:" + projectId.Trim()
            : GlobalDraftScopeKey;

    /// <summary>Normalizes a draft scope key, defaulting blank keys to the global draft.</summary>
    public static string NormalizeDraftScopeKey(string? scopeKey)
        => string.IsNullOrWhiteSpace(scopeKey) ? GlobalDraftScopeKey : scopeKey.Trim();

    /// <summary>Gets the legacy filename, replacing this OS's invalid filename characters with '-'.</summary>
    /// <exception cref="ArgumentException">The scope key is blank or the path is invalid.</exception>
    public string GetPath(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var invalid = Path.GetInvalidFileNameChars();
        var characters = scopeKey.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
        {
            if (invalid.Contains(characters[i]))
            {
                characters[i] = '-';
            }
        }

        return Path.Combine(_root, $"saved_prompt_{new string(characters)}.md");
    }

    /// <summary>Loads literal text with BOM-aware strict decoding, or a missing snapshot.</summary>
    /// <exception cref="ArgumentException">The scope key or path is invalid.</exception>
    /// <exception cref="IOException">Reading failed for a reason other than absence.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="DecoderFallbackException">The file has invalid Unicode bytes.</exception>
    public PromptDraftSnapshot Load(string scopeKey) => LoadAsync(scopeKey).GetAwaiter().GetResult();

    /// <summary>Loads a draft without cancellation.</summary>
    /// <inheritdoc cref="Load(string)"/>
    public Task<PromptDraftSnapshot> LoadAsync(string scopeKey) => LoadAsync(scopeKey, CancellationToken.None);

    /// <summary>Loads a draft with cancellation.</summary>
    /// <inheritdoc cref="Load(string)"/>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<PromptDraftSnapshot> LoadAsync(string scopeKey, CancellationToken cancellationToken)
    {
        try
        {
            var file = await _files.LoadAsync(GetPath(scopeKey), cancellationToken).ConfigureAwait(false);
            return new PromptDraftSnapshot(file.Text, file.Revision);
        }
        catch (FileNotFoundException)
        {
            return new PromptDraftSnapshot(null, TextFileRevision.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new PromptDraftSnapshot(null, TextFileRevision.Missing);
        }
    }

    /// <summary>Conditionally saves literal UTF-8 without BOM; null/blank text conditionally deletes.</summary>
    /// <exception cref="ArgumentException">The scope key or path is invalid.</exception>
    /// <exception cref="ArgumentNullException">The expected revision is null.</exception>
    /// <exception cref="IOException">Reading, staging or committing failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied or the target is read-only.</exception>
    /// <exception cref="EncoderFallbackException">The text contains invalid Unicode.</exception>
    public Task<PromptDraftSaveResult> SaveAsync(string scopeKey, string? text, TextFileRevision expectedRevision)
        => SaveAsync(scopeKey, text, expectedRevision, CancellationToken.None);

    /// <summary>Conditionally saves or deletes; cancellation only takes effect before commit.</summary>
    /// <inheritdoc cref="SaveAsync(string, string, TextFileRevision)"/>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    public async Task<PromptDraftSaveResult> SaveAsync(string scopeKey, string? text, TextFileRevision expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        var path = GetPath(scopeKey);
        if (string.IsNullOrWhiteSpace(text))
        {
            var deletion = await _files.DeleteAsync(path, expectedRevision, cancellationToken).ConfigureAwait(false);
            return new PromptDraftSaveResult(
                deletion.IsConflict ? null : new PromptDraftSnapshot(null, deletion.CurrentRevision),
                deletion.CurrentRevision);
        }

        var saved = await _files.SaveAsync(new TextFileSaveRequest(path, text, Encoding.UTF8, false, expectedRevision), cancellationToken).ConfigureAwait(false);
        return new PromptDraftSaveResult(
            saved.IsConflict ? null : new PromptDraftSnapshot(saved.Snapshot.Text, saved.CurrentRevision),
            saved.CurrentRevision);
    }
}

/// <summary>Literal draft text (null when missing) and its raw-byte revision.</summary>
public sealed record PromptDraftSnapshot(string? Text, TextFileRevision Revision);

/// <summary>Reports either a committed snapshot or a conflict, never an uncommitted snapshot.</summary>
/// <param name="Snapshot">Acknowledged text/deletion, or null on conflict.</param>
/// <param name="CurrentRevision">Committed revision on success, or observed conflicting revision.</param>
public sealed record PromptDraftSaveResult(PromptDraftSnapshot? Snapshot, TextFileRevision CurrentRevision)
{
    /// <summary>Gets whether the expected revision did not match and nothing was committed.</summary>
    [MemberNotNullWhen(false, nameof(Snapshot))]
    public bool IsConflict => Snapshot is null;
}
