using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>
/// The files of a session bundle: its manifest, the hashes of its files, and its packing into one archive stream for a
/// transfer.
/// </summary>
public static class SessionBundle
{
    private const int BufferSize = 81920;

    /// <summary>Writes the manifest of a bundle, with the list of its files (<see cref="SessionMoveManifest.Files"/>) made from the folder.</summary>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="manifest">The manifest; its list of files is replaced.</param>
    /// <param name="cancellationToken">Stops the hashing.</param>
    /// <returns>The manifest as written.</returns>
    /// <exception cref="IOException">A file cannot be read or the manifest written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<SessionMoveManifest> WriteManifestAsync(string bundleFolder, SessionMoveManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ArgumentNullException.ThrowIfNull(manifest);
        var root = Path.GetFullPath(bundleFolder);
        var files = new List<SessionMoveFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == SessionMoveManifest.FileName) continue;
            files.Add(await HashFileAsync(path, relative, cancellationToken).ConfigureAwait(false));
        }

        var written = manifest with { Files = files };
        await File.WriteAllBytesAsync(Path.Combine(root, SessionMoveManifest.FileName),
            JsonSerializer.SerializeToUtf8Bytes(written, SessionMoveJsonContext.Default.SessionMoveManifest), cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>Reads the manifest of a bundle.</summary>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The manifest; <see langword="null"/> when there is none or it cannot be read.</returns>
    public static async Task<SessionMoveManifest?> ReadManifestAsync(string bundleFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        var path = Path.Combine(bundleFolder, SessionMoveManifest.FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), SessionMoveJsonContext.Default.SessionMoveManifest)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a bundle against its manifest: every listed file is in the folder with its length and its hash, nothing
    /// else is, and the journal is the one the manifest names.
    /// </summary>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="manifest">Its manifest.</param>
    /// <param name="cancellationToken">Stops the hashing.</param>
    /// <returns><see langword="null"/> when the bundle is whole; otherwise what is wrong, in words.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<string?> VerifyAsync(string bundleFolder, SessionMoveManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Format != SessionMoveManifest.CurrentFormat) return $"The bundle has the format {manifest.Format}, which this version does not read.";
        var root = Path.GetFullPath(bundleFolder);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in manifest.Files)
            {
                var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!GitWorktreeService.IsWithin(path, root) || !listed.Add(file.Path)) return $"The bundle names the file '{file.Path}' it cannot have.";
                if (!File.Exists(path)) return $"The file '{file.Path}' of the bundle is missing.";
                var actual = await HashFileAsync(path, file.Path, cancellationToken).ConfigureAwait(false);
                if (actual.Length != file.Length || !string.Equals(actual.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"The file '{file.Path}' of the bundle is not the one that was sent.";
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative != SessionMoveManifest.FileName && !listed.Contains(relative)) return $"The bundle has the file '{relative}' its manifest does not list.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The bundle cannot be read: {exception.Message}";
        }

        var journal = manifest.Files.FirstOrDefault(static file => file.Path == SessionMoveManifest.JournalFile);
        return journal is null || journal.Length != manifest.Journal.Length || !string.Equals(journal.Sha256, manifest.Journal.Sha256, StringComparison.OrdinalIgnoreCase)
            ? "The journal of the bundle is not the one its manifest names."
            : null;
    }

    /// <summary>Copies a file into a bundle and hashes it as it is copied.</summary>
    /// <param name="source">The file.</param>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="relative">Its path in the bundle, with forward slashes.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    /// <returns>The file of the bundle.</returns>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<SessionMoveFile> CopyIntoAsync(string source, string bundleFolder, string relative, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var target = Path.Combine(bundleFolder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length += read;
            }
        }

        return new SessionMoveFile(relative, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Copies a folder into a bundle, recursively.</summary>
    /// <param name="source">The folder; nothing is copied when it does not exist.</param>
    /// <param name="target">The folder it is copied to.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    /// <returns>A task that completes once the files are copied.</returns>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task CopyFolderAsync(string source, string target, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous);
            await using var output = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
            await CopyFolderAsync(folder, Path.Combine(target, Path.GetFileName(folder)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Packs a bundle folder into one archive stream (zip, stored as it is), for a transfer.</summary>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="destination">The stream the archive is written to.</param>
    /// <param name="cancellationToken">Stops the packing.</param>
    /// <returns>A task that completes once the archive is written.</returns>
    /// <exception cref="IOException">A file cannot be read, or the stream written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task PackAsync(string bundleFolder, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ArgumentNullException.ThrowIfNull(destination);
        var root = Path.GetFullPath(bundleFolder);
        await using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), CompressionLevel.NoCompression);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using var output = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Unpacks an archive made by <see cref="PackAsync"/> into an empty folder, within <see cref="SessionBundleLimits.Default"/>;
    /// an entry that would leave the folder is refused.
    /// </summary>
    /// <param name="source">The archive.</param>
    /// <param name="bundleFolder">The folder the bundle is written to; created when missing.</param>
    /// <param name="cancellationToken">Stops the unpacking.</param>
    /// <returns>A task that completes once the files are written.</returns>
    /// <exception cref="InvalidDataException">The archive is not valid, names a path outside the folder, or goes past a limit.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static Task UnpackAsync(Stream source, string bundleFolder, CancellationToken cancellationToken)
        => UnpackAsync(source, bundleFolder, SessionBundleLimits.Default, cancellationToken);

    /// <summary>
    /// Unpacks an archive made by <see cref="PackAsync"/> into a folder that does not exist or is empty, within limits
    /// counted on the bytes written; an entry that would leave the folder is refused. When the unpacking fails, what it
    /// wrote goes.
    /// </summary>
    /// <param name="source">The archive.</param>
    /// <param name="bundleFolder">The folder the bundle is written to; created when missing.</param>
    /// <param name="limits">The most entries and bytes the archive may unpack to.</param>
    /// <param name="cancellationToken">Stops the unpacking.</param>
    /// <returns>A task that completes once the files are written.</returns>
    /// <exception cref="InvalidDataException">The archive is not valid, names a path outside the folder, or goes past a limit.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task UnpackAsync(Stream source, string bundleFolder, SessionBundleLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ArgumentNullException.ThrowIfNull(limits);
        var created = !Directory.Exists(bundleFolder);
        var root = Directory.CreateDirectory(bundleFolder).FullName;
        try
        {
            await using var archive = await ZipArchive.CreateAsync(source, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
            // The count of the central directory is read before anything is written.
            if (archive.Entries.Count > limits.MaximumEntries)
                throw new InvalidDataException($"The archive has {archive.Entries.Count} entries, more than the {limits.MaximumEntries} a bundle may have.");
            var buffer = new byte[BufferSize];
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                var path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!GitWorktreeService.IsWithin(path, root) || path == root) throw new InvalidDataException($"The archive names the path '{entry.FullName}' outside the bundle.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                // The lengths the archive gives of its entries are not trusted: the bytes are counted as they come.
                long length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                    total += read;
                    if (length > limits.MaximumEntryBytes)
                        throw new InvalidDataException($"The entry '{entry.FullName}' unpacks to more than the {limits.MaximumEntryBytes} bytes a file of a bundle may have.");
                    if (total > limits.MaximumTotalBytes)
                        throw new InvalidDataException($"The archive unpacks to more than the {limits.MaximumTotalBytes} bytes a bundle may have.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            RemoveUnpacked(root, created);
            throw;
        }
    }

    // What an unpacking that failed wrote goes: the folder when it made it, its content otherwise (it was empty).
    private static void RemoveUnpacked(string root, bool created)
    {
        try
        {
            if (created)
            {
                Directory.Delete(root, recursive: true);
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(root)) Directory.Delete(folder, recursive: true);
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What is left is refused again by the verification of the bundle, and its folder is removed with the move.
        }
    }

    private static async Task<SessionMoveFile> HashFileAsync(string path, string relative, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new SessionMoveFile(relative, stream.Length, Convert.ToHexStringLower(hash));
    }
}
