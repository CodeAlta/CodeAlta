using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>
/// The markers of the sessions another process wrote into the sessions folder: one <c>&lt;session-id&gt;.json</c> each in
/// <see cref="MarkersDirectory"/>. The headless command line drops one for each session it creates; CodeAlta Desktop and
/// TUI watch the folder, list the sessions the markers name, then delete the markers.
/// </summary>
/// <remarks>
/// A marker is written aside and renamed onto its name, so a reader finds a whole marker or none. It is not a journal
/// (<c>.json</c>, not <c>.jsonl</c>), and its folder is not a date folder: the scans of the journals and retention leave it
/// out. The store does not watch: a watcher of <see cref="MarkersDirectory"/> calls <see cref="List"/>.
/// </remarks>
public sealed class SessionMarkerStore
{
    private const string Extension = ".json";
    private const string StagingExtension = ".tmp";
    private const int MaximumBytes = 16 * 1024;

    /// <summary>Initializes the store of the markers of an instance's state root (<see cref="CatalogOptions.SessionMarkersRoot"/>).</summary>
    /// <param name="options">The catalog options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The state root is empty.</exception>
    public SessionMarkerStore(CatalogOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).SessionsRoot)
    {
    }

    /// <summary>Initializes the store of the markers of a sessions folder.</summary>
    /// <param name="sessionsDirectory">The sessions folder; the markers are in its <c>markers</c> folder.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionsDirectory"/> is empty or not a valid path.</exception>
    public SessionMarkerStore(string sessionsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);
        SessionsDirectory = Path.GetFullPath(sessionsDirectory);
        MarkersDirectory = Path.Combine(SessionsDirectory, AgentRuntimePathLayout.SessionMarkersFolderName);
    }

    /// <summary>Gets the sessions folder the journal paths of the markers are relative to.</summary>
    public string SessionsDirectory { get; }

    /// <summary>Gets the folder of the markers, for a watcher.</summary>
    public string MarkersDirectory { get; }

    /// <summary>Writes the marker of a session, replacing the one it may have, atomically.</summary>
    /// <param name="marker">The marker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The session id is not valid (<see cref="SessionLeaseStore.IsValidSessionId"/>), the writer is empty, or the journal
    /// path is not a <c>.jsonl</c> path inside the sessions folder.
    /// </exception>
    /// <exception cref="IOException">The marker cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    public void Write(SessionMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        SessionFileName.Validate(marker.SessionId, nameof(marker));
        ArgumentException.ThrowIfNullOrWhiteSpace(marker.Writer, nameof(marker));
        var normalized = marker with { JournalPath = NormalizeJournalPath(marker.JournalPath) ?? throw new ArgumentException(
            $"'{marker.JournalPath}' is not the path of a journal inside the sessions folder.", nameof(marker)) };

        Directory.CreateDirectory(MarkersDirectory);
        var path = GetMarkerPath(marker.SessionId);
        // Not a .json name: List never reads a marker being written.
        var staged = Path.Combine(MarkersDirectory, $"{marker.SessionId}.{Guid.NewGuid():N}{StagingExtension}");
        try
        {
            File.WriteAllBytes(staged, JsonSerializer.SerializeToUtf8Bytes(normalized, SessionFilesJsonContext.Default.SessionMarker));
            StagedFileReplacement.MoveOver(staged, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A staged file is never listed; it stays until the folder is cleaned.
            }

            throw;
        }
    }

    /// <summary>Lists the markers. A file that is not a whole marker, or whose name is not its session, is left out.</summary>
    /// <returns>The markers, oldest first.</returns>
    /// <exception cref="IOException">The folder cannot be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    public IReadOnlyList<SessionMarker> List()
    {
        if (!Directory.Exists(MarkersDirectory)) return [];
        var markers = new List<SessionMarker>();
        foreach (var path in Directory.EnumerateFiles(MarkersDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)) continue;
            var sessionId = Path.GetFileNameWithoutExtension(path);
            if (SessionFileName.IsValid(sessionId) && TryReadFile(path, sessionId) is { } marker) markers.Add(marker);
        }

        return [.. markers.OrderBy(static marker => marker.CreatedUtc).ThenBy(static marker => marker.SessionId, StringComparer.Ordinal)];
    }

    /// <summary>Reads the marker of a session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The marker; <see langword="null"/> when there is none, or it is not a whole marker.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not valid (<see cref="SessionLeaseStore.IsValidSessionId"/>).</exception>
    public SessionMarker? TryRead(string sessionId)
        => TryReadFile(GetMarkerPath(sessionId), sessionId);

    /// <summary>Deletes the marker of a session, once its session is listed.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><see langword="true"/> when a marker was deleted; <see langword="false"/> when there was none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not valid (<see cref="SessionLeaseStore.IsValidSessionId"/>).</exception>
    /// <exception cref="IOException">The marker cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the marker is denied.</exception>
    public bool Delete(string sessionId)
    {
        var path = GetMarkerPath(sessionId);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Gets the full path of the journal a marker names.</summary>
    /// <param name="marker">The marker.</param>
    /// <returns>The path, inside <see cref="SessionsDirectory"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The journal path is not a <c>.jsonl</c> path inside the sessions folder.</exception>
    public string GetJournalPath(SessionMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var relative = NormalizeJournalPath(marker.JournalPath)
            ?? throw new ArgumentException($"'{marker.JournalPath}' is not the path of a journal inside the sessions folder.", nameof(marker));
        return Path.GetFullPath(Path.Combine(SessionsDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string GetMarkerPath(string sessionId)
    {
        SessionFileName.Validate(sessionId, nameof(sessionId));
        return Path.Combine(MarkersDirectory, sessionId + Extension);
    }

    // A whole marker of the session its file is named after, or null: a file being replaced, cut short or written by
    // something else is skipped, never an error.
    private static SessionMarker? TryReadFile(string path, string sessionId)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
            var marker = JsonSerializer.Deserialize(File.ReadAllBytes(path), SessionFilesJsonContext.Default.SessionMarker);
            return marker is not null && string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(marker.Writer) && NormalizeJournalPath(marker.JournalPath) is { } journal
                ? marker with { JournalPath = journal }
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // A relative .jsonl path with '/' separators that stays inside the sessions folder, or null.
    private static string? NormalizeJournalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal)) return null;
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment is "" or "." or "..")) return null;
        if (segments[0].Equals(AgentRuntimePathLayout.SessionMarkersFolderName, StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals(AgentRuntimePathLayout.SessionLeasesFolderName, StringComparison.OrdinalIgnoreCase)) return null;
        return normalized.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }
}
