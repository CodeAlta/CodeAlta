using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

/// <summary>
/// Keeps what the providers read of the files of conversations other programs started in the application database
/// of the instance, in the table <c>external_conversation_header</c>: one row per provider and file path, with the
/// length and the last write time the file had, the format version of the provider and its header as JSON.
/// </summary>
/// <remarks>
/// The rows are derived from the files of the other programs. A table that is damaged, or that a newer build wrote, is
/// dropped and made again empty; a database that is locked or cannot be used now is not waited for: the read gives no
/// rows and the write is not made, and the providers read the files instead.
/// </remarks>
public sealed class ExternalConversationHeaderStore : IAgentExternalConversationHeaderStore
{
    internal const string Owner = "external_conversation_headers";
    internal const string TablePrefix = "external_conversation_header";
    internal const int SchemaVersion = 1;
    private const string Table = "external_conversation_header";

    private readonly ApplicationDatabase _database;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    // The generation of the file whose table is known to exist; -1 when it has to be made or checked.
    private int _schemaGeneration = -1;

    /// <summary>Initializes a new instance of the <see cref="ExternalConversationHeaderStore"/> class.</summary>
    /// <param name="database">The application database of the instance, which the store borrows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public ExternalConversationHeaderStore(ApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentExternalConversationHeaderRecord>?> ReadAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return await RunAsync<IReadOnlyList<AgentExternalConversationHeaderRecord>?>(
                () => _database.ReadAsync(Owner, (connection, token) => ReadRowsAsync(connection, provider, token), recoverDamagedFile: false, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> WriteAsync(
        string provider,
        IReadOnlyCollection<AgentExternalConversationHeaderRecord> upserts,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removedPaths);
        if (upserts.Count == 0 && removedPaths.Count == 0)
        {
            return true;
        }

        return await RunAsync<bool?>(
                () => _database.WriteAsync<bool?>(Owner, async (connection, token) =>
                {
                    await WriteRowsAsync(connection, provider, upserts, removedPaths, token).ConfigureAwait(false);
                    return true;
                }, recoverDamagedFile: false, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false) ?? false;
    }

    // Runs an operation on the table, made first when it is missing; default when the database cannot be used now. A
    // damaged table, or one a newer build wrote, is made again empty and the operation runs once more.
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
                if (!await TryClearAsync(exception, generation, cancellationToken).ConfigureAwait(false))
                {
                    return default;
                }
            }
            catch (ApplicationDatabaseNewerVersionException)
            {
                if (!await TryClearAsync(failure: null, generation, cancellationToken).ConfigureAwait(false))
                {
                    return default;
                }
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
        if (Volatile.Read(ref _schemaGeneration) == _database.Generation && File.Exists(_database.DatabasePath))
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = _database.Generation;
            if (_schemaGeneration == generation && File.Exists(_database.DatabasePath))
            {
                return;
            }

            await _database.MigrateAsync(Owner, TablePrefix, SchemaVersion, static async (connection, _, _, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        CREATE TABLE IF NOT EXISTS {Table} (
                            provider TEXT NOT NULL,
                            path TEXT NOT NULL,
                            length INTEGER NOT NULL,
                            last_write_utc_ticks INTEGER NOT NULL,
                            format_version INTEGER NOT NULL,
                            importable INTEGER NOT NULL,
                            header_json TEXT NULL,
                            PRIMARY KEY (provider, path)
                        );
                        """;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }, recoverDamagedFile: false, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaGeneration, generation);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    // Drops the table, to be made again empty; the file itself is replaced when the damage is not in this table. False
    // when the database cannot be used now.
    private async Task<bool> TryClearAsync(SqliteException? failure, int generation, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _schemaGeneration, -1);
        try
        {
            try
            {
                await _database.DropTablesAsync(Owner, TablePrefix, recoverDamagedFile: false, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException again) when (ApplicationDatabase.IsDamaged(again))
            {
                await _database.RecoverDamagedFileAsync(failure ?? again, generation, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception) || ApplicationDatabase.IsDamaged(exception))
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async ValueTask<IReadOnlyList<AgentExternalConversationHeaderRecord>?> ReadRowsAsync(
        SqliteConnection connection,
        string provider,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT path, length, last_write_utc_ticks, format_version, importable, header_json FROM {Table} WHERE provider = $provider;";
        command.Parameters.AddWithValue("$provider", provider);
        var rows = new List<AgentExternalConversationHeaderRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AgentExternalConversationHeaderRecord(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static async Task WriteRowsAsync(
        SqliteConnection connection,
        string provider,
        IReadOnlyCollection<AgentExternalConversationHeaderRecord> upserts,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken cancellationToken)
    {
        if (upserts.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {Table} (provider, path, length, last_write_utc_ticks, format_version, importable, header_json)
                VALUES ($provider, $path, $length, $ticks, $version, $importable, $header)
                ON CONFLICT (provider, path) DO UPDATE SET
                    length = excluded.length,
                    last_write_utc_ticks = excluded.last_write_utc_ticks,
                    format_version = excluded.format_version,
                    importable = excluded.importable,
                    header_json = excluded.header_json;
                """;
            command.Parameters.AddWithValue("$provider", provider);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var length = command.Parameters.Add("$length", SqliteType.Integer);
            var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
            var version = command.Parameters.Add("$version", SqliteType.Integer);
            var importable = command.Parameters.Add("$importable", SqliteType.Integer);
            var header = command.Parameters.Add("$header", SqliteType.Text);
            foreach (var row in upserts)
            {
                path.Value = row.Path;
                length.Value = row.Length;
                ticks.Value = row.LastWriteUtcTicks;
                version.Value = row.FormatVersion;
                importable.Value = row.Importable ? 1 : 0;
                header.Value = row.HeaderJson is null ? DBNull.Value : row.HeaderJson;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (removedPaths.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {Table} WHERE provider = $provider AND path = $path;";
            command.Parameters.AddWithValue("$provider", provider);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var removed in removedPaths)
            {
                path.Value = removed;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
