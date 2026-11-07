namespace CodeAlta.LiveTool;

/// <summary>
/// The release notes of CodeAlta, for <c>alta release-notes</c>: the versions published on GitHub, their notes, and
/// where the running version stands among them. A host without a window registers no such service.
/// </summary>
public interface IAltaReleaseNotes
{
    /// <summary>Lists the published releases, the newest first, with the running version and the latest one.</summary>
    /// <param name="refresh">Reads the releases again rather than the ones read a moment ago.</param>
    /// <param name="cancellationToken">Stops waiting for them.</param>
    /// <returns>The releases.</returns>
    /// <exception cref="HttpRequestException">GitHub could not be reached, or refused the request.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    Task<AltaReleaseNotesList> ListAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>Shows the release notes of a version to the user, in their dialog in the window.</summary>
    /// <param name="version">The version shown; null for the running one.</param>
    /// <param name="callerSessionId">The session that asks, whose window shows them; null for the main window.</param>
    /// <returns>Whether a window received the request.</returns>
    bool Show(string? version, string? callerSessionId);
}

/// <summary>The published releases, with the running version and the latest one.</summary>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newest version that is not a prerelease; null when none is published.</param>
/// <param name="IsLatest">Whether the running version is at least the latest one; null for a development build.</param>
/// <param name="Releases">The releases, the newest first.</param>
public sealed record AltaReleaseNotesList(string CurrentVersion, string? LatestVersion, bool? IsLatest, IReadOnlyList<AltaRelease> Releases);

/// <summary>A published release.</summary>
/// <param name="Version">Its version.</param>
/// <param name="Name">Its title.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Prerelease">Whether it is a prerelease.</param>
/// <param name="Url">Its page on GitHub.</param>
/// <param name="Body">Its notes, in Markdown.</param>
public sealed record AltaRelease(string Version, string Name, DateTimeOffset? PublishedAt, bool Prerelease, string Url, string Body);
