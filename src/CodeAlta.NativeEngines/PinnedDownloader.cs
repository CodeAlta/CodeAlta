using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.NativeEngines;

/// <summary>A file whose size and hash are known before it is downloaded.</summary>
/// <param name="Name">The name the file gets in its folder.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Sha256">Its SHA-256 in lowercase hexadecimal, or null.</param>
/// <param name="GitSha1">The SHA-1 git names it by as a blob, in lowercase hexadecimal, or null. Used when <paramref name="Sha256"/> is null.</param>
public sealed record PinnedFile(string Name, long Size, string? Sha256, string? GitSha1 = null);

/// <summary>A download that ends with a reason the user is told.</summary>
/// <param name="code"><c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c> or <c>disk</c>.</param>
public sealed class PinnedDownloadException(string code) : Exception(code)
{
    /// <summary>Gets the reason.</summary>
    public string Code { get; } = code;
}

/// <summary>What a download reports while it runs.</summary>
public interface IPinnedDownloadProgress
{
    /// <summary>Bytes of a file are on disk: those found from an earlier attempt, then those received.</summary>
    /// <param name="bytes">The bytes added.</param>
    void Received(long bytes);

    /// <summary>A file is complete and its hash is being checked.</summary>
    /// <param name="file">The file.</param>
    void Checking(PinnedFile file);
}

/// <summary>
/// Downloads pinned files: written as <c>name.part</c>, resumed with an HTTP range where it stopped, checked
/// against its hash and only then given its name. A folder of files is installed in a <c>.partial</c> folder
/// that gets its own name once its marker file is written, so a folder under its name is complete; a lock in the
/// <c>.partial</c> folder keeps two instances of CodeAlta from writing the same files.
/// </summary>
public sealed class PinnedDownloader : IDisposable
{
    /// <summary>A read that gets nothing for this long fails the download.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    /// <summary>Creates a downloader.</summary>
    /// <param name="handler">The handler of its requests, or null for the network.</param>
    /// <param name="userAgent">The product it names itself with, or null.</param>
    public PinnedDownloader(HttpMessageHandler? handler, ProductInfoHeaderValue? userAgent)
    {
        // A download lasts as long as the connection allows: what bounds it is a read that gets nothing.
        _http = new(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        if (userAgent is not null) _http.DefaultRequestHeaders.UserAgent.Add(userAgent);
    }

    /// <summary>Reads a small document, such as the listing of a repository.</summary>
    /// <param name="address">Its address.</param>
    /// <param name="maximumBytes">The most it may hold.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="PinnedDownloadException"><c>not_found</c>, <c>network</c>, or <c>too_large</c> beyond <paramref name="maximumBytes"/>.</exception>
    public async Task<byte[]> ReadAsync(string address, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StallTimeout);
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new PinnedDownloadException("not_found");
        if (!response.IsSuccessStatusCode) throw new PinnedDownloadException("network");
        if (response.Content.Headers.ContentLength > maximumBytes) throw new PinnedDownloadException("too_large");
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return bytes.Length > maximumBytes ? throw new PinnedDownloadException("too_large") : bytes;
    }

    /// <summary>
    /// Installs a folder of files: downloads each into <c><paramref name="target"/>.partial</c>, lets
    /// <paramref name="finish"/> work on that folder, writes the marker, then gives the folder its name.
    /// </summary>
    /// <param name="target">The folder once installed.</param>
    /// <param name="marker">The name of the file whose presence says the folder is complete.</param>
    /// <param name="files">The files and their addresses, downloaded in this order.</param>
    /// <param name="finish">What to do in the partial folder once every file is there (unpack an archive), or null.</param>
    /// <param name="markerContent">The content of the marker.</param>
    /// <param name="progress">Where the progress goes.</param>
    /// <param name="cancellationToken">Ends the download; what is on disk stays for the next attempt.</param>
    /// <exception cref="PinnedDownloadException">The download failed; its code says why.</exception>
    public async Task InstallFolderAsync(string target, string marker, IReadOnlyList<(PinnedFile File, Uri Address)> files,
        Func<string, CancellationToken, Task>? finish, byte[] markerContent, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(markerContent);
        ArgumentNullException.ThrowIfNull(progress);
        var partial = target + ".partial";
        Directory.CreateDirectory(partial);
        FileStream guard;
        try { guard = new(Path.Combine(partial, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new PinnedDownloadException("busy_elsewhere"); }
        await using (guard.ConfigureAwait(false))
        {
            // A folder under its name without its marker is what a removal left: it is not an installation.
            if (Directory.Exists(target) && !File.Exists(Path.Combine(target, marker))) Directory.Delete(target, recursive: true);
            if (!Directory.Exists(target))
            {
                foreach (var (file, address) in files)
                {
                    await DownloadAsync(address, file, partial, progress, cancellationToken).ConfigureAwait(false);
                }

                if (finish is not null) await finish(partial, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(partial, marker), markerContent, cancellationToken).ConfigureAwait(false);
            }
        }

        // The other instance installed it meanwhile: what this one started is not needed.
        if (Directory.Exists(target)) Directory.Delete(partial, recursive: true);
        else Directory.Move(partial, target);
    }

    /// <summary>Downloads one file into a folder, resuming a <c>.part</c> file left by an earlier attempt.</summary>
    /// <param name="address">Its address.</param>
    /// <param name="file">The file.</param>
    /// <param name="directory">The folder it goes to.</param>
    /// <param name="progress">Where the progress goes.</param>
    /// <param name="cancellationToken">Ends the download.</param>
    /// <exception cref="PinnedDownloadException">The download failed; its code says why.</exception>
    public async Task DownloadAsync(Uri address, PinnedFile file, string directory, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(progress);
        var final = Path.Combine(directory, file.Name);
        if (File.Exists(final) && new FileInfo(final).Length == file.Size)
        {
            // It got its name after its check, in an earlier attempt.
            progress.Received(file.Size);
            return;
        }

        var part = final + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size)
        {
            File.Delete(part);
            have = 0;
        }

        if (have < file.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new PinnedDownloadException("not_found");
            if (!response.IsSuccessStatusCode) throw new PinnedDownloadException("network");
            // A server that ignores the range sends the whole file: start again.
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0;
            if (response.Content.Headers.ContentLength is { } length && length != file.Size - have) throw new PinnedDownloadException("checksum");
            progress.Received(have);
            var written = have;
            var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var buffer = new byte[1 << 16];
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read;
                        try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new PinnedDownloadException("network"); }
                        catch (IOException) { throw new PinnedDownloadException("network"); }
                        if (read == 0) break;
                        written += read;
                        if (written > file.Size) throw new PinnedDownloadException("checksum");
                        try { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); }
                        catch (IOException) { throw new PinnedDownloadException("disk"); }
                        progress.Received(read);
                    }
                }
            }

            // A connection that closed early leaves what it gave: the next attempt goes on from there.
            if (written != file.Size) throw new PinnedDownloadException("network");
        }
        else
        {
            progress.Received(have);
        }

        progress.Checking(file);
        if (!await VerifyAsync(part, file, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(part);
            throw new PinnedDownloadException("checksum");
        }

        File.Move(part, final, overwrite: true);
    }

    /// <summary>Checks a file against the hash its source gives: SHA-256, or the SHA-1 git names a blob by.</summary>
    /// <param name="path">The file on disk.</param>
    /// <param name="file">What it should be.</param>
    /// <param name="cancellationToken">Ends the check.</param>
    /// <returns>True when it matches, or when no hash is known.</returns>
    public static async Task<bool> VerifyAsync(string path, PinnedFile file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Sha256 is null && file.GitSha1 is null) return true;
        using var hash = IncrementalHash.CreateHash(file.Sha256 is not null ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            if (file.Sha256 is null) hash.AppendData(Encoding.ASCII.GetBytes($"blob {input.Length}\0"));
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        }

        return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256 ?? file.GitSha1, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StallTimeout);
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new PinnedDownloadException("network"); }
    }
}
