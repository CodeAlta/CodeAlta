using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>The states of a move, as each side records them.</summary>
public static class SessionMoveState
{
    /// <summary>The source froze the session and made its bundle.</summary>
    public const string Offered = "offered";

    /// <summary>The target holds a verified copy: its worktree is ready, its journal checked, nothing runs yet.</summary>
    public const string Prepared = "prepared";

    /// <summary>The arbiter recorded the new owner: the target runs the session, the source must give it up.</summary>
    public const string Committed = "committed";

    /// <summary>The move ended: the target installed the session, or the source relinquished it.</summary>
    public const string Finished = "finished";

    /// <summary>The arbiter never committed the move: the source unfroze the session, the target removed what it prepared.</summary>
    public const string Aborted = "aborted";

    /// <summary>Gets whether a state ends a move.</summary>
    /// <param name="state">The state.</param>
    /// <returns><see langword="true"/> for <see cref="Finished"/> and <see cref="Aborted"/>.</returns>
    public static bool IsFinal(string? state) => state is Finished or Aborted;
}

/// <summary>The side of a move a machine is on.</summary>
public static class SessionMoveRole
{
    /// <summary>The machine that gives the session.</summary>
    public const string Source = "source";

    /// <summary>The machine that takes it.</summary>
    public const string Target = "target";
}

/// <summary>What one machine recorded of a move.</summary>
public sealed record SessionMoveTransaction
{
    /// <summary>Gets the move.</summary>
    [JsonPropertyName("moveId")]
    public required string MoveId { get; init; }

    /// <summary>Gets the side this machine is on (<see cref="SessionMoveRole"/>).</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Gets the state (<see cref="SessionMoveState"/>).</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Gets the session.</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>Gets the machine the session comes from.</summary>
    [JsonPropertyName("fromMachine")]
    public string? FromMachine { get; init; }

    /// <summary>Gets the machine the session goes to.</summary>
    [JsonPropertyName("toMachine")]
    public string? ToMachine { get; init; }

    /// <summary>Gets the epoch the move commits, once known.</summary>
    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    /// <summary>Gets the folder of the bundle on this machine.</summary>
    [JsonPropertyName("bundle")]
    public string? BundleFolder { get; init; }

    /// <summary>Gets the project the target resolved, on the target.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; init; }

    /// <summary>Gets the folder of that project, on the target.</summary>
    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; init; }

    /// <summary>Gets the root of the worktree the target prepared.</summary>
    [JsonPropertyName("worktreeRoot")]
    public string? WorktreeRoot { get; init; }

    /// <summary>Gets the folder the session works in on the target.</summary>
    [JsonPropertyName("workFolder")]
    public string? WorkFolder { get; init; }

    /// <summary>Gets the branch the target checked out.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>Gets whether the target created that branch (and removes it when the move is undone).</summary>
    [JsonPropertyName("createdBranch")]
    public bool CreatedBranch { get; init; }

    /// <summary>Gets what went wrong, for a move that was undone.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Gets when the state was recorded.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>What the arbiter of a move decided, once and for all.</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Outcome"><see cref="SessionMoveState.Committed"/> or <see cref="SessionMoveState.Aborted"/>.</param>
/// <param name="Epoch">The epoch a committed move gave the session.</param>
/// <param name="Time">When it was decided.</param>
public sealed record SessionMoveDecision(
    [property: JsonPropertyName("moveId")] string MoveId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

/// <summary>
/// The durable records of the moves of an instance, under <see cref="CatalogOptions.FleetRoot"/>/<c>moves</c>: one file
/// per move and side, written by rename, and the decisions of this machine when it arbitrates moves (in paired mode the
/// target does), each written once.
/// </summary>
public sealed class SessionMoveTransactionStore
{
    /// <summary>Initializes the store of an instance.</summary>
    /// <param name="options">The catalog options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SessionMoveTransactionStore(CatalogOptions options)
        : this(Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).FleetRoot, "moves"))
    {
    }

    /// <summary>Initializes the store of a folder.</summary>
    /// <param name="directory">The folder of the records.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    public SessionMoveTransactionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>Gets the folder of the records.</summary>
    public string Directory { get; }

    /// <summary>Gets the folder this machine keeps the bundles and the staged copies of its moves in.</summary>
    public string BundlesDirectory => Path.Combine(Directory, "bundles");

    /// <summary>Writes the record of a move on one side, replacing the previous one.</summary>
    /// <param name="transaction">The record.</param>
    /// <exception cref="ArgumentException">The move or the role is not valid.</exception>
    /// <exception cref="IOException">The record cannot be written.</exception>
    public void Save(SessionMoveTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var path = PathOf(transaction.MoveId, transaction.Role);
        WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(transaction with { UpdatedAt = DateTimeOffset.UtcNow },
            SessionMoveJsonContext.Default.SessionMoveTransaction));
    }

    /// <summary>Reads the record of a move on one side.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="role">The side (<see cref="SessionMoveRole"/>).</param>
    /// <returns>The record; <see langword="null"/> when there is none.</returns>
    /// <exception cref="ArgumentException">The move or the role is not valid.</exception>
    /// <exception cref="IOException">The record cannot be read.</exception>
    /// <exception cref="JsonException">The record is damaged.</exception>
    public SessionMoveTransaction? Read(string moveId, string role)
    {
        var path = PathOf(moveId, role);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), SessionMoveJsonContext.Default.SessionMoveTransaction) : null;
    }

    /// <summary>Lists the moves that did not end, on both sides.</summary>
    /// <returns>The records; a damaged one is left out.</returns>
    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public IReadOnlyList<SessionMoveTransaction> ListUnfinished() => List(unfinishedOnly: true);

    /// <summary>Lists the records of the moves of this machine, on both sides, the latest first.</summary>
    /// <returns>The records; a damaged one is left out.</returns>
    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public IReadOnlyList<SessionMoveTransaction> List() => [.. List(unfinishedOnly: false).OrderByDescending(static record => record.UpdatedAt)];

    private List<SessionMoveTransaction> List(bool unfinishedOnly)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var found = new List<SessionMoveTransaction>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(path), SessionMoveJsonContext.Default.SessionMoveTransaction) is { } record
                    && (!unfinishedOnly || !SessionMoveState.IsFinal(record.State)))
                    found.Add(record);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged record is left for a person to look at; it holds nothing a recovery could trust.
            }
        }

        return found;
    }

    /// <summary>
    /// Records the decision of this machine, as arbiter, on a move: the first decision stands, also against another
    /// process, and is returned to every later caller.
    /// </summary>
    /// <param name="moveId">The move.</param>
    /// <param name="outcome"><see cref="SessionMoveState.Committed"/> or <see cref="SessionMoveState.Aborted"/>.</param>
    /// <param name="epoch">The epoch a commit gives the session.</param>
    /// <returns>The decision that stands: this one, or the one recorded before.</returns>
    /// <exception cref="ArgumentException">An argument is not valid.</exception>
    /// <exception cref="IOException">The decision cannot be written or read.</exception>
    public SessionMoveDecision Decide(string moveId, string outcome, long epoch)
    {
        if (outcome is not (SessionMoveState.Committed or SessionMoveState.Aborted))
            throw new ArgumentException($"'{outcome}' is not the outcome of a move.", nameof(outcome));
        var path = DecisionPath(moveId);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var decision = new SessionMoveDecision(moveId, outcome, epoch, DateTimeOffset.UtcNow);
        try
        {
            // Created once: whoever writes first decides.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, decision, SessionMoveJsonContext.Default.SessionMoveDecision);
            stream.Flush(flushToDisk: true);
            return decision;
        }
        catch (IOException) when (File.Exists(path))
        {
            return ReadDecision(moveId) ?? throw new IOException($"The decision on the move {moveId} cannot be read.");
        }
    }

    /// <summary>Reads the decision of this machine, as arbiter, on a move.</summary>
    /// <param name="moveId">The move.</param>
    /// <returns>The decision; <see langword="null"/> when none was made.</returns>
    /// <exception cref="ArgumentException"><paramref name="moveId"/> is not valid.</exception>
    public SessionMoveDecision? ReadDecision(string moveId)
    {
        var path = DecisionPath(moveId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                // A decision being written by another process is empty for a moment.
                if (bytes.Length > 0) return JsonSerializer.Deserialize(bytes, SessionMoveJsonContext.Default.SessionMoveDecision);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // Read again below.
            }

            Thread.Sleep(20);
        }

        return null;
    }

    private string PathOf(string moveId, string role)
    {
        ValidateMoveId(moveId);
        if (role is not (SessionMoveRole.Source or SessionMoveRole.Target)) throw new ArgumentException($"'{role}' is not a side of a move.", nameof(role));
        return Path.Combine(Directory, moveId + "." + role + ".json");
    }

    private string DecisionPath(string moveId)
    {
        ValidateMoveId(moveId);
        return Path.Combine(Directory, "decisions", moveId + ".json");
    }

    /// <summary>Checks that a move id can name files: 1 to 128 characters, letters, digits, <c>-</c> and <c>_</c>.</summary>
    /// <param name="moveId">The move id.</param>
    /// <exception cref="ArgumentException">It cannot.</exception>
    public static void ValidateMoveId(string? moveId)
    {
        if (string.IsNullOrWhiteSpace(moveId) || moveId.Length > 128 || !moveId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"'{moveId}' is not a valid move id.", nameof(moveId));
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
