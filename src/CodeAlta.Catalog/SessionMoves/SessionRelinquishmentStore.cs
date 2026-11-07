using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>What a machine recorded of a session it is giving, or gave, to another machine.</summary>
public sealed record SessionRelinquishment
{
    /// <summary>The state of a session offered to another machine: nothing may write it until the move ends.</summary>
    public const string Frozen = "frozen";

    /// <summary>The state of a session another machine owns now: it stays read-only here.</summary>
    public const string Relinquished = "relinquished";

    /// <summary>Gets the state: <see cref="Frozen"/> or <see cref="Relinquished"/>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Gets the move.</summary>
    [JsonPropertyName("moveId")]
    public required string MoveId { get; init; }

    /// <summary>Gets the machine the session goes, or went, to.</summary>
    [JsonPropertyName("toMachine")]
    public string? ToMachine { get; init; }

    /// <summary>Gets the ownership epoch the move committed; 0 while the session is frozen.</summary>
    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    /// <summary>Gets when the state was recorded.</summary>
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    /// <summary>Gets whether the session moved for good.</summary>
    [JsonIgnore]
    public bool IsRelinquished => State == Relinquished;
}

/// <summary>
/// The sessions of an instance that are frozen for a move or were given to another machine:
/// <c>&lt;session-id&gt;.moved</c>, beside the lease of the session, so that every writer of the sessions folder honors
/// it (CodeAlta Desktop, the TUI, the node and the command line): a writer checks it once it holds the lease.
/// </summary>
/// <remarks>
/// The file is written by rename, by the holder of the lease of the session, and never removed by retention. A frozen
/// session is unfrozen by the move that froze it only.
/// </remarks>
public sealed class SessionRelinquishmentStore
{
    private const string Extension = ".moved";

    /// <summary>Initializes the store of an instance's sessions folder (<see cref="CatalogOptions.SessionLeasesRoot"/>).</summary>
    /// <param name="options">The catalog options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SessionRelinquishmentStore(CatalogOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).SessionLeasesRoot)
    {
    }

    /// <summary>Initializes the store of a folder of leases.</summary>
    /// <param name="leasesDirectory">The folder of the lease files.</param>
    /// <exception cref="ArgumentException"><paramref name="leasesDirectory"/> is blank.</exception>
    public SessionRelinquishmentStore(string leasesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leasesDirectory);
        Directory = Path.GetFullPath(leasesDirectory);
    }

    /// <summary>Gets the folder of the files.</summary>
    public string Directory { get; }

    /// <summary>Gets the path of the file of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not a valid session id.</exception>
    public string GetPath(string sessionId)
    {
        SessionFileName.Validate(sessionId, nameof(sessionId));
        return Path.Combine(Directory, sessionId + Extension);
    }

    /// <summary>Reads what is recorded of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The record; <see langword="null"/> when the session is neither frozen nor given away, or its id is not valid.</returns>
    /// <remarks>A file that cannot be read is taken for a relinquishment: a session is never written by mistake.</remarks>
    public SessionRelinquishment? Read(string sessionId)
    {
        if (!SessionFileName.IsValid(sessionId)) return null;
        var path = Path.Combine(Directory, sessionId + Extension);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), SessionMoveJsonContext.Default.SessionRelinquishment)
                ?? Unreadable();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return File.Exists(path) ? Unreadable() : null;
        }

        static SessionRelinquishment Unreadable() => new() { State = SessionRelinquishment.Relinquished, MoveId = string.Empty };
    }

    /// <summary>Lists the sessions this instance gave to another machine (the frozen ones left out).</summary>
    /// <returns>Their ids; a file that cannot be read counts as given away.</returns>
    public IReadOnlySet<string> ListRelinquished()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(Directory)) return found;
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension, SearchOption.TopDirectoryOnly))
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                if (Read(sessionId) is { IsRelinquished: true }) found.Add(sessionId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder went away meanwhile: what was read stands.
        }

        return found;
    }

    /// <summary>Lists the sessions of this instance that are frozen for a move, and the move that froze each.</summary>
    /// <returns>The sessions and their moves; a file that cannot be read is left out (it counts as given away).</returns>
    public IReadOnlyList<(string SessionId, string MoveId)> ListFrozen()
    {
        var found = new List<(string SessionId, string MoveId)>();
        if (!System.IO.Directory.Exists(Directory)) return found;
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension, SearchOption.TopDirectoryOnly))
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                if (Read(sessionId) is { State: SessionRelinquishment.Frozen } record) found.Add((sessionId, record.MoveId));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder went away meanwhile: what was read stands.
        }

        return found;
    }

    /// <summary>Freezes a session for a move. The caller holds the lease of the session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move.</param>
    /// <param name="toMachine">The machine it is offered to.</param>
    /// <returns><see langword="true"/> when it was frozen; <see langword="false"/> when another move froze it or it was given away.</returns>
    /// <exception cref="ArgumentException">An argument is not valid.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public bool Freeze(string sessionId, string moveId, string? toMachine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moveId);
        if (Read(sessionId) is { } current && !(current.State == SessionRelinquishment.Frozen && current.MoveId == moveId)) return false;
        Write(sessionId, new SessionRelinquishment { State = SessionRelinquishment.Frozen, MoveId = moveId, ToMachine = toMachine, Time = DateTimeOffset.UtcNow });
        return true;
    }

    /// <summary>Records that a session belongs to another machine now.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move that gave it.</param>
    /// <param name="toMachine">The machine that owns it.</param>
    /// <param name="epoch">The ownership epoch the move committed.</param>
    /// <exception cref="ArgumentException">An argument is not valid.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Relinquish(string sessionId, string moveId, string? toMachine, long epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moveId);
        Write(sessionId, new SessionRelinquishment { State = SessionRelinquishment.Relinquished, MoveId = moveId, ToMachine = toMachine, Epoch = epoch, Time = DateTimeOffset.UtcNow });
    }

    /// <summary>Unfreezes a session the move did not take: it can be written again.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="moveId">The move that froze it.</param>
    /// <returns><see langword="true"/> when it was unfrozen; <see langword="false"/> when it was not frozen by that move.</returns>
    /// <exception cref="IOException">The file cannot be removed.</exception>
    public bool Unfreeze(string sessionId, string moveId)
    {
        if (Read(sessionId) is not { State: SessionRelinquishment.Frozen } current || current.MoveId != moveId) return false;
        File.Delete(GetPath(sessionId));
        return true;
    }

    /// <summary>Removes what is recorded of a session that comes back to this machine.</summary>
    /// <param name="sessionId">The session.</param>
    /// <exception cref="IOException">The file cannot be removed.</exception>
    public void Clear(string sessionId) => File.Delete(GetPath(sessionId));

    private void Write(string sessionId, SessionRelinquishment record)
    {
        var path = GetPath(sessionId);
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, SessionMoveJsonContext.Default.SessionRelinquishment);
                stream.Flush(flushToDisk: true);
            }

            StagedFileReplacement.MoveOver(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
