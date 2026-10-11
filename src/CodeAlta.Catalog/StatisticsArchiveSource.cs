using System.Security.Cryptography;
using CodeAlta.Agent;

namespace CodeAlta.Catalog;

/// <summary>A journal reader for a full Statistics retirement scan under the runtime's native source lease.</summary>
/// <remarks>The runtime MUST hold retirement ownership and its native source lease throughout preparation, validation and deletion.
/// This reader shares write/delete access to coexist with that lease's DELETE handle; it does not provide writer exclusion itself.
/// It validates the catalog identity and full source bytes, never a prefix fingerprint.</remarks>
public sealed class StatisticsArchiveSource : IAsyncDisposable
{
    private readonly ISessionJournalCatalog _catalog;
    private readonly SessionJournalFile _file;
    private readonly FileStream _stream;

    private StatisticsArchiveSource(ISessionJournalCatalog catalog, SessionJournalFile file, FileStream stream)
    {
        _catalog = catalog;
        _file = file;
        _stream = stream;
    }

    /// <summary>Gets the seekable read-only source. The caller must scan it from zero to its complete end.</summary>
    public Stream Stream => _stream;

    /// <summary>Opens a trusted catalog source, sharing access with the caller's native retirement lease.</summary>
    /// <param name="catalog">The owning instance's journal catalog.</param>
    /// <param name="sessionId">The verified session identifier.</param>
    /// <param name="cancellationToken">Cancels catalog lookup.</param>
    /// <returns>The source, or null when absent.</returns>
    /// <exception cref="IOException">The source is linked, locked or changed.</exception>
    public static async ValueTask<StatisticsArchiveSource?> OpenAsync(ISessionJournalCatalog catalog, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var file = await catalog.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        if (file.SessionId != sessionId || !Path.IsPathFullyQualified(file.Path))
        {
            throw new IOException("The journal catalog did not establish the source identity.");
        }

        RejectLinks(file.Path);
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length != file.Length || File.GetLastWriteTimeUtc(file.Path).Ticks != file.LastWriteUtc.UtcTicks)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new IOException("The journal changed while opening the retirement source.");
        }

        return new StatisticsArchiveSource(catalog, file, stream);
    }

    /// <summary>Hashes every byte again and checks catalog metadata against the opened source.</summary>
    /// <param name="scanSha256">The digest computed from the exact bytes consumed by the full scan; null for validation-only reads.</param>
    /// <param name="cancellationToken">Cancels hashing.</param>
    /// <returns>The bound identity, or null when the source changed.</returns>
    public async ValueTask<SessionStatisticsSource?> VerifyAsync(string? scanSha256, CancellationToken cancellationToken = default)
    {
        RejectLinks(_file.Path);
        _stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(_stream, cancellationToken).ConfigureAwait(false));
        var current = await _catalog.GetAsync(_file.SessionId, cancellationToken).ConfigureAwait(false);
        if (current != _file || _stream.Length != _file.Length || File.GetLastWriteTimeUtc(_file.Path).Ticks != _file.LastWriteUtc.UtcTicks || (scanSha256 is not null && scanSha256 != hash))
        {
            return null;
        }

        // Reopen the path: on platforms that permit replacement while an old inode is open, do not validate the old inode alone.
        await using var currentStream = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(currentStream, cancellationToken).ConfigureAwait(false));
        return currentHash == hash && currentStream.Length == _file.Length
            ? new SessionStatisticsSource(_file.SessionId, _file.Path, _file.Length, _file.LastWriteUtc, hash)
            : null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked journal paths cannot be retired.");
            }
        }
    }
}
