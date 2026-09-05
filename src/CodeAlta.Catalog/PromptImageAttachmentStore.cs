using System.Globalization;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>Persists prompt image copies beside date-sharded session journals without overwriting existing entries.</summary>
/// <remarks>
/// This is a trusted backend filesystem API, not a sandbox or renderer RPC authorization boundary.
/// External replacement of directories, symlinks or newly created files can race writes and rollback.
/// A failed batch attempts to remove only files it created; filesystem failures can prevent cleanup.
/// Successful saves remain owned by the session even if subsequent dispatch fails or the composer clears.
/// </remarks>
public sealed class PromptImageAttachmentStore
{
    private readonly string _sessionsRoot;
    private readonly TimeProvider _clock;
    private readonly Func<Stream, ReadOnlyMemory<byte>, CancellationToken, Task> _writeAsync;

    /// <summary>Creates a store using the configured global catalog root.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalogOptions"/> is null.</exception>
    /// <exception cref="ArgumentException">The global root is empty or an invalid path.</exception>
    /// <exception cref="NotSupportedException">The root uses an unsupported path format.</exception>
    /// <exception cref="PathTooLongException">The root exceeds platform path limits.</exception>
    public PromptImageAttachmentStore(CatalogOptions catalogOptions)
        : this(catalogOptions, TimeProvider.System, null)
    {
    }

    internal PromptImageAttachmentStore(
        CatalogOptions catalogOptions,
        TimeProvider clock,
        Func<Stream, ReadOnlyMemory<byte>, CancellationToken, Task>? writeAsync)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogOptions.GlobalRoot);
        ArgumentNullException.ThrowIfNull(clock);
        _sessionsRoot = Path.Combine(Path.GetFullPath(catalogOptions.GlobalRoot), "sessions");
        _clock = clock;
        _writeAsync = writeAsync ?? (static (stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
    }

    /// <summary>Saves a validated batch of encoded image copies, returning their display titles, paths and MIME types.</summary>
    /// <remarks>
    /// Filenames retain the UTC timestamp, batch index, sanitized title and first eight ID characters.
    /// Atomic create-new retries only native already-exists errors, using suffixes -2 through -1000.
    /// Payload validation completes before creating directories. No image decoding or media sniffing is performed.
    /// Callers must not mutate the supplied batch or byte arrays until the operation completes.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The session, batch, or a required payload field is null.</exception>
    /// <exception cref="ArgumentException">A payload is empty/malformed or the session ID has no usable filename component.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; owned files are removed on a best-effort basis.</exception>
    /// <exception cref="IOException">Filesystem creation/writing fails, a path is too long, or all 1000 candidate names exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem denies access.</exception>
    public async Task<IReadOnlyList<PromptImageAttachmentReference>> SaveAsync(
        SessionViewDescriptor session,
        IReadOnlyList<PromptImageAttachment> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(images);
        cancellationToken.ThrowIfCancellationRequested();
        if (images.Count == 0)
        {
            return [];
        }

        var directory = GetAttachmentDirectory(session);
        var names = new string[images.Count];
        for (var index = 0; index < images.Count; index++)
        {
            names[index] = CreateAttachmentFileName(images[index], index + 1);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var ownedPaths = new List<string>(images.Count);
        var references = new List<PromptImageAttachmentReference>(images.Count);
        try
        {
            for (var index = 0; index < images.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = images[index];
                var (path, stream) = CreateNewFile(directory, names[index], cancellationToken);
                ownedPaths.Add(path);
                await using (stream.ConfigureAwait(false))
                {
                    await _writeAsync(stream, image.Bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                references.Add(new PromptImageAttachmentReference(image.Title, path, image.MediaType));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return references;
        }
        catch
        {
            foreach (var path in ownedPaths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException) { } // Preserve the original error; external changes can prevent rollback.
                catch (UnauthorizedAccessException) { }
            }

            throw;
        }
    }

    internal string GetAttachmentDirectory(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var createdAt = session.CreatedAt == default ? _clock.GetUtcNow() : session.CreatedAt;
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        var segment = SanitizeFileName(session.SessionId);
        if (segment.Length is 0 or > 200)
        {
            throw new ArgumentException("Session ID must yield a filename component of 1–200 characters.", nameof(session));
        }

        return Path.Combine(
            _sessionsRoot,
            createdAt.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("MM", CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("dd", CultureInfo.InvariantCulture),
            segment + ".attachments");
    }

    private string CreateAttachmentFileName(PromptImageAttachment image, int index)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(image.Id);
        if (image.Id.Length > 128 || image.Id.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new ArgumentException("Image ID must contain 1–128 ASCII letters, digits, hyphens or underscores.", nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(image.Title);
        ArgumentNullException.ThrowIfNull(image.Bytes);
        if (image.Bytes.Length == 0)
        {
            throw new ArgumentException("Image bytes must not be empty.", nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(image.MediaType);
        if (!image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || image.MediaType.Length <= 6 ||
            image.MediaType.Length > 128 || image.MediaType.AsSpan(6).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$&^_.+-"))
        {
            throw new ArgumentException("An image MIME type without parameters or control characters is required.", nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(image.FileExtension);
        if (image.FileExtension.Any(char.IsControl))
        {
            throw new ArgumentException("Image extension must not contain control characters.", nameof(image));
        }

        var extension = image.FileExtension.Trim();
        if (extension.StartsWith('.'))
        {
            extension = extension[1..];
        }

        if (extension.Length is 0 or > 16 || extension.Any(static ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            throw new ArgumentException("Image extension must contain 1–16 ASCII letters or digits, optionally preceded by a dot.", nameof(image));
        }

        var title = SanitizeFileName(image.Title);
        if (title.Length > 48)
        {
            title = title[..48].TrimEnd('.', ' ', '_', '-');
        }

        if (title.Length == 0)
        {
            title = "image";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{_clock.GetUtcNow():yyyyMMddHHmmssfff}-{index:00}-{title}-{image.Id[..Math.Min(8, image.Id.Length)]}.{extension}");
    }

    private static (string Path, FileStream Stream) CreateNewFile(string directory, string name, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = attempt == 1 ? name : string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(name)}-{attempt}{Path.GetExtension(name)}");
            var path = Path.Combine(directory, candidate);
            try
            {
                return (path, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous));
            }
            catch (IOException ex) when (IsNameCollision(ex))
            {
                // Only the atomic open may trigger retries. Write/flush failures must roll back instead.
            }
        }

        throw new IOException("All 1000 prompt attachment filename candidates already exist.");
    }

    private static bool IsNameCollision(IOException exception)
        => OperatingSystem.IsWindows()
            ? (exception.HResult & 0xffff) is 80 or 183 // ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS
            : exception.HResult == 17; // EEXIST returned by the Unix FileStream implementation

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            // Apply both platforms' separators/invalid characters even when running on Unix.
            builder.Append(char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) || Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        }

        return builder.ToString().Trim('.', ' ', '_');
    }
}
