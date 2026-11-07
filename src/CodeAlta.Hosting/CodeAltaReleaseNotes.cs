using System.Net.Http.Headers;
using System.Text.Json;
using NuGet.Versioning;

namespace CodeAlta.Hosting;

/// <summary>A release of CodeAlta as GitHub publishes it: its version, its title, its date and its notes in Markdown.</summary>
/// <param name="Version">The version, its tag without a leading <c>v</c>.</param>
/// <param name="Name">The title of the release; its version when it has none.</param>
/// <param name="PublishedAt">When it was published, or null for a release not published yet.</param>
/// <param name="Prerelease">Whether it is a prerelease.</param>
/// <param name="Url">Its page on GitHub.</param>
/// <param name="Body">Its notes, in Markdown.</param>
public sealed record CodeAltaRelease(string Version, string Name, DateTimeOffset? PublishedAt, bool Prerelease, string Url, string Body);

/// <summary>A picture of release notes, as its server gave it.</summary>
/// <param name="MediaType">Its media type, an <c>image/</c> one.</param>
/// <param name="Data">Its bytes.</param>
public sealed record CodeAltaReleasePicture(string MediaType, byte[] Data);

/// <summary>
/// The release notes of CodeAlta, read from the releases of its GitHub repository and kept for a while: the window shows
/// them, and agents read them with <c>alta release-notes</c>. GitHub answers 60 requests an hour to a caller without a
/// token, so the list is read again only when it is older than <see cref="MaximumAge" /> or when asked for.
/// </summary>
public sealed class CodeAltaReleaseNotes : IDisposable
{
    /// <summary>The repository whose releases are read.</summary>
    public const string Repository = "CodeAlta/CodeAlta";

    /// <summary>How long a read list is kept before it is read again.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);

    /// <summary>How old a list is before an explicit refresh reads it again: GitHub is not asked more often.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(1);

    /// <summary>The largest picture of release notes read: a larger one stays a link.</summary>
    public const int MaximumPictureLength = 8 * 1024 * 1024;

    // The newest pictures read, so that notes shown again draw them at once: at most this many.
    private const int KeptPictures = 32;

    // The pictures of release notes are published on GitHub and on the site of CodeAlta: only those are read.
    private static readonly string[] PictureHosts = ["github.com", "githubusercontent.com", "codealta.io"];

    private const string Address = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";
    private readonly Dictionary<string, CodeAltaReleasePicture> _pictures = new(StringComparer.Ordinal);
    private readonly Queue<string> _pictureOrder = new();
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<CodeAltaRelease>>? _read;
    private long _readAt;

    /// <summary>Creates the reader of the releases.</summary>
    /// <param name="version">The running version, named in the <c>User-Agent</c> GitHub requires.</param>
    /// <param name="handler">Sends the requests; a new <see cref="HttpClientHandler" /> by default, which the reader owns.</param>
    /// <param name="time">The clock of the age of the list.</param>
    /// <exception cref="ArgumentException"><paramref name="version" /> is blank.</exception>
    public CodeAltaReleaseNotes(string version, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodeAlta", Uri.EscapeDataString(version)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Lists the published releases, the newest first. A list older than <see cref="MaximumAge" /> is read again, and one
    /// older than <see cref="MinimumAge" /> when <paramref name="refresh" /> asks for it; a read that failed is tried again
    /// at the next call.
    /// </summary>
    /// <param name="refresh">Reads the list again, unless it was read less than <see cref="MinimumAge" /> ago.</param>
    /// <param name="cancellationToken">Stops waiting for the list; the read itself goes on for the next caller.</param>
    /// <returns>The releases.</returns>
    /// <exception cref="HttpRequestException">GitHub could not be reached, or refused the request (its rate limit).</exception>
    /// <exception cref="JsonException">GitHub answered something that is not a list of releases.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<IReadOnlyList<CodeAltaRelease>> ListAsync(bool refresh, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<CodeAltaRelease>> read;
        lock (_gate)
        {
            var age = _read is null ? TimeSpan.MaxValue : _time.GetElapsedTime(_readAt);
            var stale = _read is null || _read.IsFaulted || _read.IsCanceled || age > MaximumAge || refresh && age > MinimumAge;
            if (stale)
            {
                _read = ReadAsync();
                _readAt = _time.GetTimestamp();
            }

            read = _read!;
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>Reads the release of a version, its notes included; null when GitHub publishes none of that version.</summary>
    /// <param name="version">The version, with or without a leading <c>v</c>.</param>
    /// <param name="cancellationToken">Stops waiting for the list.</param>
    /// <returns>The release, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="version" /> is blank.</exception>
    /// <exception cref="HttpRequestException">GitHub could not be reached, or refused the request.</exception>
    /// <exception cref="JsonException">GitHub answered something that is not a list of releases.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<CodeAltaRelease?> FindAsync(string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var wanted = TrimVersion(version);
        var releases = await ListAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        return releases.FirstOrDefault(release => SameVersion(release.Version, wanted));
    }

    /// <summary>
    /// The newest version published as a release that is not a prerelease, else the newest of all; null without any.
    /// </summary>
    /// <param name="releases">The releases.</param>
    /// <returns>The version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="releases" /> is null.</exception>
    public static string? Latest(IReadOnlyList<CodeAltaRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var versions = releases.Select(static release => (release, version: NuGetVersion.TryParse(release.Version, out var parsed) ? parsed : null))
            .Where(static entry => entry.version is not null).ToList();
        var stable = versions.Where(static entry => !entry.release.Prerelease).ToList();
        return (stable.Count > 0 ? stable : versions).OrderByDescending(static entry => entry.version).Select(static entry => entry.release.Version).FirstOrDefault();
    }

    /// <summary>
    /// Whether <paramref name="current" /> is at least <paramref name="latest" />; null when either is not a version (a
    /// development build).
    /// </summary>
    /// <param name="current">The running version.</param>
    /// <param name="latest">The latest published version.</param>
    /// <returns>Whether the running version is the latest one.</returns>
    public static bool? IsLatest(string? current, string? latest)
        => NuGetVersion.TryParse(TrimVersion(current ?? ""), out var running) && NuGetVersion.TryParse(TrimVersion(latest ?? ""), out var published)
            ? running >= published : null;

    /// <summary>Whether two versions name the same release, with or without a leading <c>v</c>.</summary>
    /// <param name="left">A version.</param>
    /// <param name="right">Another version.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool SameVersion(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (a, b) = (TrimVersion(left), TrimVersion(right));
        return NuGetVersion.TryParse(a, out var x) && NuGetVersion.TryParse(b, out var y) ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the releases of a GitHub answer: drafts are left out, and the newest comes first.</summary>
    /// <param name="root">The JSON array GitHub answered.</param>
    /// <returns>The releases.</returns>
    /// <exception cref="JsonException"><paramref name="root" /> is not an array.</exception>
    public static IReadOnlyList<CodeAltaRelease> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub did not answer a list of releases.");
        var releases = new List<CodeAltaRelease>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Bool(item, "draft")) continue;
            var tag = String(item, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var version = TrimVersion(tag);
            var name = String(item, "name");
            var published = DateTimeOffset.TryParse(String(item, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
            releases.Add(new CodeAltaRelease(version, string.IsNullOrWhiteSpace(name) ? version : name.Trim(), published, Bool(item, "prerelease"),
                String(item, "html_url") ?? $"https://github.com/{Repository}/releases/tag/{tag}", String(item, "body") ?? ""));
        }

        return [.. releases.OrderByDescending(static release => release.PublishedAt ?? DateTimeOffset.MinValue)];
    }

    /// <summary>Whether release notes may show a picture of that address: one of GitHub or of the site of CodeAlta, over HTTPS.</summary>
    /// <param name="address">The address of the picture.</param>
    /// <returns>Whether it is read.</returns>
    public static bool IsPictureAddress(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && PictureHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a picture of release notes, for the window to draw it: the page loads nothing of the web itself. Null when the
    /// address answers something that is not an image, or one larger than <see cref="MaximumPictureLength" />.
    /// </summary>
    /// <param name="address">The address of the picture, as <see cref="IsPictureAddress" /> accepts it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The picture, or null.</returns>
    /// <exception cref="ArgumentException">The address is not one release notes may show.</exception>
    /// <exception cref="HttpRequestException">The picture could not be read.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public async Task<CodeAltaReleasePicture?> ReadPictureAsync(string address, CancellationToken cancellationToken)
    {
        if (!IsPictureAddress(address)) throw new ArgumentException("Release notes show the pictures of GitHub and of the site of CodeAlta only.", nameof(address));
        lock (_gate)
        {
            if (_pictures.TryGetValue(address, out var kept)) return kept;
        }

        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The picture answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        if (response.Content.Headers.ContentLength > MaximumPictureLength) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumPictureLength) return null;
            buffer.Write(chunk, 0, read);
        }

        var picture = new CodeAltaReleasePicture(mediaType, buffer.ToArray());
        lock (_gate)
        {
            if (_pictures.TryAdd(address, picture))
            {
                _pictureOrder.Enqueue(address);
                while (_pictureOrder.Count > KeptPictures) _pictures.Remove(_pictureOrder.Dequeue());
            }
        }

        return picture;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyList<CodeAltaRelease>> ReadAsync()
    {
        using var response = await _http.GetAsync(Address, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var limited = response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";
            throw new HttpRequestException(limited
                ? "GitHub limits the requests of this computer for now: try again in a while."
                : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    private static string TrimVersion(string version)
    {
        var trimmed = version.Trim();
        return trimmed.Length > 1 && trimmed[0] is 'v' or 'V' && char.IsDigit(trimmed[1]) ? trimmed[1..] : trimmed;
    }

    private static string? String(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
