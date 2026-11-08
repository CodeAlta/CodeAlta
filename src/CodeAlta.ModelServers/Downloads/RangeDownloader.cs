using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Core.IO;

namespace CodeAlta.ModelServers.Downloads;

/// <summary>Where a file is fetched from now: an address, and whether the request carries the token (never to a CDN).</summary>
/// <param name="Address">The address.</param>
/// <param name="Token">The bearer token to send, or null.</param>
public sealed record DownloadSource(Uri Address, string? Token);

/// <summary>Why a download stopped, said to people.</summary>
public sealed class DownloadException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DownloadException"/> class.</summary>
    /// <param name="code"><c>network</c>, <c>not_found</c>, <c>gated</c>, <c>checksum</c>, <c>disk</c> or <c>rate_limited</c>.</param>
    /// <param name="message">What it means, for people.</param>
    public DownloadException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the reason.</summary>
    public string Code { get; }
}

/// <summary>The chunks of a file already on disk, kept beside it so that a download resumes where it stopped.</summary>
public sealed record DownloadJournal
{
    /// <summary>Gets the size of the file.</summary>
    public long Size { get; init; }

    /// <summary>Gets the size of a chunk.</summary>
    public long ChunkSize { get; init; }

    /// <summary>Gets the chunks done.</summary>
    public List<int> Done { get; init; } = [];
}

/// <summary>
/// Downloads one file in ranges over several connections into a file of its final size, with a journal of the chunks
/// done beside it: a stop, a restart or a broken connection resumes there. An address that expired (a signed CDN address
/// lives about an hour) is asked for again, a rate limit is waited for, and the SHA-256 is checked at the end.
/// </summary>
public sealed class RangeDownloader
{
    /// <summary>The size of a chunk.</summary>
    public const long ChunkSize = 64L * 1024 * 1024;

    /// <summary>A read that gets nothing for this long fails the attempt.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private const int Attempts = 6;
    private readonly HttpClient _http;

    /// <summary>Creates a downloader.</summary>
    /// <param name="http">The client of the downloads; it follows no redirect by itself only when the source asks for it.</param>
    public RangeDownloader(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Downloads a file into <paramref name="partPath"/>, then checks it.</summary>
    /// <param name="resolve">Gives where the file is now; called again when an address expires.</param>
    /// <param name="partPath">The file being written.</param>
    /// <param name="size">Its size, or -1 when not known (then one connection, no resume).</param>
    /// <param name="sha256">Its SHA-256 in lowercase hexadecimal, or null to check nothing.</param>
    /// <param name="connections">How many connections at once.</param>
    /// <param name="received">Called with the bytes added: those found from an earlier attempt first.</param>
    /// <param name="checking">Called once every byte is there, before the check.</param>
    /// <param name="cancellationToken">Stops; what is on disk stays for the next attempt.</param>
    /// <exception cref="DownloadException">It failed; its code says why.</exception>
    public async Task DownloadAsync(Func<CancellationToken, Task<DownloadSource>> resolve, string partPath, long size, string? sha256, int connections,
        Action<long> received, Action? checking, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(received);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(partPath))!);
        var journalPath = partPath + ".chunks";
        var journal = ReadJournal(journalPath, size);
        var source = await resolve(cancellationToken).ConfigureAwait(false);
        if (size < 0)
        {
            await DownloadWholeAsync(source, partPath, received, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var chunks = (int)((size + ChunkSize - 1) / ChunkSize);
            var done = new HashSet<int>(journal.Done.Where(index => index >= 0 && index < chunks));
            using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (file.Length != size)
                {
                    try { file.SetLength(size); }
                    catch (IOException exception) { throw new DownloadException("disk", $"The disk has no room for {size:N0} bytes: {exception.Message}"); }
                }

                received(done.Sum(index => ChunkLength(index, size)));
                var pending = new Queue<int>(Enumerable.Range(0, chunks).Where(index => !done.Contains(index)));
                var gate = new Lock();
                var shared = new SourceBox(source);
                async Task WorkAsync()
                {
                    while (true)
                    {
                        int index;
                        lock (gate)
                        {
                            if (!pending.TryDequeue(out index)) return;
                        }

                        await FetchChunkAsync(shared, resolve, file, index, size, received, cancellationToken).ConfigureAwait(false);
                        lock (gate)
                        {
                            done.Add(index);
                            journal = journal with { Size = size, ChunkSize = ChunkSize, Done = done.Order().ToList() };
                            SafeFile.WriteAllText(journalPath, JsonSerializer.Serialize(journal, ModelServersJsonContext.Default.DownloadJournal));
                        }
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, Math.Clamp(connections, 1, 8)).Select(_ => WorkAsync())).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (sha256 is { Length: > 0 })
        {
            checking?.Invoke();
            string actual;
            await using (var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                TryDelete(journalPath);
                throw new DownloadException("checksum", $"The file does not match its SHA-256 ({actual[..12]}… instead of {sha256[..12]}…): it was removed.");
            }
        }

        TryDelete(journalPath);
    }

    private sealed class SourceBox(DownloadSource source)
    {
        public DownloadSource Source { get; set; } = source;
    }

    private async Task FetchChunkAsync(SourceBox shared, Func<CancellationToken, Task<DownloadSource>> resolve, FileStream file, int index, long size,
        Action<long> received, CancellationToken cancellationToken)
    {
        var start = index * ChunkSize;
        var end = Math.Min(size, start + ChunkSize) - 1;
        var buffer = new byte[256 * 1024];
        for (var attempt = 1; ; attempt++)
        {
            var written = 0L;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, shared.Source.Address);
                request.Headers.Range = new RangeHeaderValue(start, end);
                if (shared.Source.Token is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized && shared.Source.Token is null)
                {
                    // The signed address of the CDN expired: ask the hub for a new one.
                    shared.Source = await resolve(cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"The address expired ({(int)response.StatusCode}).");
                }

                await ThrowForStatusAsync(response, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent && !(start == 0 && end == size - 1))
                    throw new DownloadException("network", "The server does not send parts of the file.");
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var position = start;
                while (position <= end)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new HttpRequestException("Nothing came for a minute.");
                    }

                    if (read == 0) throw new HttpRequestException("The connection ended early.");
                    await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);
                    position += read;
                    written += read;
                    received(read);
                }

                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                if (written > 0) received(-written); // the chunk starts again
                if (exception is IOException io && io.HResult is unchecked((int)0x80070070) or 28)
                    throw new DownloadException("disk", "The disk is full.");
                if (attempt >= Attempts) throw new DownloadException("network", $"The download failed after {Attempts} attempts: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * attempt)), cancellationToken).ConfigureAwait(false);
            }
            catch (RateLimitedException limited)
            {
                if (written > 0) received(-written);
                await Task.Delay(limited.Wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadWholeAsync(DownloadSource source, string partPath, Action<long> received, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Address);
        if (source.Token is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await ThrowForStatusAsync(response, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received(read);
            }
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadException("network", exception.Message);
        }
        catch (RateLimitedException)
        {
            throw new DownloadException("rate_limited", "The server limits the downloads of this address for now.");
        }
    }

    private static async Task ThrowForStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            throw new RateLimitedException(wait > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait);
        }

        var code = response.Headers.TryGetValues("X-Error-Code", out var values) ? values.FirstOrDefault() : null;
        if (code is "GatedRepo" || response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DownloadException("gated", "This repository asks for a licence first: accept it on Hugging Face, and give the machine an HF_TOKEN secret.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new DownloadException("not_found", "The file is not there any more.");
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"The server answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 200)]}");
    }

    private static DownloadJournal ReadJournal(string path, long size)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), ModelServersJsonContext.Default.DownloadJournal) is { } journal
                && journal.Size == size && journal.ChunkSize == ChunkSize) return journal;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new DownloadJournal { Size = size, ChunkSize = ChunkSize };
    }

    private static long ChunkLength(int index, long size) => Math.Min(size, (index + 1) * ChunkSize) - index * ChunkSize;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class RateLimitedException(TimeSpan wait) : Exception("Rate limited.")
    {
        public TimeSpan Wait { get; } = wait;
    }
}
