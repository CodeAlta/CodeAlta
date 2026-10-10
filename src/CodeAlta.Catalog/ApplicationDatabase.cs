using System.Globalization;
using Microsoft.Data.Sqlite;
using XenoAtom.Logging;

namespace CodeAlta.Catalog;

/// <summary>
/// Runs the steps that bring the tables of one owner of the application database from the version recorded for
/// it to the version the owner now needs.
/// </summary>
/// <param name="connection">The connection of the write transaction the steps run in.</param>
/// <param name="fromVersion">The version recorded for the owner; 0 when it has no tables yet.</param>
/// <param name="toVersion">The version the owner asked for.</param>
/// <param name="cancellationToken">A token to cancel the migration.</param>
/// <returns>A task representing the migration.</returns>
public delegate ValueTask ApplicationDatabaseMigration(
    SqliteConnection connection,
    int fromVersion,
    int toVersion,
    CancellationToken cancellationToken);

/// <summary>
/// What the application database did when its file was found damaged.
/// </summary>
/// <param name="OccurredAt">When the file was replaced.</param>
/// <param name="MovedAsidePath">Where the damaged file was moved to, or <see langword="null"/> when it could not be kept.</param>
/// <param name="RestoredFromCopy">The copy the new file was restored from, or <see langword="null"/> when it started empty.</param>
/// <param name="Reason">The error of SQLite that revealed the damage.</param>
public sealed record ApplicationDatabaseRecovery(
    DateTimeOffset OccurredAt,
    string? MovedAsidePath,
    string? RestoredFromCopy,
    string Reason);

/// <summary>
/// The SQLite database of one CodeAlta instance: the list of sessions and the tables the plugins keep.
/// </summary>
/// <remarks>
/// <para>
/// The file lives in the state folder of the instance (<see cref="CatalogOptions.ApplicationDatabasePath"/>), so the
/// developer instance has a database of its own. It is no longer a cache that may be deleted: it holds data that
/// cannot be built again. The service therefore never deletes it. The write-ahead log is on, so readers never wait
/// for a writer; <see cref="WriteAsync{T}"/> runs one transaction at a time through a queue the service owns, so no
/// writer meets another one; a file that is damaged is moved aside and replaced by its last copy (kept in
/// <see cref="ApplicationDatabaseOptions.BackupDirectory"/>), or by an empty file.
/// </para>
/// <para>
/// Every owner (the list of sessions, a plugin) names itself and records the version of its tables with
/// <see cref="MigrateAsync"/>. Connections are opened for one operation and closed with it.
/// </para>
/// </remarks>
public sealed class ApplicationDatabase : IApplicationDatabase
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const string MetaTable = "app_meta";
    private const string BackupFilePrefix = "alta-";
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int CorruptFilesToKeep = 3;

    private readonly ApplicationDatabaseOptions _options;
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Database");
    private readonly AsyncFifoLock _writeQueue = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _backupDirectory;
    private readonly object _maintenanceGate = new();
    private Task? _maintenance;
    private bool _ready;
    private int _generation;
    private int _disposed;
    private ApplicationDatabaseRecovery? _lastRecovery;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationDatabase"/> class. The file is opened on first use.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The database path is empty.</exception>
    public ApplicationDatabase(ApplicationDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        _options = options;
        DatabasePath = Path.GetFullPath(options.DatabasePath);
        _backupDirectory = string.IsNullOrWhiteSpace(options.BackupDirectory)
            ? Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups")
            : Path.GetFullPath(options.BackupDirectory);
    }

    /// <summary>
    /// Creates the database of an instance, in the location its catalog options name.
    /// </summary>
    /// <param name="options">The catalog options of the instance.</param>
    /// <returns>The database, not yet opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static ApplicationDatabase Create(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            LegacyDatabasePath = options.LegacySessionCacheDatabasePath,
            BackupDirectory = options.ApplicationDatabaseBackupRoot,
        });
    }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public int Generation => Volatile.Read(ref _generation);

    /// <inheritdoc />
    public ApplicationDatabaseRecovery? LastRecovery => Volatile.Read(ref _lastRecovery);

    /// <summary>Gets the folder that holds the copies of the database.</summary>
    public string BackupDirectory => _backupDirectory;

    /// <inheritdoc />
    public ValueTask<T> ReadAsync<T>(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask<T>> read,
        CancellationToken cancellationToken = default)
        => ReadAsync(owner, read, recoverDamagedFile: true, cancellationToken);

    /// <summary>
    /// Reads on a connection of its own.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="owner">The owner, for diagnostics.</param>
    /// <param name="read">The read.</param>
    /// <param name="recoverDamagedFile">
    /// Whether a damaged file is replaced, and the read run once more, when SQLite reports it. An owner of data it
    /// can rebuild passes <see langword="false"/> to clear its own tables first (see <see cref="RecoverDamagedFileAsync"/>).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The database was disposed.</exception>
    /// <exception cref="SqliteException">SQLite failed; a lock that outlasts the busy timeout is error 5 or 6.</exception>
    public async ValueTask<T> ReadAsync<T>(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask<T>> read,
        bool recoverDamagedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 0; ; attempt++)
        {
            ThrowIfDisposed();
            var generation = await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenAsync(readOnly: true, cancellationToken).ConfigureAwait(false);
                return await read(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (recoverDamagedFile && attempt == 0 && IsDamaged(exception))
            {
                await RecoverDamagedFileAsync(exception, generation, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await WriteAsync<object?>(
                owner,
                async (connection, token) =>
                {
                    await write(connection, token).ConfigureAwait(false);
                    return null;
                },
                recoverDamagedFile: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes in one transaction, in turn with the other writers of the application.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="owner">The owner, for diagnostics and for the log of a write that holds the queue too long.</param>
    /// <param name="write">The write. It runs in a transaction that is committed when it returns and rolled back when it throws.</param>
    /// <param name="recoverDamagedFile">See <see cref="ReadAsync{T}(string, Func{SqliteConnection, CancellationToken, ValueTask{T}}, bool, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write. A write that is still queued when it is canceled never runs.</param>
    /// <returns>The result of <paramref name="write"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The database was disposed.</exception>
    /// <exception cref="SqliteException">SQLite failed; a lock that outlasts the busy timeout is error 5 or 6.</exception>
    public async ValueTask<T> WriteAsync<T>(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask<T>> write,
        bool recoverDamagedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(write);
        for (var attempt = 0; ; attempt++)
        {
            ThrowIfDisposed();
            var generation = await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var turn = await _writeQueue.AcquireAsync(cancellationToken).ConfigureAwait(false);
                return await RunWriteAsync(owner, write, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (recoverDamagedFile && attempt == 0 && IsDamaged(exception))
            {
                await RecoverDamagedFileAsync(exception, generation, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> GetVersionAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return await ReadAsync(
                owner,
                (connection, token) => ReadVersionAsync(connection, owner, transaction: null, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask MigrateAsync(
        string owner,
        string? tablePrefix,
        int version,
        ApplicationDatabaseMigration migrate,
        CancellationToken cancellationToken = default)
        => MigrateAsync(owner, tablePrefix, version, migrate, recoverDamagedFile: true, cancellationToken);

    /// <summary>
    /// Brings the tables of an owner to a version, once, in one write transaction.
    /// </summary>
    /// <param name="owner">The owner, such as <c>session_cache</c> or <c>plugin:statistics</c>.</param>
    /// <param name="tablePrefix">
    /// The prefix every table, index, view and trigger the migration creates, changes or drops must start with, or
    /// <see langword="null"/> for an owner that is part of the application and is trusted.
    /// </param>
    /// <param name="version">The version the owner needs; at least 1.</param>
    /// <param name="migrate">The steps. They run when the recorded version is lower than <paramref name="version"/>.</param>
    /// <param name="recoverDamagedFile">See <see cref="ReadAsync{T}(string, Func{SqliteConnection, CancellationToken, ValueTask{T}}, bool, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">A token to cancel the migration.</param>
    /// <returns>A task representing the migration.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="tablePrefix"/> is empty, or <paramref name="version"/> is below 1.</exception>
    /// <exception cref="InvalidOperationException">
    /// The recorded version is higher than <paramref name="version"/> (the tables were written by a newer build), or
    /// the migration touched an object that does not start with <paramref name="tablePrefix"/>. The transaction is
    /// rolled back and the version is not recorded.
    /// </exception>
    public async ValueTask MigrateAsync(
        string owner,
        string? tablePrefix,
        int version,
        ApplicationDatabaseMigration migrate,
        bool recoverDamagedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (tablePrefix is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);
        }

        await WriteAsync<object?>(
                owner,
                async (connection, token) =>
                {
                    var recorded = await ReadVersionAsync(connection, owner, transaction: null, token).ConfigureAwait(false);
                    if (recorded == version)
                    {
                        return null;
                    }

                    if (recorded > version)
                    {
                        throw new InvalidOperationException(
                            $"The tables of '{owner}' are at version {recorded}, newer than the version {version} this build knows. They were written by a newer build; nothing was changed.");
                    }

                    var before = tablePrefix is null ? null : await ReadSchemaAsync(connection, token).ConfigureAwait(false);
                    await migrate(connection, recorded, version, token).ConfigureAwait(false);
                    if (before is not null)
                    {
                        var after = await ReadSchemaAsync(connection, token).ConfigureAwait(false);
                        var offending = FindForeignChanges(before, after, tablePrefix!);
                        if (offending.Count > 0)
                        {
                            throw new InvalidOperationException(
                                $"The migration of '{owner}' to version {version} changed objects that do not start with its prefix '{tablePrefix}': {string.Join(", ", offending)}. It was rolled back.");
                        }
                    }

                    await WriteVersionAsync(connection, owner, version, token).ConfigureAwait(false);
                    return null;
                },
                recoverDamagedFile,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string tablePrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);
        return await ReadAsync(
                "database",
                async (connection, token) =>
                {
                    var objects = await ReadSchemaAsync(connection, token).ConfigureAwait(false);
                    return (IReadOnlyList<string>)objects
                        .Where(item => item.Type == "table" && item.Name.StartsWith(tablePrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(static item => item.Name)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> DropTablesAsync(string owner, string tablePrefix, CancellationToken cancellationToken = default)
        => DropTablesAsync(owner, tablePrefix, recoverDamagedFile: true, cancellationToken);

    /// <summary>
    /// Drops every table, view, index and trigger of an owner and forgets its version.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="tablePrefix">The prefix of its objects.</param>
    /// <param name="recoverDamagedFile">See <see cref="ReadAsync{T}(string, Func{SqliteConnection, CancellationToken, ValueTask{T}}, bool, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The names of the tables that were dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="tablePrefix"/> is empty.</exception>
    public async ValueTask<IReadOnlyList<string>> DropTablesAsync(
        string owner,
        string tablePrefix,
        bool recoverDamagedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);
        return await WriteAsync<IReadOnlyList<string>>(
                owner,
                async (connection, token) =>
                {
                    var objects = await ReadSchemaAsync(connection, token).ConfigureAwait(false);
                    var owned = objects.Where(item => item.Name.StartsWith(tablePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var item in owned.Where(static item => item.Type is "view" or "trigger"))
                    {
                        await ExecuteAsync(connection, $"DROP {item.Type.ToUpperInvariant()} IF EXISTS {Quote(item.Name)};", token).ConfigureAwait(false);
                    }

                    var tables = owned.Where(static item => item.Type == "table").Select(static item => item.Name).ToArray();
                    foreach (var table in tables)
                    {
                        await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Quote(table)};", token).ConfigureAwait(false);
                    }

                    await using var command = connection.CreateCommand();
                    command.CommandText = $"DELETE FROM {MetaTable} WHERE owner = $owner;";
                    command.Parameters.AddWithValue("$owner", owner);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return tables;
                },
                recoverDamagedFile,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a damaged file aside and replaces it by the newest copy that is valid, or by an empty file.
    /// </summary>
    /// <param name="failure">The error of SQLite that revealed the damage.</param>
    /// <param name="observedGeneration">The <see cref="Generation"/> the failing operation ran with; a file that was replaced since is left as it is.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the write queue.</param>
    /// <returns>A task that completes when the file is replaced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The damaged file cannot be moved aside.</exception>
    public async ValueTask RecoverDamagedFileAsync(SqliteException failure, int observedGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ThrowIfDisposed();
        using var turn = await _writeQueue.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (Generation != observedGeneration)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReplaceDamagedFile(failure);
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Makes a copy of the database now, with the online backup of SQLite: readers and writers go on while it is made.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the copy.</param>
    /// <returns>The path of the copy, or <see langword="null"/> when the database is damaged and nothing was copied.</returns>
    /// <exception cref="ObjectDisposedException">The database was disposed.</exception>
    public async ValueTask<string?> BackupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_backupDirectory);
        var now = _options.TimeProvider.GetUtcNow();
        var path = Path.Combine(_backupDirectory, BackupFilePrefix + now.UtcDateTime.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture) + ".sqlite3");
        var temporary = path + ".tmp";
        DeleteIfExists(temporary);
        try
        {
            await using (var source = await OpenAsync(readOnly: true, cancellationToken).ConfigureAwait(false))
            {
                await Task.Run(() =>
                {
                    using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString());
                    destination.Open();
                    source.BackupDatabase(destination);
                    // One file, without a log of its own.
                    using var command = destination.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode = DELETE;";
                    command.ExecuteNonQuery();
                }, cancellationToken).ConfigureAwait(false);
            }

            if (!IsHealthy(temporary))
            {
                if (LogManager.IsInitialized) _logger.Warn($"The copy of the application database was not kept: it is not valid. The database may be damaged: {DatabasePath}");
                DeleteIfExists(temporary);
                return null;
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            DeleteIfExists(temporary);
            throw;
        }

        PruneBackups();
        return path;
    }

    /// <summary>
    /// Makes a copy when the newest one is older than <see cref="ApplicationDatabaseOptions.BackupInterval"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the copy.</param>
    /// <returns>The path of the new copy, or <see langword="null"/> when none was due.</returns>
    public async ValueTask<string?> BackupIfDueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var backups = GetBackups();
        if (backups.Count > 0 && _options.TimeProvider.GetUtcNow() - backups[0].Timestamp < _options.BackupInterval)
        {
            return null;
        }

        return await BackupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the copies of the database, the newest first.
    /// </summary>
    /// <returns>The paths of the copies.</returns>
    public IReadOnlyList<string> GetBackupPaths() => [.. GetBackups().Select(static backup => backup.Path)];

    /// <summary>
    /// Starts the upkeep a host owns: a copy of the database at start when the last one is older than a day, and
    /// then at most once a day while the host runs. Calling it again does nothing.
    /// </summary>
    public void StartMaintenance()
    {
        ThrowIfDisposed();
        lock (_maintenanceGate)
        {
            _maintenance ??= RunMaintenanceAsync(_lifetime.Token);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_maintenance is { } maintenance)
        {
            try
            {
                await maintenance.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The queue is closed behind every write that was admitted: shutdown waits for them.
        using var turn = await _writeQueue.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.MaintenanceStartDelay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (await BackupIfDueAsync(cancellationToken).ConfigureAwait(false) is { } path)
                    {
                        if (LogManager.IsInitialized) _logger.Info($"A copy of the application database was made: {path}");
                    }
                }
                catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
                {
                    if (LogManager.IsInitialized) _logger.Warn($"The copy of the application database failed: {exception.Message}");
                }

                await Task.Delay(_options.MaintenanceInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async ValueTask<T> RunWriteAsync<T>(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask<T>> write,
        CancellationToken cancellationToken)
    {
        var started = _options.TimeProvider.GetTimestamp();
        try
        {
            await using var connection = await OpenAsync(readOnly: false, cancellationToken).ConfigureAwait(false);
            // IMMEDIATE: the lock is taken, and waited for, up front; a deferred transaction that upgrades later fails at once.
            // The transaction is plain SQL, not a transaction object of the provider: the commands of the write do not
            // have to name it.
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await write(connection, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, "COMMIT;", CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            var elapsed = _options.TimeProvider.GetElapsedTime(started);
            if (elapsed >= _options.SlowWriteThreshold)
            {
                if (LogManager.IsInitialized) _logger.Warn($"A write of '{owner}' held the application database queue for {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s. Writes should be short: commit more often.");
            }
        }
    }

    // Opens the file when needed and returns its generation: the number of times it was replaced.
    private async ValueTask<int> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ready) && File.Exists(DatabasePath))
        {
            return Generation;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _ready) && File.Exists(DatabasePath))
            {
                return Generation;
            }

            if (Volatile.Read(ref _ready))
            {
                // The file was deleted under the service: whatever is made again is a new file, and the owners say so
                // by comparing the generation they saw.
                Interlocked.Increment(ref _generation);
                Volatile.Write(ref _ready, false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            TakeOverLegacyFile();
            try
            {
                await InitializeFileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (IsDamaged(exception))
            {
                ReplaceDamagedFile(exception);
                await InitializeFileAsync(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _ready, true);
            return Generation;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task InitializeFileAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(readOnly: false, cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            // The mode is kept in the file: it is set once, and every later connection finds it.
            command.CommandText = "PRAGMA journal_mode = WAL;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
                connection,
                $"CREATE TABLE IF NOT EXISTS {MetaTable} (owner TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, version INTEGER NOT NULL, updated_utc_ticks INTEGER NOT NULL);",
                cancellationToken)
            .ConfigureAwait(false);
    }

    // The session list lived in cache/cache.sqlite3 before this service: the file is moved, not rebuilt from the
    // journals, which can take minutes for a large history. Only the file of this instance's state folder is read.
    private void TakeOverLegacyFile()
    {
        var legacy = _options.LegacyDatabasePath;
        if (string.IsNullOrWhiteSpace(legacy) || File.Exists(DatabasePath))
        {
            return;
        }

        legacy = Path.GetFullPath(legacy);
        if (!File.Exists(legacy) || string.Equals(legacy, DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                if (File.Exists(legacy + suffix))
                {
                    File.Move(legacy + suffix, DatabasePath + suffix);
                    moved.Add((legacy + suffix, DatabasePath + suffix));
                }
            }

            if (LogManager.IsInitialized) _logger.Info($"The session database moved from {legacy} to {DatabasePath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be moved is left where it was; the database starts empty and the list of sessions is built again.
            foreach (var (from, to) in moved)
            {
                try { File.Move(to, from); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { DeleteIfExists(to); }
            }

            if (LogManager.IsInitialized) _logger.Warn($"The session database could not be moved from {legacy}: {exception.Message}. A new database starts at {DatabasePath}.");
        }
    }

    private void ReplaceDamagedFile(SqliteException failure)
    {
        var timestamp = _options.TimeProvider.GetUtcNow();
        string? movedAside = DatabasePath + ".corrupt-" + timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(DatabasePath))
            {
                File.Move(DatabasePath, movedAside, overwrite: true);
            }
            else
            {
                movedAside = null;
            }

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                // The log of a damaged file is of no use to a new one.
                if (File.Exists(DatabasePath + suffix))
                {
                    if (movedAside is not null) File.Move(DatabasePath + suffix, movedAside + suffix, overwrite: true);
                    else File.Delete(DatabasePath + suffix);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (LogManager.IsInitialized) _logger.Error($"The damaged application database {DatabasePath} could not be moved aside: {exception.Message}");
            throw;
        }

        string? restored = null;
        foreach (var backup in GetBackups())
        {
            var candidate = backup.Path + ".restore";
            try
            {
                File.Copy(backup.Path, candidate, overwrite: true);
                if (!IsHealthy(candidate))
                {
                    DeleteIfExists(candidate);
                    continue;
                }

                File.Move(candidate, DatabasePath, overwrite: true);
                restored = backup.Path;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                DeleteIfExists(candidate);
                if (LogManager.IsInitialized) _logger.Warn($"The copy {backup.Path} of the application database could not be restored: {exception.Message}");
            }
        }

        Volatile.Write(ref _ready, false);
        Interlocked.Increment(ref _generation);
        var recovery = new ApplicationDatabaseRecovery(timestamp, movedAside, restored, failure.Message);
        Volatile.Write(ref _lastRecovery, recovery);
        PruneCorruptFiles();
        if (LogManager.IsInitialized) _logger.Error(restored is not null
            ? $"The application database {DatabasePath} was damaged ({failure.Message}). It was moved to {movedAside} and replaced by the copy {restored}: what was written since that copy is lost, and the list of sessions is built again from the journals."
            : $"The application database {DatabasePath} was damaged ({failure.Message}). It was moved to {movedAside} and replaced by an empty database: the list of sessions is built again from the journals, and the tables of the plugins start empty.");
    }

    private async Task<SqliteConnection> OpenAsync(bool readOnly, CancellationToken cancellationToken)
    {
        var timeout = _options.BusyTimeout;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)),
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {(int)timeout.TotalMilliseconds}; PRAGMA synchronous = NORMAL;"
                + (readOnly ? " PRAGMA query_only = ON;" : string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsHealthy(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async ValueTask<int> ReadVersionAsync(
        SqliteConnection connection,
        string owner,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT version FROM {MetaTable} WHERE owner = $owner;";
        command.Parameters.AddWithValue("$owner", owner);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;
    }

    private async ValueTask WriteVersionAsync(SqliteConnection connection, string owner, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {MetaTable} (owner, version, updated_utc_ticks) VALUES ($owner, $version, $ticks)
            ON CONFLICT(owner) DO UPDATE SET version = excluded.version, updated_utc_ticks = excluded.updated_utc_ticks;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$ticks", _options.TimeProvider.GetUtcNow().UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<SchemaObject>> ReadSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, COALESCE(sql, '') FROM sqlite_master;";
        var result = new List<SchemaObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    // What a migration created, dropped or altered outside the names of its owner. The automatic objects of SQLite
    // (the index of a unique key, the counter of AUTOINCREMENT, the statistics) follow their table.
    private static List<string> FindForeignChanges(IReadOnlyList<SchemaObject> before, IReadOnlyList<SchemaObject> after, string prefix)
    {
        var offending = new List<string>();
        var previous = before.ToDictionary(static item => item.Name, StringComparer.OrdinalIgnoreCase);
        var current = after.ToDictionary(static item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in after)
        {
            if (previous.TryGetValue(item.Name, out var old) && old == item)
            {
                continue;
            }

            if (!IsOwnedName(item.Name, prefix)) offending.Add(item.Name);
        }

        foreach (var item in before)
        {
            if (!current.ContainsKey(item.Name) && !IsOwnedName(item.Name, prefix)) offending.Add(item.Name);
        }

        return offending.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsOwnedName(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sqlite_autoindex_" + prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sqlite_sequence", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sqlite_stat", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RollbackAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // SQLite already rolled back (a failed commit, a damaged file): the connection is closed next.
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private List<(string Path, DateTimeOffset Timestamp)> GetBackups()
    {
        var result = new List<(string Path, DateTimeOffset Timestamp)>();
        if (!Directory.Exists(_backupDirectory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_backupDirectory, BackupFilePrefix + "*.sqlite3"))
        {
            var stem = Path.GetFileNameWithoutExtension(file)[BackupFilePrefix.Length..];
            if (DateTimeOffset.TryParseExact(stem, BackupTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                result.Add((file, timestamp));
            }
        }

        result.Sort(static (left, right) => right.Timestamp.CompareTo(left.Timestamp));
        return result;
    }

    private void PruneBackups()
    {
        foreach (var backup in GetBackups().Skip(Math.Max(1, _options.BackupsToKeep)))
        {
            try
            {
                File.Delete(backup.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (LogManager.IsInitialized) _logger.Warn($"The old copy {backup.Path} of the application database could not be deleted: {exception.Message}");
            }
        }
    }

    private void PruneCorruptFiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath)!;
            var damaged = Directory.EnumerateFiles(directory, Path.GetFileName(DatabasePath) + ".corrupt-*")
                .Where(static file => !file.EndsWith("-wal", StringComparison.Ordinal) && !file.EndsWith("-shm", StringComparison.Ordinal) && !file.EndsWith("-journal", StringComparison.Ordinal))
                .OrderByDescending(static file => file, StringComparer.Ordinal)
                .Skip(CorruptFilesToKeep);
            foreach (var file in damaged)
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
                {
                    DeleteIfExists(file + suffix);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Old damaged files left behind are harmless.
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that stays is overwritten the next time.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Gets whether SQLite reported that the file is not a database, or is malformed.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns><see langword="true"/> for error 11 (malformed) and 26 (not a database).</returns>
    public static bool IsDamaged(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;
    }

    /// <summary>
    /// Gets whether SQLite reported that another connection holds a lock past the busy timeout.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns><see langword="true"/> for error 5 (busy) and 6 (locked).</returns>
    public static bool IsLocked(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is SqliteBusy or SqliteLocked;
    }

    private readonly record struct SchemaObject(string Type, string Name, string Sql);
}
