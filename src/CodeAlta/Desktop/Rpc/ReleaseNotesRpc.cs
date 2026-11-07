using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The release notes of CodeAlta for the window and for <c>alta release-notes</c>: the releases GitHub publishes, the
/// running version among them, and the dialog that shows them, opened through Settings' requests to the page.
/// </summary>
internal sealed class DesktopReleaseNotes : IAltaReleaseNotes
{
    /// <summary>The page the window opens the dialog of the release notes for: <c>release-notes</c>, then <c>/&lt;version&gt;</c>.</summary>
    internal const string Page = "release-notes";

    private readonly IAltaSettings? _settings;

    /// <param name="source">Reads the releases from GitHub.</param>
    /// <param name="currentVersion">The running version.</param>
    /// <param name="settings">Opens the dialog in the window; null where there is no window.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="currentVersion" /> is blank.</exception>
    internal DesktopReleaseNotes(CodeAltaReleaseNotes source, string currentVersion, IAltaSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        Source = source;
        CurrentVersion = currentVersion;
        _settings = settings;
    }

    /// <summary>The running version.</summary>
    internal string CurrentVersion { get; }

    /// <summary>Reads the releases, and the pictures of their notes.</summary>
    internal CodeAltaReleaseNotes Source { get; }

    /// <inheritdoc />
    public async Task<AltaReleaseNotesList> ListAsync(bool refresh, CancellationToken cancellationToken)
    {
        var releases = await Source.ListAsync(refresh, cancellationToken).ConfigureAwait(false);
        var latest = CodeAltaReleaseNotes.Latest(releases);
        return new AltaReleaseNotesList(CurrentVersion, latest, CodeAltaReleaseNotes.IsLatest(CurrentVersion, latest),
            [.. releases.Select(static release => new AltaRelease(release.Version, release.Name, release.PublishedAt, release.Prerelease, release.Url, release.Body))]);
    }

    /// <inheritdoc />
    public bool Show(string? version, string? callerSessionId)
        => _settings?.Open(string.IsNullOrWhiteSpace(version) ? Page : $"{Page}/{version.Trim()}", null, callerSessionId) ?? false;
}

/// <summary>
/// The release notes for the page: the list of the published versions with the running one and the latest one, and the
/// notes of a version in Markdown. GitHub is read by the host (its rate limit is the computer's), and kept a while.
/// </summary>
[NeoRpcService("releaseNotes", Version = 1)]
internal sealed class ReleaseNotesService
{
    private readonly DesktopReleaseNotes _notes;

    /// <param name="notes">The release notes of the application.</param>
    /// <exception cref="ArgumentNullException"><paramref name="notes" /> is null.</exception>
    internal ReleaseNotesService(DesktopReleaseNotes notes) => _notes = notes ?? throw new ArgumentNullException(nameof(notes));

    /// <summary>
    /// Lists the published versions, the newest first, with the running version, the latest one and whether the running
    /// one is the latest: <c>ok</c>, or <c>failed</c> with why when GitHub could not be read.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<ReleaseNotesListResponse> ListAsync(ReleaseNotesListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var list = await _notes.ListAsync(request.Refresh, cancellationToken).ConfigureAwait(false);
            return new("ok", list.CurrentVersion, list.LatestVersion, list.IsLatest,
                [.. list.Releases.Select(static release => new ReleaseNotesEntry(release.Version, release.Name, Date(release.PublishedAt), release.Prerelease))], null);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return new("failed", _notes.CurrentVersion, null, null, [], exception.Message);
        }
    }

    /// <summary>Reads the notes of a version: <c>ok</c>, <c>not_found</c> (no release of it) or <c>failed</c>.</summary>
    [NeoRpcMethod("read")]
    public async Task<ReleaseNotesReadResponse> ReadAsync(ReleaseNotesReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 64) return new("invalid_request", null, null, null, null);
        try
        {
            var list = await _notes.ListAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            var release = list.Releases.FirstOrDefault(item => CodeAltaReleaseNotes.SameVersion(item.Version, request.Version));
            return release is null ? new("not_found", null, null, null, null)
                : new("ok", new ReleaseNotesEntry(release.Version, release.Name, Date(release.PublishedAt), release.Prerelease), release.Url, release.Body, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return new("failed", null, null, null, exception.Message);
        }
    }

    /// <summary>
    /// Reads a picture of the notes, which the page draws: <c>ok</c> with its media type and its bytes in base64,
    /// <c>invalid_request</c> (an address of neither GitHub nor the site of CodeAlta), <c>not_image</c> (not an image, or one
    /// larger than 8 MB), or <c>failed</c>.
    /// </summary>
    [NeoRpcMethod("picture")]
    public async Task<ReleaseNotesPictureResponse> PictureAsync(ReleaseNotesPictureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CodeAltaReleaseNotes.IsPictureAddress(request.Address)) return new("invalid_request", null, null);
        try
        {
            var picture = await _notes.Source.ReadPictureAsync(request.Address, cancellationToken).ConfigureAwait(false);
            return picture is null ? new("not_image", null, null) : new("ok", picture.MediaType, Convert.ToBase64String(picture.Data));
        }
        catch (HttpRequestException)
        {
            return new("failed", null, null);
        }
    }

    private static string? Date(DateTimeOffset? value) => value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

/// <param name="Refresh">Reads the releases from GitHub again rather than the ones read a moment ago.</param>
internal sealed record ReleaseNotesListRequest(bool Refresh = false);

/// <summary>The published versions.</summary>
/// <param name="Status"><c>ok</c> or <c>failed</c>.</param>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newest version that is not a prerelease, or null.</param>
/// <param name="IsLatest">Whether the running version is at least the latest one; null for a development build or an unknown latest.</param>
/// <param name="Releases">The versions, the newest first.</param>
/// <param name="Message">Why GitHub could not be read, for <c>failed</c>.</param>
internal sealed record ReleaseNotesListResponse(string Status, string CurrentVersion, string? LatestVersion, bool? IsLatest, IReadOnlyList<ReleaseNotesEntry> Releases, string? Message);

/// <summary>A published version.</summary>
/// <param name="Version">Its version.</param>
/// <param name="Name">Its title.</param>
/// <param name="PublishedAt">When it was published, as an ISO date; null when not known.</param>
/// <param name="Prerelease">Whether it is a prerelease.</param>
internal sealed record ReleaseNotesEntry(string Version, string Name, string? PublishedAt, bool Prerelease);

/// <param name="Address">The address of a picture of the notes.</param>
internal sealed record ReleaseNotesPictureRequest(string Address);

/// <summary>A picture of the notes.</summary>
/// <param name="Status"><c>ok</c>, <c>invalid_request</c>, <c>not_image</c> or <c>failed</c>.</param>
/// <param name="MediaType">Its media type.</param>
/// <param name="Data">Its bytes, in base64.</param>
internal sealed record ReleaseNotesPictureResponse(string Status, string? MediaType, string? Data);

/// <param name="Version">The version whose notes are read.</param>
internal sealed record ReleaseNotesReadRequest(string Version);

/// <summary>The notes of a version.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>invalid_request</c> or <c>failed</c>.</param>
/// <param name="Release">The version.</param>
/// <param name="Url">Its page on GitHub.</param>
/// <param name="Body">Its notes, in Markdown.</param>
/// <param name="Message">Why GitHub could not be read, for <c>failed</c>.</param>
internal sealed record ReleaseNotesReadResponse(string Status, ReleaseNotesEntry? Release, string? Url, string? Body, string? Message);
