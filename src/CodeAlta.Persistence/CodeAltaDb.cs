using Microsoft.Data.Sqlite;

namespace CodeAlta.Persistence;

/// <summary>
/// Provides SQLite connection management and migration support.
/// </summary>
public sealed class CodeAltaDb
{
    private static readonly IReadOnlyList<DbMigration> Migrations =
    [
        new DbMigration(
            "0001_initial",
            """
            CREATE TABLE IF NOT EXISTS projects (
                project_id TEXT PRIMARY KEY,
                path TEXT,
                checkout_path TEXT,
                git_root TEXT
            );

            CREATE TABLE IF NOT EXISTS agents (
                agent_id TEXT PRIMARY KEY,
                role TEXT NOT NULL,
                scope_kind TEXT NOT NULL,
                scope_id TEXT NULL,
                backend_id TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agent_sessions (
                session_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                backend_session_id TEXT NULL,
                created_at TEXT NOT NULL,
                last_used_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                project_id TEXT NULL,
                parent_task_id TEXT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                assigned_agent_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS task_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                payload_json TEXT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS artifacts (
                artifact_id TEXT PRIMARY KEY,
                uri TEXT NOT NULL,
                project_id TEXT NULL,
                type TEXT NOT NULL,
                path TEXT NOT NULL,
                frontmatter_json TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS artifact_links (
                from_artifact_id TEXT NOT NULL,
                to_kind TEXT NOT NULL,
                to_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS documents (
                document_id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                project_id TEXT NULL,
                title TEXT NULL,
                mime_type TEXT NULL,
                text TEXT NOT NULL,
                text_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
                document_id UNINDEXED,
                title,
                text
            );

            CREATE TABLE IF NOT EXISTS document_embeddings (
                document_id INTEGER PRIMARY KEY,
                model_id TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                embedding_blob BLOB NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_project_updated
                ON tasks(project_id, updated_at);
            CREATE INDEX IF NOT EXISTS idx_task_events_task
                ON task_events(task_id, created_at);
            CREATE INDEX IF NOT EXISTS idx_artifacts_project_type
                ON artifacts(project_id, type, updated_at);
            CREATE INDEX IF NOT EXISTS idx_documents_source
                ON documents(source_kind, source_id);
            """
        ),
        new DbMigration(
            "0002_remove_workspace_scope",
            """
            PRAGMA foreign_keys = OFF;

            DROP INDEX IF EXISTS idx_tasks_workspace_project;
            DROP INDEX IF EXISTS idx_artifacts_scope_type;

            ALTER TABLE projects RENAME TO projects_old;
            CREATE TABLE projects (
                project_id TEXT PRIMARY KEY,
                path TEXT,
                checkout_path TEXT,
                git_root TEXT
            );
            INSERT INTO projects(project_id, path, checkout_path, git_root)
            SELECT project_id, path, checkout_path, git_root
            FROM projects_old;
            DROP TABLE projects_old;

            ALTER TABLE tasks RENAME TO tasks_old;
            CREATE TABLE tasks (
                task_id TEXT PRIMARY KEY,
                project_id TEXT NULL,
                parent_task_id TEXT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                assigned_agent_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO tasks(task_id, project_id, parent_task_id, title, status, assigned_agent_id, created_at, updated_at)
            SELECT task_id, project_id, parent_task_id, title, status, assigned_agent_id, created_at, updated_at
            FROM tasks_old;
            DROP TABLE tasks_old;

            ALTER TABLE artifacts RENAME TO artifacts_old;
            CREATE TABLE artifacts (
                artifact_id TEXT PRIMARY KEY,
                uri TEXT NOT NULL,
                project_id TEXT NULL,
                type TEXT NOT NULL,
                path TEXT NOT NULL,
                frontmatter_json TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO artifacts(artifact_id, uri, project_id, type, path, frontmatter_json, created_at, updated_at)
            SELECT artifact_id, uri, project_id, type, path, frontmatter_json, created_at, updated_at
            FROM artifacts_old;
            DROP TABLE artifacts_old;

            ALTER TABLE documents RENAME TO documents_old;
            CREATE TABLE documents (
                document_id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                project_id TEXT NULL,
                title TEXT NULL,
                mime_type TEXT NULL,
                text TEXT NOT NULL,
                text_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO documents(document_id, source_kind, source_id, project_id, title, mime_type, text, text_hash, created_at, updated_at)
            SELECT document_id, source_kind, source_id, project_id, title, mime_type, text, text_hash, created_at, updated_at
            FROM documents_old;
            DROP TABLE documents_old;

            UPDATE agents
            SET scope_kind = 'global',
                scope_id = NULL
            WHERE lower(scope_kind) = 'workspace';

            DROP TABLE IF EXISTS workspaces;
            DROP TABLE IF EXISTS document_embeddings_vec;

            CREATE INDEX IF NOT EXISTS idx_tasks_project_updated
                ON tasks(project_id, updated_at);
            CREATE INDEX IF NOT EXISTS idx_task_events_task
                ON task_events(task_id, created_at);
            CREATE INDEX IF NOT EXISTS idx_artifacts_project_type
                ON artifacts(project_id, type, updated_at);
            CREATE INDEX IF NOT EXISTS idx_documents_source
                ON documents(source_kind, source_id);

            PRAGMA foreign_keys = ON;
            """
        ),
        new DbMigration(
            "0003_project_file_usage",
            """
            CREATE TABLE IF NOT EXISTS project_file_usage (
                project_root TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                kind TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                access_count INTEGER NOT NULL DEFAULT 1,
                last_access_kind TEXT NULL,
                PRIMARY KEY (project_root, relative_path, kind)
            );

            CREATE INDEX IF NOT EXISTS idx_project_file_usage_recent
                ON project_file_usage(project_root, last_accessed_at DESC, access_count DESC);
            """
        ),
    ];

    private readonly CodeAltaDbOptions _options;
    private readonly SemaphoreSlim _writeLock = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaDb"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CodeAltaDbOptions.DatabasePath"/> is empty.</exception>
    public CodeAltaDb(CodeAltaDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("DatabasePath is required.", nameof(options));
        }

        _options = options;
    }

    /// <summary>
    /// Gets the configured database path.
    /// </summary>
    public string DatabasePath => _options.DatabasePath;

    /// <summary>
    /// Initializes the database and applies pending migrations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseDirectory();

        await using var connection = await CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var createVersionCommand = connection.CreateCommand();
        createVersionCommand.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                migration_id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await createVersionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var migration in Migrations)
        {
            var exists = await IsMigrationAppliedAsync(connection, migration.MigrationId, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var insertVersion = connection.CreateCommand();
            insertVersion.Transaction = transaction;
            insertVersion.CommandText =
                """
                INSERT INTO schema_version(migration_id, applied_at)
                VALUES ($id, $applied_at);
                """;
            insertVersion.Parameters.AddWithValue("$id", migration.MigrationId);
            insertVersion.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
            await insertVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates and opens a new SQLite connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open <see cref="SqliteConnection"/>.</returns>
    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseDirectory();

        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Pooling = _options.EnablePooling,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var connection = new SqliteConnection(connectionStringBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        TryLoadSqliteVecExtension(connection);
        return connection;
    }

    /// <summary>
    /// Executes a write operation using serialized writer access.
    /// </summary>
    /// <param name="action">The write action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The write operation result.</returns>
    /// <typeparam name="T">The result type.</typeparam>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="action"/> is <see langword="null"/>.</exception>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await action(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Executes a read operation.
    /// </summary>
    /// <param name="action">The read action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read operation result.</returns>
    /// <typeparam name="T">The result type.</typeparam>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="action"/> is <see langword="null"/>.</exception>
    public async Task<T> ExecuteReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = await CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await action(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        SqliteConnection connection,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM schema_version
            WHERE migration_id = $id;
            """;
        command.Parameters.AddWithValue("$id", migrationId);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return count > 0;
    }

    private void EnsureDatabaseDirectory()
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void TryLoadSqliteVecExtension(SqliteConnection connection)
    {
        var extensionPath = _options.SqliteVecExtensionPath;
        if (string.IsNullOrWhiteSpace(extensionPath))
        {
            return;
        }

        try
        {
            connection.EnableExtensions(enable: true);
            connection.LoadExtension(extensionPath);
        }
        catch when (!_options.RequireSqliteVec)
        {
            // Optional extension mode: continue without sqlite-vec.
        }
    }

    private sealed record DbMigration(string MigrationId, string Sql);
}

