using Microsoft.Data.Sqlite;

namespace CodeAlta.Persistence;

/// <summary>
/// Provides durable recent-usage operations for project files and directories.
/// </summary>
public sealed class ProjectFileUsageRepository
{
    private readonly CodeAltaDb _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileUsageRepository"/> class.
    /// </summary>
    /// <param name="db">Database accessor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="db"/> is <see langword="null"/>.</exception>
    public ProjectFileUsageRepository(CodeAltaDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Records an access for a project file or directory and increments the usage count.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="relativePath">Normalized relative path.</param>
    /// <param name="kind">Persisted item kind.</param>
    /// <param name="accessedAt">Access timestamp.</param>
    /// <param name="accessKind">Optional access source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated usage record.</returns>
    /// <exception cref="ArgumentException">Thrown when required values are empty.</exception>
    public Task<ProjectFileUsageRecord> RecordAccessAsync(
        string projectRoot,
        string relativePath,
        string kind,
        DateTimeOffset accessedAt,
        string? accessKind = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Kind is required.", nameof(kind));
        }

        return _db.ExecuteWriteAsync(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO project_file_usage (
                        project_root,
                        relative_path,
                        kind,
                        last_accessed_at,
                        access_count,
                        last_access_kind)
                    VALUES (
                        $project_root,
                        $relative_path,
                        $kind,
                        $last_accessed_at,
                        1,
                        $last_access_kind)
                    ON CONFLICT(project_root, relative_path, kind) DO UPDATE SET
                        last_accessed_at = excluded.last_accessed_at,
                        access_count = project_file_usage.access_count + 1,
                        last_access_kind = excluded.last_access_kind;
                    """;
                command.Parameters.AddWithValue("$project_root", projectRoot);
                command.Parameters.AddWithValue("$relative_path", relativePath);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$last_accessed_at", accessedAt.ToString("O"));
                command.Parameters.AddWithValue("$last_access_kind", (object?)accessKind ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                return await GetRequiredAsync(connection, projectRoot, relativePath, kind, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Lists recent usage entries for a project root.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recent usage entries ordered by recency and count.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is not positive.</exception>
    public Task<IReadOnlyList<ProjectFileUsageRecord>> ListRecentAsync(
        string projectRoot,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        return _db.ExecuteReadAsync<IReadOnlyList<ProjectFileUsageRecord>>(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT project_root, relative_path, kind, last_accessed_at, access_count, last_access_kind
                    FROM project_file_usage
                    WHERE project_root = $project_root
                    ORDER BY last_accessed_at DESC, access_count DESC, relative_path ASC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$project_root", projectRoot);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<ProjectFileUsageRecord>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(ReadRecord(reader));
                }

                return results;
            },
            cancellationToken);
    }

    /// <summary>
    /// Lists all usage entries for a project root.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All usage entries for the project root.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> is empty.</exception>
    public Task<IReadOnlyList<ProjectFileUsageRecord>> ListByProjectAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        return _db.ExecuteReadAsync<IReadOnlyList<ProjectFileUsageRecord>>(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT project_root, relative_path, kind, last_accessed_at, access_count, last_access_kind
                    FROM project_file_usage
                    WHERE project_root = $project_root
                    ORDER BY relative_path ASC, kind ASC;
                    """;
                command.Parameters.AddWithValue("$project_root", projectRoot);

                var results = new List<ProjectFileUsageRecord>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(ReadRecord(reader));
                }

                return results;
            },
            cancellationToken);
    }

    private static async Task<ProjectFileUsageRecord> GetRequiredAsync(
        SqliteConnection connection,
        string projectRoot,
        string relativePath,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_root, relative_path, kind, last_accessed_at, access_count, last_access_kind
            FROM project_file_usage
            WHERE project_root = $project_root
              AND relative_path = $relative_path
              AND kind = $kind;
            """;
        command.Parameters.AddWithValue("$project_root", projectRoot);
        command.Parameters.AddWithValue("$relative_path", relativePath);
        command.Parameters.AddWithValue("$kind", kind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Project file usage record was not found after being recorded.");
        }

        return ReadRecord(reader);
    }

    private static ProjectFileUsageRecord ReadRecord(SqliteDataReader reader)
    {
        return new ProjectFileUsageRecord
        {
            ProjectRoot = reader.GetString(0),
            RelativePath = reader.GetString(1),
            Kind = reader.GetString(2),
            LastAccessedAt = DateTimeOffset.Parse(reader.GetString(3), provider: null),
            AccessCount = reader.GetInt64(4),
            LastAccessKind = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
    }
}
