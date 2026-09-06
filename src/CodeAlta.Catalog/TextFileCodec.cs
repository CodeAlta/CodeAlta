using System.Security.AccessControl;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// Reads complete Unicode text files and conditionally saves them without normalizing newlines.
/// Share one instance among cooperating editors so their check/commit operations are serialized.
/// Paths are trusted application inputs; this class does not authorize project or renderer access.
/// </summary>
/// <remarks>
/// Saves stage bytes beside the destination before checking its revision again and replacing it.
/// Noncooperating writers (including other instances) can still change the file or a path link
/// between the final check and replacement. This is not cross-process atomic compare-and-swap.
/// </remarks>
public sealed class TextFileCodec
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Loads a workflow document through this codec, retaining its backend path policy.</summary>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="IOException">Reading failed or an observed skill path is linked.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="DecoderFallbackException">The file is not supported Unicode text.</exception>
    public Task<TextFileSnapshot> LoadAsync(TextFileDocument document) => LoadAsync(document, CancellationToken.None);

    /// <summary>Loads a workflow document with cancellation.</summary>
    /// <inheritdoc cref="LoadAsync(TextFileDocument)"/>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public Task<TextFileSnapshot> LoadAsync(TextFileDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePath();
        return LoadAsync(document.FullPath, cancellationToken);
    }

    /// <summary>Conditionally saves workflow text, enforcing backend read-only policy before any I/O.</summary>
    /// <remarks>All frontend saves, including overwrite confirmations, must retain the open document contract.</remarks>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The document is read-only or access is denied.</exception>
    /// <exception cref="IOException">Storage failed or an observed skill path is linked.</exception>
    /// <exception cref="ArgumentException">The encoding/BOM combination is invalid.</exception>
    /// <exception cref="EncoderFallbackException">Text contains invalid Unicode.</exception>
    public Task<TextFileSaveResult> SaveAsync(TextFileDocument document, string text, TextFileSnapshot format, TextFileRevision expectedRevision)
        => SaveAsync(document, text, format, expectedRevision, CancellationToken.None);

    /// <summary>Conditionally saves a workflow document with cancellation before commit.</summary>
    /// <inheritdoc cref="SaveAsync(TextFileDocument, string, TextFileSnapshot, TextFileRevision)"/>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    public Task<TextFileSaveResult> SaveAsync(TextFileDocument document, string text, TextFileSnapshot format, TextFileRevision expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(format);
        if (document.IsReadOnly) throw new UnauthorizedAccessException("The skill document is read-only.");
        document.ValidatePath();
        return SaveAsync(new TextFileSaveRequest(document.FullPath, text, format.Encoding, format.HasByteOrderMark, expectedRevision), cancellationToken);
    }

    /// <summary>Loads a complete file, decoding UTF-8 or BOM-bearing UTF-16/UTF-32 strictly.</summary>
    /// <param name="fullPath">Trusted local file path.</param>
    /// <returns>The decoded file and raw-byte revision.</returns>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="DecoderFallbackException">The bytes are invalid in the detected encoding.</exception>
    public TextFileSnapshot Load(string fullPath) => LoadAsync(fullPath).GetAwaiter().GetResult();

    /// <summary>Loads a complete file without cancellation.</summary>
    /// <inheritdoc cref="Load(string)"/>
    public Task<TextFileSnapshot> LoadAsync(string fullPath) => LoadAsync(fullPath, CancellationToken.None);

    /// <summary>Loads a complete file with cancellation.</summary>
    /// <param name="fullPath">Trusted local file path.</param>
    /// <param name="cancellationToken">Cancels reading and decoding before completion.</param>
    /// <returns>The decoded file and raw-byte revision.</returns>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="DecoderFallbackException">The bytes are invalid in the detected encoding.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<TextFileSnapshot> LoadAsync(string fullPath, CancellationToken cancellationToken)
    {
        var path = ResolveFileLink(fullPath);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (encoding, bomLength) = DetectEncoding(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return new TextFileSnapshot(text, encoding, bomLength != 0, File.GetLastWriteTimeUtc(path), TextFileRevision.FromBytes(bytes));
    }

    /// <summary>Reads raw-byte content identity, returning missing only for an absent file or directory.</summary>
    /// <param name="fullPath">Trusted local file path.</param>
    /// <returns>The current content identity, including any BOM bytes.</returns>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public Task<TextFileRevision> GetRevisionAsync(string fullPath) => GetRevisionAsync(fullPath, CancellationToken.None);

    /// <summary>Reads raw-byte content identity with cancellation.</summary>
    /// <param name="fullPath">Trusted local file path.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The current content identity, or missing.</returns>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public Task<TextFileRevision> GetRevisionAsync(string fullPath, CancellationToken cancellationToken)
        => ReadRevisionAsync(ResolveFileLink(fullPath), cancellationToken);

    /// <summary>Conditionally saves a document, returning conflict without changing the target.</summary>
    /// <param name="request">Complete text, format and expected revision.</param>
    /// <returns>The acknowledged snapshot or a conflict.</returns>
    /// <exception cref="ArgumentNullException">The request or required values are null.</exception>
    /// <exception cref="ArgumentException">The path or encoding/BOM combination is invalid.</exception>
    /// <exception cref="EncoderFallbackException">The text contains invalid Unicode.</exception>
    /// <exception cref="IOException">Reading, staging or replacing the file failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied or the target is read-only.</exception>
    public TextFileSaveResult Save(TextFileSaveRequest request) => SaveAsync(request).GetAwaiter().GetResult();

    /// <summary>Conditionally saves a document without cancellation.</summary>
    /// <inheritdoc cref="Save(TextFileSaveRequest)"/>
    public Task<TextFileSaveResult> SaveAsync(TextFileSaveRequest request) => SaveAsync(request, CancellationToken.None);

    /// <summary>Conditionally saves a document using a same-directory staged file.</summary>
    /// <param name="request">Complete text, format and expected revision.</param>
    /// <param name="cancellationToken">Cancels before commit. Once committed, success is returned even if cancellation follows.</param>
    /// <returns>The acknowledged snapshot or a conflict; no automatic retry overwrites conflicting content.</returns>
    /// <exception cref="ArgumentNullException">The request or required values are null.</exception>
    /// <exception cref="ArgumentException">The path or encoding/BOM combination is invalid.</exception>
    /// <exception cref="EncoderFallbackException">The text contains invalid Unicode.</exception>
    /// <exception cref="IOException">Reading, staging or replacing the file failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied or the target is read-only.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    public async Task<TextFileSaveResult> SaveAsync(TextFileSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        ArgumentNullException.ThrowIfNull(request.Encoding);
        ArgumentNullException.ThrowIfNull(request.ExpectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveFileLink(request.FullPath);
        var encoding = ResolveEncoding(request.Encoding, request.HasByteOrderMark);
        var preamble = encoding.GetPreamble();
        var bytes = new byte[checked(preamble.Length + encoding.GetByteCount(request.Text))];
        preamble.CopyTo(bytes, 0);
        encoding.GetBytes(request.Text, bytes.AsSpan(preamble.Length));
        var revision = TextFileRevision.FromBytes(bytes);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingPath = null;
        try
        {
            var current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
            if (current != request.ExpectedRevision)
            {
                return new TextFileSaveResult(null, current);
            }

            var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("A file path with a parent directory is required.", nameof(request));
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, $".codealta-save-{Guid.NewGuid():N}.tmp");
            await using (var stream = CreateStagingFile(candidate, current.Exists ? path : null))
            {
                stagingPath = candidate; // Only remove a staging file created by this operation.
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
            if (current != request.ExpectedRevision)
            {
                return new TextFileSaveResult(null, current);
            }

            if (current.Exists)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                {
                    throw new UnauthorizedAccessException("The text file is read-only.");
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stagingPath, File.GetUnixFileMode(path));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The owner gate covers cooperating saves, not external editors or path-link changes.
            if (current.Exists)
            {
                File.Replace(stagingPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(stagingPath, path); // A concurrent creation must never be overwritten.
            }

            stagingPath = null;
            var snapshot = new TextFileSnapshot(request.Text, encoding, request.HasByteOrderMark, File.GetLastWriteTimeUtc(path), revision);
            return new TextFileSaveResult(snapshot, revision);
        }
        finally
        {
            try
            {
                if (stagingPath is not null)
                {
                    File.Delete(stagingPath);
                }
            }
            finally
            {
                _saveGate.Release();
            }
        }
    }

    /// <summary>Conditionally deletes a file entry under the same instance gate used for saves.</summary>
    /// <remarks>
    /// Revisions describe the bytes read through the path, but deletion unlinks the final entry,
    /// not a symbolic link's target. Dangling links can be deleted with the missing revision.
    /// External writers can still race between the revision check and deletion.
    /// </remarks>
    /// <exception cref="ArgumentException">The path is blank or invalid.</exception>
    /// <exception cref="ArgumentNullException">The expected revision is null.</exception>
    /// <exception cref="IOException">Reading or deleting failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public Task<TextFileDeleteResult> DeleteAsync(string fullPath, TextFileRevision expectedRevision)
        => DeleteAsync(fullPath, expectedRevision, CancellationToken.None);

    /// <summary>Conditionally deletes; cancellation takes effect only before commit.</summary>
    /// <inheritdoc cref="DeleteAsync(string, TextFileRevision)"/>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    public async Task<TextFileDeleteResult> DeleteAsync(string fullPath, TextFileRevision expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var path = Path.GetFullPath(fullPath);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
            if (current != expectedRevision)
            {
                return new TextFileDeleteResult(true, current);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (current.Exists || new FileInfo(path).LinkTarget is not null)
            {
                File.Delete(path);
            }

            return new TextFileDeleteResult(false, TextFileRevision.Missing);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    internal static FileStream CreateStagingFile(string stagingPath, string? existingPath)
    {
        if (OperatingSystem.IsWindows() && existingPath is not null)
        {
            var security = new FileInfo(existingPath).GetAccessControl(AccessControlSections.Access);
            // Supply the effective original DACL at creation, before any edited bytes exist.
            // Prevent additional directory grants from broadening it while the closed staging
            // file awaits replacement. If reading/applying the DACL fails, do not fall back.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            return new FileInfo(stagingPath).Create(
                FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
                FileOptions.Asynchronous, security);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            // Do not expose a restrictive original's contents through a more permissive temporary file.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(stagingPath, options);
    }

    /// <summary>
    /// Resolves a trusted attached-file path: absolute paths are retained; relative paths use
    /// the first existing candidate, then the first nonblank root, then the current directory.
    /// This lookup deliberately permits external paths and is not project authorization.
    /// </summary>
    /// <param name="path">Absolute or relative file path.</param>
    /// <param name="rootCandidates">Ordered working-directory/project-root candidates.</param>
    /// <returns>The absolute resolved path.</returns>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="ArgumentNullException">The roots are null.</exception>
    public static string ResolvePath(string path, IReadOnlyList<string> rootCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rootCandidates);
        var normalized = path.Trim();
        if (Path.IsPathFullyQualified(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        foreach (var root in rootCandidates)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                var candidate = Path.GetFullPath(Path.Combine(root, normalized));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var fallback = rootCandidates.FirstOrDefault(static root => !string.IsNullOrWhiteSpace(root)) ?? Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(fallback, normalized));
    }

    private static string ResolveFileLink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        // Replace the linked file, not the symbolic link itself, matching ordinary file writes.
        var info = new FileInfo(fullPath);
        return info.LinkTarget is null ? fullPath : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
    }

    private static async Task<TextFileRevision> ReadRevisionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return TextFileRevision.FromBytes(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return TextFileRevision.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return TextFileRevision.Missing;
        }
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
        => bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(true, true), 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (new UTF32Encoding(false, true, true), 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => (new UTF32Encoding(true, true, true), 4),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, true, true), 2),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, true, true), 2),
            _ => (new UTF8Encoding(false, true), 0),
        };

    private static Encoding ResolveEncoding(Encoding encoding, bool bom)
        => encoding.CodePage switch
        {
            65001 => new UTF8Encoding(bom, true),
            1200 when bom => new UnicodeEncoding(false, true, true),
            1201 when bom => new UnicodeEncoding(true, true, true),
            12000 when bom => new UTF32Encoding(false, true, true),
            12001 when bom => new UTF32Encoding(true, true, true),
            _ => throw new ArgumentException("Text files require UTF-8, or UTF-16/UTF-32 with a BOM.", nameof(encoding)),
        };
}
