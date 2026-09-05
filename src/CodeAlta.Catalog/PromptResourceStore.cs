using System.Text;

namespace CodeAlta.Catalog;

/// <summary>Prompt resource storage scope; built-in resources are always read-only.</summary>
public enum PromptResourceScope
{
    /// <summary>Shipped content.</summary>
    BuiltIn,
    /// <summary>User-global overrides.</summary>
    Global,
    /// <summary>Explicit project overrides.</summary>
    Project,
}

/// <summary>File-backed prompt resource kind.</summary>
public enum PromptResourceKind
{
    /// <summary>Agent metadata and instructions.</summary>
    Agent,
    /// <summary>Base system instructions.</summary>
    System,
}

/// <summary>Scoped file id; not a renderer authorization grant.</summary>
public sealed record PromptResourceIdentity(PromptResourceScope Scope, PromptResourceKind Kind, string Id);

/// <summary>File-local editable values and their matching byte/format snapshot.</summary>
public sealed class PromptResourceSnapshot
{
    internal PromptResourceSnapshot(PromptResourceStore owner, PromptResourceIdentity identity, PromptFileContent content, TextFileSnapshot file)
        => (Owner, Identity, Content, File) = (owner, identity, content, file);
    internal PromptResourceStore Owner { get; }
    /// <summary>Gets the scope/kind/id loaded by the owner.</summary>
    public PromptResourceIdentity Identity { get; }
    /// <summary>Gets values parsed from the same bytes as File.Revision.</summary>
    public PromptFileContent Content { get; }
    /// <summary>Gets literal text, encoding and raw-byte revision.</summary>
    public TextFileSnapshot File { get; }
}

/// <summary>
/// Trusted-backend CRUD over explicit prompt roots. Does not discover or assemble runtime prompts.
/// Share the application's text codec with overlapping editors. No raw path is a renderer grant.
/// </summary>
/// <remarks>
/// Rejects symbolic links/reparse points in every existing path component, including roots and final files.
/// This is a conservative editing policy, not a sandbox: a noncooperating process can race path checks
/// or the codec's final revision check. There is no cross-process atomic compare-and-swap.
/// </remarks>
public sealed class PromptResourceStore
{
    private readonly string _builtInRoot;
    private readonly string _globalRoot;
    private readonly string? _projectRoot;
    private readonly TextFileCodec _textFiles;

    /// <summary>Creates a store with explicit content/prompts and global/project prompts roots.</summary>
    /// <exception cref="ArgumentException">Roots are relative, empty or overlapping.</exception>
    /// <exception cref="ArgumentNullException">The codec is null.</exception>
    public PromptResourceStore(string builtInPromptRoot, string globalPromptRoot, string? projectPromptRoot, TextFileCodec textFiles)
    {
        ArgumentNullException.ThrowIfNull(textFiles);
        _builtInRoot = NormalizeRoot(builtInPromptRoot);
        _globalRoot = NormalizeRoot(globalPromptRoot);
        _projectRoot = projectPromptRoot is null ? null : NormalizeRoot(projectPromptRoot);
        var roots = new[] { _builtInRoot, _globalRoot, _projectRoot }.OfType<string>().ToArray();
        for (var i = 0; i < roots.Length; i++)
            for (var j = i + 1; j < roots.Length; j++)
                if (Contains(roots[i], roots[j]) || Contains(roots[j], roots[i]))
                    throw new ArgumentException("Prompt roots must be separate and non-overlapping.");
        _textFiles = textFiles;
    }

    /// <summary>Validates a portable single-component file/system id (without extension).</summary>
    /// <exception cref="ArgumentException">Id is empty, unsafe or a reserved device name.</exception>
    public static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or ".." || id.EndsWith('.') || id.Any(static ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
            throw new ArgumentException("Invalid prompt file id.", nameof(id));
        var stem = id.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new ArgumentException("Reserved prompt file id.", nameof(id));
    }

    /// <summary>Resolves a validated identity to its configured root, without filesystem access.</summary>
    /// <exception cref="ArgumentException">Identity, scope, kind or project root is invalid.</exception>
    /// <exception cref="ArgumentNullException">Identity is null.</exception>
    public string GetPath(PromptResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateId(identity.Id);
        return Path.Combine(GetDirectory(identity.Scope, identity.Kind), identity.Id + Suffix(identity.Kind));
    }

    /// <summary>Gets the configured agent/system directory; project scope never falls back to global.</summary>
    /// <exception cref="ArgumentException">Scope, kind or project root is invalid.</exception>
    public string GetDirectory(PromptResourceScope scope, PromptResourceKind kind)
    {
        var root = scope switch
        {
            PromptResourceScope.BuiltIn => _builtInRoot,
            PromptResourceScope.Global => _globalRoot,
            PromptResourceScope.Project when _projectRoot is not null => _projectRoot,
            _ => throw new ArgumentException("Invalid or unavailable prompt scope.", nameof(scope)),
        };
        return Path.Combine(root, kind switch { PromptResourceKind.Agent => "agents", PromptResourceKind.System => "system", _ => throw new ArgumentException("Invalid prompt kind.", nameof(kind)) });
    }

    /// <summary>Checks discovery path provenance against a claimed scope/kind and returns its identity.</summary>
    /// <exception cref="ArgumentException">Path is not an absolute direct child with the expected extension/id.</exception>
    public PromptResourceIdentity Identify(PromptResourceScope scope, PromptResourceKind kind, string sourcePath)
    {
        if (!Path.IsPathFullyQualified(sourcePath)) throw new ArgumentException("An absolute prompt source path is required.", nameof(sourcePath));
        var suffix = Suffix(kind);
        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Wrong prompt extension.", nameof(sourcePath));
        var identity = new PromptResourceIdentity(scope, kind, fileName[..^suffix.Length]);
        if (!string.Equals(GetPath(identity), sourcePath, PathComparison)) throw new ArgumentException("Prompt source does not match its configured scope/root.", nameof(sourcePath));
        return identity;
    }

    /// <summary>Loads editable fields and revision from one strict decoded snapshot.</summary>
    /// <exception cref="ArgumentException">Identity or file metadata is invalid.</exception>
    /// <exception cref="IOException">File is missing or unreadable.</exception>
    /// <exception cref="UnauthorizedAccessException">Path contains links or access is denied.</exception>
    /// <exception cref="DecoderFallbackException">File encoding is invalid.</exception>
    public PromptResourceSnapshot Load(PromptResourceIdentity identity)
    {
        var path = CheckedPath(identity, writable: false);
        var file = _textFiles.Load(path);
        return new PromptResourceSnapshot(this, identity, PromptFileFormat.Parse(identity.Kind, file.Text), file);
    }

    /// <summary>Creates a UTF-8 resource without overwriting an existing same-scope file.</summary>
    /// <exception cref="ArgumentException">Identity or content is invalid.</exception>
    /// <exception cref="ArgumentNullException">Identity or content is null.</exception>
    /// <exception cref="IOException">Reading/staging/commit fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Built-in scope, links or access denial.</exception>
    /// <exception cref="EncoderFallbackException">Content has invalid Unicode.</exception>
    public TextFileSaveResult Create(PromptResourceIdentity identity, PromptFileContent content)
        => _textFiles.Save(new TextFileSaveRequest(CheckedPath(identity, writable: true), PromptFileFormat.Serialize(identity.Kind, content), new UTF8Encoding(false, true), false, TextFileRevision.Missing));

    /// <summary>Saves only against the loaded byte revision.</summary>
    /// <inheritdoc cref="Save(PromptResourceSnapshot, PromptFileContent, TextFileRevision)"/>
    public TextFileSaveResult Save(PromptResourceSnapshot snapshot, PromptFileContent content)
    {
        CheckOwner(snapshot);
        return Save(snapshot, content, snapshot.File.Revision);
    }

    /// <summary>Saves against an explicitly observed/confirmed revision, retaining loaded encoding.</summary>
    /// <exception cref="ArgumentException">Snapshot belongs to another store or values are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="IOException">Reading/staging/commit fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Built-in scope, links or access denial.</exception>
    /// <exception cref="EncoderFallbackException">Content has invalid Unicode.</exception>
    public TextFileSaveResult Save(PromptResourceSnapshot snapshot, PromptFileContent content, TextFileRevision expectedRevision)
        => SaveAsync(snapshot, content, expectedRevision, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Saves conditionally with cancellation before commit; never automatically retries.</summary>
    /// <inheritdoc cref="Save(PromptResourceSnapshot, PromptFileContent, TextFileRevision)"/>
    /// <exception cref="OperationCanceledException">Canceled before commit.</exception>
    public Task<TextFileSaveResult> SaveAsync(PromptResourceSnapshot snapshot, PromptFileContent content, TextFileRevision expectedRevision, CancellationToken cancellationToken)
    {
        CheckOwner(snapshot);
        return _textFiles.SaveAsync(new TextFileSaveRequest(CheckedPath(snapshot.Identity, writable: true), PromptFileFormat.Serialize(snapshot.Identity.Kind, content), snapshot.File.Encoding, snapshot.File.HasByteOrderMark, expectedRevision), cancellationToken);
    }

    /// <summary>Deletes only against the loaded byte revision.</summary>
    /// <inheritdoc cref="Delete(PromptResourceSnapshot, TextFileRevision)"/>
    public TextFileDeleteResult Delete(PromptResourceSnapshot snapshot)
    {
        CheckOwner(snapshot);
        return Delete(snapshot, snapshot.File.Revision);
    }

    /// <summary>Deletes against an explicitly observed/confirmed revision.</summary>
    /// <exception cref="ArgumentException">Snapshot belongs to another store.</exception>
    /// <exception cref="ArgumentNullException">Snapshot or revision is null.</exception>
    /// <exception cref="IOException">Reading/deleting fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Built-in scope, links or access denial.</exception>
    public TextFileDeleteResult Delete(PromptResourceSnapshot snapshot, TextFileRevision expectedRevision)
    {
        CheckOwner(snapshot);
        return _textFiles.DeleteAsync(CheckedPath(snapshot.Identity, writable: true), expectedRevision).GetAwaiter().GetResult();
    }

    /// <summary>Builds an editing snapshot from an acknowledged save without an invisible reread.</summary>
    /// <exception cref="ArgumentException">Result is a conflict or its content is invalid.</exception>
    /// <exception cref="ArgumentNullException">Identity or result is null.</exception>
    public PromptResourceSnapshot Acknowledge(PromptResourceIdentity identity, TextFileSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _ = GetPath(identity);
        if (result.IsConflict) throw new ArgumentException("Cannot acknowledge a conflicting save.", nameof(result));
        return new PromptResourceSnapshot(this, identity, PromptFileFormat.Parse(identity.Kind, result.Snapshot.Text), result.Snapshot);
    }

    private void CheckOwner(PromptResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.Owner, this)) throw new ArgumentException("Prompt snapshot belongs to another root owner.", nameof(snapshot));
    }

    private string CheckedPath(PromptResourceIdentity identity, bool writable)
    {
        var path = GetPath(identity);
        if (writable && identity.Scope == PromptResourceScope.BuiltIn) throw new UnauthorizedAccessException("Built-in prompts are read-only.");
        for (string? component = path; component is not null; component = Path.GetDirectoryName(component))
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Prompt management does not edit linked paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    private static string Suffix(PromptResourceKind kind) => kind switch { PromptResourceKind.Agent => ".prompt.md", PromptResourceKind.System => ".system-prompt.md", _ => throw new ArgumentException("Invalid prompt kind.", nameof(kind)) };
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool Contains(string root, string path)
        => string.Equals(root, path, PathComparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Prompt roots must be absolute.", nameof(root));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
