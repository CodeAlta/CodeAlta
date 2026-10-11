using System.Security.Cryptography;
using CodeAlta.Agent;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

/// <summary>An immutable Statistics payload. Only the Statistics engine interprets its versioned contents.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Generation">The immutable archive generation.</param>
/// <param name="PayloadSha256">The payload digest.</param>
/// <param name="Payload">The versioned, sanitized base facts, not a reducer checkpoint or journal.</param>
/// <param name="Source">The source generation the facts cover in full; null for facts migrated after the source disappeared.</param>
public sealed record StatisticsArchiveEntry(string SessionId, string Generation, string PayloadSha256, byte[] Payload, SessionStatisticsSource? Source);

/// <summary>The dedicated, irreplaceable Statistics archive of one StateRoot.</summary>
/// <remarks>Unlike <see cref="ApplicationDatabase"/>, this store never restores an older backup or recreates a damaged database.
/// SQLite rollback journaling and synchronous EXTRA persist payload and effective-generation intent together. All reads verify
/// integrity and payload digests. A flushed StateRoot marker outside the directory prevents its loss from looking like first use.
/// Keep the directory and its sibling .initialized marker in profile backups; physical loss requires explicit repair/restore.</remarks>
public sealed class StatisticsArchiveStore
{
    private const int ApplicationId = 0x43415341;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _initializationPath;

    /// <summary>Creates a store bound to the owning instance, without opening or changing storage.</summary>
    /// <param name="stateRoot">The trusted host StateRoot (not GlobalRoot in a developer instance).</param>
    /// <exception cref="ArgumentException">The root is empty.</exception>
    public StatisticsArchiveStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _directory = Path.Combine(Path.GetFullPath(stateRoot), "saved_statistics_after_deleted_sessions");
        _initializationPath = _directory + ".initialized";
    }

    /// <summary>Gets the archive database path, for diagnostics and backup.</summary>
    public string DatabasePath => Path.Combine(_directory, "archives.sqlite3");

    /// <summary>Reads the effective generation of each session, verifying every retained generation first.</summary>
    /// <param name="cancellationToken">Cancels waiting for the archive.</param>
    /// <returns>One effective generation per session.</returns>
    /// <exception cref="InvalidDataException">The archive is missing, corrupt or unsupported and requires repair/restore.</exception>
    public async ValueTask<IReadOnlyList<StatisticsArchiveEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            var entries = ReadAndVerify(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT generation FROM effective ORDER BY session_id";
            using var reader = command.ExecuteReader();
            var effective = new List<StatisticsArchiveEntry>();
            while (reader.Read())
            {
                if (!entries.TryGetValue(reader.GetString(0), out var entry))
                {
                    throw new InvalidDataException("Statistics archive has a missing effective generation; restore the archive.");
                }

                effective.Add(entry);
            }

            return effective;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Commits immutable facts and their effective-generation intent, then reopens and verifies the committed bytes.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="payload">The versioned Statistics payload.</param>
    /// <param name="source">The source generation the facts cover in full, or null for legacy deleted-session migration.</param>
    /// <param name="cancellationToken">Cancels before writing.</param>
    /// <returns>The reopened committed generation. Repeating identical content is idempotent.</returns>
    /// <exception cref="InvalidDataException">The archive is missing, corrupt or unsupported.</exception>
    /// <exception cref="ArgumentException">The session, payload or source identity is invalid.</exception>
    public async ValueTask<StatisticsArchiveEntry> SaveAsync(string sessionId, byte[] payload, SessionStatisticsSource? source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || (source is not null && (source.SessionId != sessionId || !IsValid(source))))
        {
            throw new ArgumentException("Invalid Statistics archive payload or source identity.");
        }

        // Copy before the first await: caller-owned buffers cannot change the bytes being committed.
        payload = payload.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string generation;
            using (var connection = Open())
            {
                var entries = ReadAndVerify(connection);
                generation = entries.Values.FirstOrDefault(entry => entry.SessionId == sessionId && entry.PayloadSha256 == digest && entry.Source == source)?.Generation
                    ?? Guid.NewGuid().ToString("N");
                cancellationToken.ThrowIfCancellationRequested();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction,
                    "INSERT OR IGNORE INTO archive (generation, session_id, digest, payload, source_path, source_length, source_stamp, source_digest) VALUES ($g,$s,$d,$p,$path,$length,$stamp,$hash)",
                    ("$g", generation), ("$s", sessionId), ("$d", digest), ("$p", payload), ("$path", source?.Path),
                    ("$length", source?.Length), ("$stamp", source?.LastWriteUtc.UtcTicks), ("$hash", source?.Sha256));
                Execute(connection, transaction, "INSERT INTO effective (session_id,generation) VALUES ($s,$g) ON CONFLICT(session_id) DO UPDATE SET source_deleted=CASE WHEN generation=excluded.generation THEN source_deleted ELSE 0 END, artifacts_remaining=CASE WHEN generation=excluded.generation THEN artifacts_remaining ELSE 0 END, generation=excluded.generation", ("$s", sessionId), ("$g", generation));
                transaction.Commit();
            }

            // A success from SQLite alone is not a receipt: reopen and verify what survived the commit.
            using var verify = Open();
            var saved = ReadAndVerify(verify)[generation];
            if (saved.PayloadSha256 != digest || saved.Source != source || !saved.Payload.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidDataException("Statistics archive verification failed after commit.");
            }

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records the owning runtime's confirmed deletion outcome without altering the immutable payload.</summary>
    /// <param name="receipt">The verified receipt.</param>
    /// <param name="artifactsRemaining">Whether attachment/trace cleanup was partial.</param>
    /// <param name="cancellationToken">Cancels before the transaction.</param>
    /// <returns>False when the effective receipt no longer matches.</returns>
    /// <exception cref="InvalidDataException">The archive is inconsistent and requires repair.</exception>
    public async ValueTask<bool> RecordDeletionAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            var entries = ReadAndVerify(connection);
            if (!entries.TryGetValue(receipt.Generation, out var entry) || entry.PayloadSha256 != receipt.PayloadSha256 || entry.Source != receipt.Source)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE effective SET source_deleted=1,artifacts_remaining=$remaining WHERE session_id=$s AND generation=$g";
            command.Parameters.AddWithValue("$remaining", artifactsRemaining ? 1 : 0);
            command.Parameters.AddWithValue("$s", entry.SessionId);
            command.Parameters.AddWithValue("$g", entry.Generation);
            var changed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open()
    {
        RejectLinks(_directory);
        RejectLinks(_initializationPath);
        var create = !Directory.Exists(_directory);
        if (create && Path.Exists(_initializationPath))
        {
            throw new InvalidDataException("Previously initialized Statistics archive directory is missing; repair/restore saved_statistics_after_deleted_sessions before continuing.");
        }

        if (!create && (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0))
        {
            throw new InvalidDataException("Statistics archive is missing or empty; repair/restore saved_statistics_after_deleted_sessions before continuing.");
        }

        RejectLinks(DatabasePath);
        RejectLinks(DatabasePath + "-journal");
        if (!File.Exists(_initializationPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
            // Persist evidence before creating storage or acknowledging any existing archive. An interrupted first
            // initialization fails closed too; neither a new store nor a restart may mistake loss for first use.
            using var marker = new FileStream(_initializationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);
            marker.Write("CodeAlta Statistics archive initialized\n"u8);
            marker.Flush(flushToDisk: true);
        }

        if (create)
        {
            Directory.CreateDirectory(_directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;");
            if (create)
            {
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, $"""
                    PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;
                    CREATE TABLE archive (generation TEXT PRIMARY KEY, session_id TEXT NOT NULL, digest TEXT NOT NULL, payload BLOB NOT NULL,
                      source_path TEXT, source_length INTEGER, source_stamp INTEGER, source_digest TEXT);
                    CREATE TABLE effective (session_id TEXT PRIMARY KEY, generation TEXT NOT NULL REFERENCES archive(generation), source_deleted INTEGER NOT NULL DEFAULT 0, artifacts_remaining INTEGER NOT NULL DEFAULT 0);
                    """);
                transaction.Commit();
            }

            if (Scalar(connection, "PRAGMA application_id") is not long id || id != ApplicationId || Scalar(connection, "PRAGMA user_version") is not long version || version != 1
                || !Equals(Scalar(connection, "PRAGMA integrity_check"), "ok"))
            {
                throw new InvalidDataException("Statistics archive is corrupt or unsupported; repair/restore it before continuing.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static Dictionary<string, StatisticsArchiveEntry> ReadAndVerify(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation,session_id,digest,payload,source_path,source_length,source_stamp,source_digest FROM archive";
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<string, StatisticsArchiveEntry>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(3);
            var digest = reader.GetString(2);
            if (!IsDigest(digest) || digest != Convert.ToHexString(SHA256.HashData(bytes)))
            {
                throw new InvalidDataException("Statistics archive payload digest mismatch; repair/restore it before continuing.");
            }

            var source = reader.IsDBNull(4) ? null : new SessionStatisticsSource(reader.GetString(1), reader.GetString(4), reader.GetInt64(5),
                new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero), reader.IsDBNull(7) ? null : reader.GetString(7));
            if (source is not null && !IsValid(source))
            {
                throw new InvalidDataException("Statistics archive source evidence is invalid.");
            }

            var entry = new StatisticsArchiveEntry(reader.GetString(1), reader.GetString(0), digest, bytes, source);
            entries.Add(entry.Generation, entry);
        }

        reader.Close();
        command.CommandText = "SELECT session_id,generation FROM effective";
        using var effective = command.ExecuteReader();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        while (effective.Read())
        {
            var session = effective.GetString(0);
            if (!entries.TryGetValue(effective.GetString(1), out var entry) || entry.SessionId != session || !sessions.Add(session))
            {
                throw new InvalidDataException("Statistics archive effective-generation metadata is inconsistent; repair/restore it.");
            }
        }

        if (!sessions.SetEquals(entries.Values.Select(entry => entry.SessionId)))
        {
            throw new InvalidDataException("Statistics archive is missing effective-generation metadata; repair/restore it.");
        }

        return entries;
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    // A source saved at retention is described, not read: it has no digest.
    private static bool IsValid(SessionStatisticsSource source) => source.Length >= 0 && (source.Sha256 is null || IsDigest(source.Sha256));

    private static object? Scalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string text, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked Statistics archive paths are not supported.");
            }
        }
    }
}
