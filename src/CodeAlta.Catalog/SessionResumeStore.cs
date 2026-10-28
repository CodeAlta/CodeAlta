using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

/// <summary>A resume a session waits for: the run that stopped on a limit goes on by itself at that time.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ResumeId">The identity of the resume, which its card and its actions name.</param>
/// <param name="ResumeAt">When the session resumes: the earliest reset of a limit of its tier.</param>
/// <param name="ProviderKey">The provider whose limit resets first.</param>
/// <param name="Attempt">How many times in a row the session resumed by itself before, this one included.</param>
/// <param name="From">The model the run stopped on, as <c>provider:model@reasoning</c>.</param>
/// <param name="Reason">The kind of failure that stopped the run.</param>
public sealed record SessionResumeRecord(string SessionId, string ResumeId, DateTimeOffset ResumeAt, string ProviderKey, int Attempt, string From, string Reason);

/// <summary>
/// Keeps the resumes the sessions of the instance wait for in the application database, in a table of their own, so
/// that a resume outlives a restart. A row is written when the resume is scheduled and removed when it fires or is
/// cancelled.
/// </summary>
/// <remarks>
/// A resume is a schedule, not a fact of the history of a session: the journal keeps the cards that say it was
/// scheduled, started or cancelled. A database that cannot be used now reads no row and writes none: the resumes of the
/// running instance still fire, and only a restart forgets them.
/// </remarks>
public sealed class SessionResumeStore
{
    internal const string Owner = "session_resumes";
    internal const string TablePrefix = "session_resume";
    internal const int SchemaVersion = 1;
    private const string Table = "session_resume";

    private readonly IApplicationDatabase _database;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    // The generation of the file whose table is known to exist; -1 when it has to be made or checked.
    private int _schemaGeneration = -1;

    /// <summary>Initializes a new instance of the <see cref="SessionResumeStore"/> class.</summary>
    /// <param name="database">The application database of the instance, which the store borrows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public SessionResumeStore(IApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads every resume that waits. A row that does not read as a resume is left out.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The resumes; none when the database cannot be used now.</returns>
    public async Task<IReadOnlyList<SessionResumeRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
        => await RunAsync<IReadOnlyList<SessionResumeRecord>>(() => _database.ReadAsync(Owner, static async (connection, token) =>
        {
            var rows = new List<SessionResumeRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT session_id, resume_id, resume_at, provider_key, attempt, from_ref, reason FROM {Table};";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!DateTimeOffset.TryParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                rows.Add(new(reader.GetString(0), reader.GetString(1), at, reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6)));
            }

            return (IReadOnlyList<SessionResumeRecord>)rows;
        }, cancellationToken), cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Writes the resume a session waits for, in the place of the one it had.</summary>
    /// <param name="record">The resume.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Whether the row was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public async Task<bool> SaveAsync(SessionResumeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return await RunAsync<bool?>(async () =>
        {
            await _database.WriteAsync(Owner, async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    INSERT INTO {Table} (session_id, resume_id, resume_at, provider_key, attempt, from_ref, reason)
                    VALUES ($session, $resume, $at, $provider, $attempt, $from, $reason)
                    ON CONFLICT (session_id) DO UPDATE SET
                        resume_id = excluded.resume_id,
                        resume_at = excluded.resume_at,
                        provider_key = excluded.provider_key,
                        attempt = excluded.attempt,
                        from_ref = excluded.from_ref,
                        reason = excluded.reason;
                    """;
                command.Parameters.AddWithValue("$session", record.SessionId);
                command.Parameters.AddWithValue("$resume", record.ResumeId);
                command.Parameters.AddWithValue("$at", record.ResumeAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$provider", record.ProviderKey);
                command.Parameters.AddWithValue("$attempt", record.Attempt);
                command.Parameters.AddWithValue("$from", record.From);
                command.Parameters.AddWithValue("$reason", record.Reason);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false) ?? false;
    }

    /// <summary>Removes the resume of a session, when it is the one named.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="resumeId">The resume; a session that waits for another one keeps it.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Whether the database could be used.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="resumeId"/> is blank.</exception>
    public async Task<bool> RemoveAsync(string sessionId, string resumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeId);
        return await RunAsync<bool?>(async () =>
        {
            await _database.WriteAsync(Owner, async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {Table} WHERE session_id = $session AND resume_id = $resume;";
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$resume", resumeId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false) ?? false;
    }

    // Runs an operation on the table, made first when it is missing; default when the database cannot be used now. A
    // damaged table, or one a newer build wrote, is made again empty: a resume is lost, the session is not.
    private async Task<T?> RunAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var generation = _database.Generation;
            try
            {
                await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException exception) when (ApplicationDatabase.IsDamaged(exception))
            {
                if (!await TryClearAsync(cancellationToken).ConfigureAwait(false)) return default;
            }
            catch (ApplicationDatabaseNewerVersionException)
            {
                if (!await TryClearAsync(cancellationToken).ConfigureAwait(false)) return default;
            }
            catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
            {
                return default;
            }
            catch (SqliteException) when (_database.Generation != generation)
            {
                // The file was replaced while the operation ran: the table is made in the new one.
                Volatile.Write(ref _schemaGeneration, -1);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The file cannot be used now, or the database was closed with the application.
                return default;
            }
        }

        return default;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaGeneration) == _database.Generation) return;
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = _database.Generation;
            if (_schemaGeneration == generation) return;
            await _database.MigrateAsync(Owner, TablePrefix, SchemaVersion, static async (connection, _, _, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS {Table} (
                        session_id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                        resume_id TEXT NOT NULL,
                        resume_at TEXT NOT NULL,
                        provider_key TEXT NOT NULL,
                        attempt INTEGER NOT NULL,
                        from_ref TEXT NOT NULL,
                        reason TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaGeneration, generation);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task<bool> TryClearAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _schemaGeneration, -1);
        try
        {
            await _database.DropTablesAsync(Owner, TablePrefix, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }
}
