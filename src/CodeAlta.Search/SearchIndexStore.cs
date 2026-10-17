using System.Text;
using CodeAlta.Catalog;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Search;

/// <summary>Where the reading of a source stopped.</summary>
/// <param name="Key">The source: <c>journal:&lt;session&gt;</c>, <c>skills</c>, <c>docs</c>, <c>plans:&lt;project&gt;</c>…</param>
/// <param name="Stamp">What says whether it changed (lengths and write times), or null.</param>
/// <param name="Fingerprint">For a journal, the mark of its first line, or null.</param>
/// <param name="Hash">The hash of what was read, or null.</param>
/// <param name="Offset">For a journal, the byte after the last complete line that was read.</param>
/// <param name="State">What the reader keeps between two readings, as JSON, or null.</param>
/// <param name="CheckedAt">When it was last checked.</param>
/// <param name="Error">The last error of its reading, or null.</param>
public sealed record SearchSourceState(string Key, string? Stamp, byte[]? Fingerprint, byte[]? Hash, long Offset, string? State, DateTimeOffset CheckedAt, string? Error = null);

/// <summary>What a reading of a source changes in the index, written in one transaction.</summary>
/// <param name="Source">The new state of the source.</param>
/// <param name="Documents">The documents read.</param>
public sealed record SearchSourceUpdate(SearchSourceState Source, IReadOnlyList<SearchDocument> Documents)
{
    /// <summary>Gets whether <see cref="Documents"/> is everything of the source: the items it no longer gives are removed.</summary>
    public bool Complete { get; init; }

    /// <summary>Gets refs of the source that are gone.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];
}

/// <summary>One chunk found by words.</summary>
internal readonly record struct WordsCandidate(long ChunkId, long ItemId, double Bm25, string Snippet);

/// <summary>An item of the index, as a hit is made from it.</summary>
internal sealed record IndexedItem(long Id, SearchKind Kind, string Ref, string Title, DateTimeOffset At, string? SpaceId, string? ProjectId,
    string? SessionId, string? RunId, SearchRole Role, string? Extra);

/// <summary>What is in the index, for the status and the canvas.</summary>
public sealed record SearchIndexCounts
{
    /// <summary>Gets the items by kind.</summary>
    public required IReadOnlyDictionary<SearchKind, long> Items { get; init; }

    /// <summary>Gets the chunks.</summary>
    public long Chunks { get; init; }

    /// <summary>Gets the sessions that have items.</summary>
    public long Sessions { get; init; }

    /// <summary>Gets the items of the content kinds by project.</summary>
    public required IReadOnlyDictionary<string, long> Projects { get; init; }

    /// <summary>Gets the turns indexed by local day, the newest first, for the history.</summary>
    public required IReadOnlyList<(DateOnly Day, long Turns)> Days { get; init; }

    /// <summary>Gets the size of the file of the index, write-ahead log included.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the items of the content kinds written since the time a scoped count was asked from, or 0.</summary>
    public long Recent { get; init; }
}

/// <summary>The sources of a family (the journals, the plans of the projects, the skills…) and how their reading went.</summary>
/// <param name="Family">The start of their key: <c>journal</c>, <c>plans</c>, <c>skills</c>, <c>docs</c>…</param>
/// <param name="Sources">How many there are.</param>
/// <param name="LastCheck">When one was last checked, or null when none was.</param>
/// <param name="Failed">How many failed at their last reading.</param>
/// <param name="LastError">The error of the one that failed last, or null.</param>
public sealed record SearchSourceSummary(string Family, long Sources, DateTimeOffset? LastCheck, long Failed, string? LastError);

/// <summary>
/// The search index of an instance: <c>data/search.sqlite3</c>, a second <see cref="ApplicationDatabase"/> without backups.
/// Its items, their chunks with an FTS5 table over them, the vectors of the chunks and the activity of search.
/// </summary>
public sealed partial class SearchIndexStore : IAsyncDisposable
{
    /// <summary>The owner of the tables in the database.</summary>
    public const string Owner = "search";

    private const string Prefix = "search_";
    private const int SchemaVersion = 2;

    private readonly ApplicationDatabase _database;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private bool _opened;

    /// <summary>Creates the store of a file. Nothing is opened before the first use.</summary>
    /// <param name="path">The file, such as <see cref="CatalogOptions.SearchDatabasePath"/>.</param>
    public SearchIndexStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
        _database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path,
            BackupDirectory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "search-backups-unused"),
            SlowWriteThreshold = TimeSpan.FromSeconds(5),
        });
    }

    /// <summary>Gets the file.</summary>
    public string Path { get; }

    /// <summary>Gets a number that changes when the file was replaced (damaged and started again).</summary>
    public int Generation => _database.Generation;

    /// <summary>Opens the file and brings its tables to their version.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _opened)) return;
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_opened) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            try
            {
                await _database.MigrateAsync(Owner, Prefix, SchemaVersion, MigrateAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (ApplicationDatabaseNewerVersionException)
            {
                // A newer build wrote the index: it is rebuilt for this one.
                await _database.DropTablesAsync(Owner, Prefix, cancellationToken).ConfigureAwait(false);
                await _database.MigrateAsync(Owner, Prefix, SchemaVersion, MigrateAsync, cancellationToken).ConfigureAwait(false);
            }

            _opened = true;
        }
        finally
        {
            _openGate.Release();
        }
    }

    private static async ValueTask MigrateAsync(SqliteConnection connection, int fromVersion, int toVersion, CancellationToken cancellationToken)
    {
        if (fromVersion >= 2) return;
        if (fromVersion == 1)
        {
            // Version 1 kept neither the title of the text to embed nor the log of changes. The index is derived data:
            // it is made again, and its empty cursors make the indexer read everything again.
            await ExecuteAsync(connection, """
                DROP TABLE search_fts; DROP TABLE search_chunk; DROP TABLE search_item; DROP TABLE search_source;
                DROP TABLE search_vector_set; DROP TABLE search_vector; DROP TABLE search_activity; DROP TABLE search_meta;
                """, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, """
            CREATE TABLE search_source (
              id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, stamp TEXT, fingerprint BLOB, hash BLOB,
              offset INTEGER NOT NULL DEFAULT 0, state TEXT, checked_at INTEGER NOT NULL DEFAULT 0, error TEXT);
            CREATE TABLE search_item (
              id INTEGER PRIMARY KEY, source_id INTEGER NOT NULL, kind INTEGER NOT NULL, ref TEXT NOT NULL UNIQUE,
              space_id TEXT, project_id TEXT, session_id TEXT, run_id TEXT, role INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL,
              title TEXT NOT NULL, extra TEXT, text_hash BLOB NOT NULL);
            CREATE INDEX search_item_source ON search_item(source_id);
            CREATE INDEX search_item_scope ON search_item(kind, project_id, at);
            CREATE INDEX search_item_session ON search_item(session_id);
            CREATE TABLE search_chunk (
              id INTEGER PRIMARY KEY, item_id INTEGER NOT NULL, ordinal INTEGER NOT NULL,
              title TEXT NOT NULL, text TEXT NOT NULL, words TEXT NOT NULL, embed_hash BLOB NOT NULL, embed_title TEXT);
            CREATE INDEX search_chunk_item ON search_chunk(item_id);
            CREATE INDEX search_chunk_embed ON search_chunk(embed_hash);
            CREATE VIRTUAL TABLE search_fts USING fts5(title, text, words,
              content='search_chunk', content_rowid='id', tokenize='unicode61 remove_diacritics 2', prefix='2 3');
            CREATE TABLE search_vector_set (
              id INTEGER PRIMARY KEY, model TEXT NOT NULL, revision TEXT NOT NULL, dims INTEGER NOT NULL,
              prompts TEXT NOT NULL, runtime TEXT NOT NULL, state TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE search_vector (
              set_id INTEGER NOT NULL, embed_hash BLOB NOT NULL, scale REAL NOT NULL, code BLOB NOT NULL,
              PRIMARY KEY (set_id, embed_hash)) WITHOUT ROWID;
            CREATE TABLE search_activity (
              at INTEGER NOT NULL, origin INTEGER NOT NULL, session_id TEXT, kinds INTEGER NOT NULL, mode INTEGER NOT NULL,
              hits INTEGER NOT NULL, words_ms REAL, meaning_ms REAL, tokens INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX search_activity_at ON search_activity(at);
            CREATE TABLE search_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """, cancellationToken).ConfigureAwait(false);
        await CreateChangeLogAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the state of a source, or null when it was never read.</summary>
    /// <param name="key">The source.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The state.</returns>
    public async ValueTask<SearchSourceState?> GetSourceAsync(string key, CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, stamp, fingerprint, hash, offset, state, checked_at, error FROM search_source WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadSource(reader) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the sources whose key starts with a prefix.</summary>
    /// <param name="prefix">The prefix, such as <c>journal:</c>; empty for all.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The states.</returns>
    public async ValueTask<IReadOnlyList<SearchSourceState>> ListSourcesAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyList<SearchSourceState>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, stamp, fingerprint, hash, offset, state, checked_at, error FROM search_source WHERE substr(key, 1, length($prefix)) = $prefix";
            command.Parameters.AddWithValue("$prefix", prefix);
            var list = new List<SearchSourceState>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) list.Add(ReadSource(reader));
            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SearchSourceState ReadSource(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2],
        reader.IsDBNull(3) ? null : (byte[])reader[3], reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>
    /// Writes what a reading of a source gave, in one transaction: the items whose text changed get new chunks, the
    /// others only what describes them.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="cancellationToken">Cancels before the write.</param>
    /// <returns>The number of items whose text was written.</returns>
    public async ValueTask<int> ApplyAsync(SearchSourceUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            var sourceId = await UpsertSourceAsync(connection, update.Source, token).ConfigureAwait(false);
            var written = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var items = new ItemWriter(connection);
            foreach (var document in update.Documents)
            {
                if (!seen.Add(document.Ref)) continue;
                if (await items.WriteAsync(sourceId, document, token).ConfigureAwait(false)) written++;
            }

            foreach (var removed in update.Removed) await items.DeleteAsync(removed, token).ConfigureAwait(false);
            if (update.Complete)
            {
                await using var list = connection.CreateCommand();
                list.CommandText = "SELECT ref FROM search_item WHERE source_id = $source";
                list.Parameters.AddWithValue("$source", sourceId);
                var gone = new List<string>();
                await using (var reader = await list.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var reference = reader.GetString(0);
                        if (!seen.Contains(reference)) gone.Add(reference);
                    }
                }

                foreach (var reference in gone) await items.DeleteAsync(reference, token).ConfigureAwait(false);
            }

            await TrimChangesAsync(connection, token).ConfigureAwait(false);
            return written;
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the state of a source alone, such as a check that found nothing new.</summary>
    /// <param name="source">The state.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask SaveSourceAsync(SearchSourceState source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        await _database.WriteAsync(Owner, async (connection, token) => { await UpsertSourceAsync(connection, source, token).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> UpsertSourceAsync(SqliteConnection connection, SearchSourceState source, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO search_source(key, stamp, fingerprint, hash, offset, state, checked_at, error)
            VALUES($key, $stamp, $fingerprint, $hash, $offset, $state, $checked, $error)
            ON CONFLICT(key) DO UPDATE SET stamp = excluded.stamp, fingerprint = excluded.fingerprint, hash = excluded.hash,
              offset = excluded.offset, state = excluded.state, checked_at = excluded.checked_at, error = excluded.error
            RETURNING id
            """;
        command.Parameters.AddWithValue("$key", source.Key);
        command.Parameters.AddWithValue("$stamp", (object?)source.Stamp ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", (object?)source.Fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)source.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$offset", source.Offset);
        command.Parameters.AddWithValue("$state", (object?)source.State ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", source.CheckedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$error", (object?)source.Error ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Removes sources and every item they gave.</summary>
    /// <param name="keys">The sources.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask DeleteSourcesAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return;
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        await _database.WriteAsync(Owner, async (connection, token) =>
        {
            using var items = new ItemWriter(connection);
            foreach (var key in keys)
            {
                await items.DeleteWhereAsync("source_id = (SELECT id FROM search_source WHERE key = $value)", key, token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM search_source WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the items of the content kinds of a project or of a space, for an exclusion.</summary>
    /// <param name="projectId">The project, or null.</param>
    /// <param name="spaceId">The space, or null.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The number of items removed.</returns>
    public async ValueTask<int> DeleteContentAsync(string? projectId, string? spaceId, CancellationToken cancellationToken = default)
    {
        if (projectId is null && spaceId is null) return 0;
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            // The journals of its sessions are read again from their start when it is let in again.
            await using (var cursors = connection.CreateCommand())
            {
                cursors.CommandText = """
                    DELETE FROM search_source WHERE key IN (
                      SELECT DISTINCT 'journal:' || session_id FROM search_item
                      WHERE session_id IS NOT NULL AND (project_id = $project COLLATE NOCASE OR space_id = $space COLLATE NOCASE))
                    """;
                cursors.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
                cursors.Parameters.AddWithValue("$space", (object?)spaceId ?? DBNull.Value);
                await cursors.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            using var items = new ItemWriter(connection);
            var count = 0;
            if (projectId is not null) count += await items.DeleteWhereAsync("project_id = $value COLLATE NOCASE", projectId, token).ConfigureAwait(false);
            if (spaceId is not null) count += await items.DeleteWhereAsync("space_id = $value COLLATE NOCASE", spaceId, token).ConfigureAwait(false);
            return count;
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the title of each session the index holds, from its session item.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The titles by session.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetSessionTitlesAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyDictionary<string, string>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT session_id, title FROM search_item WHERE kind = {(int)SearchKind.Session} AND session_id IS NOT NULL";
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) map[reader.GetString(0)] = reader.GetString(1);
            return map;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives the items of a session its new title: their rows, their chunks and the FTS rows of the chunks.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="title">Its title.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask RetitleSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        await _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO search_fts(search_fts, rowid, title, text, words)
                    SELECT 'delete', c.id, c.title, c.text, c.words FROM search_chunk c JOIN search_item i ON i.id = c.item_id WHERE i.session_id = $session
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE search_item SET title = $title WHERE session_id = $session;
                    UPDATE search_chunk SET title = $title WHERE item_id IN (SELECT id FROM search_item WHERE session_id = $session);
                    INSERT INTO search_fts(rowid, title, text, words)
                    SELECT c.id, c.title, c.text, c.words FROM search_chunk c JOIN search_item i ON i.id = c.item_id WHERE i.session_id = $session;
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$title", title);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds chunks by words, in a scope, the best first.</summary>
    internal async ValueTask<IReadOnlyList<WordsCandidate>> SearchWordsAsync(string match, SearchScope scope, int limit, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyList<WordsCandidate>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            var where = new StringBuilder("search_fts MATCH $match");
            AppendScope(command, where, scope);
            command.CommandText = $"""
                SELECT c.id, c.item_id, bm25(search_fts, 4.0, 1.0, 0.5),
                  snippet(search_fts, 1, char(1), char(2), '…', 24)
                FROM search_fts JOIN search_chunk c ON c.id = search_fts.rowid JOIN search_item i ON i.id = c.item_id
                WHERE {where}
                ORDER BY bm25(search_fts, 4.0, 1.0, 0.5) LIMIT $limit
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<WordsCandidate>();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    list.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetString(3)));
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
            {
                // A query FTS5 refuses in spite of the quoting (a term it cannot tokenize) finds nothing.
            }

            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static void AppendScope(SqliteCommand command, StringBuilder where, SearchScope scope)
    {
        if (scope.Kinds is { } kinds)
        {
            where.Append(" AND i.kind IN (").Append(string.Join(',', kinds.Select(static kind => (int)kind))).Append(')');
        }

        if (scope.SpaceId is { } space)
        {
            where.Append(" AND (i.space_id IS NULL OR i.space_id = $space COLLATE NOCASE)");
            command.Parameters.AddWithValue("$space", space);
        }

        if (scope.ProjectId is { } project)
        {
            where.Append(" AND i.project_id = $project COLLATE NOCASE");
            command.Parameters.AddWithValue("$project", project);
        }
        else if (scope.Projects is { } projects)
        {
            where.Append(" AND (i.project_id IS NULL");
            if (projects.Count > 0)
            {
                where.Append(" OR i.project_id IN (");
                var index = 0;
                foreach (var id in projects)
                {
                    if (index > 0) where.Append(',');
                    where.Append("$p").Append(index);
                    command.Parameters.AddWithValue("$p" + index, id);
                    index++;
                }

                where.Append(')');
            }

            where.Append(')');
        }

        if (scope.SessionId is { } session)
        {
            where.Append(" AND i.session_id = $session");
            command.Parameters.AddWithValue("$session", session);
        }

        if (scope.ExceptSessionId is { } except)
        {
            where.Append(" AND (i.session_id IS NULL OR i.session_id <> $except)");
            command.Parameters.AddWithValue("$except", except);
        }

        if (scope.Role != SearchRole.None)
        {
            where.Append(" AND i.role = $role");
            command.Parameters.AddWithValue("$role", (int)scope.Role);
        }

        if (scope.Since is { } since)
        {
            where.Append(" AND i.at >= $since");
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        }

        if (scope.Until is { } until)
        {
            where.Append(" AND i.at <= $until");
            command.Parameters.AddWithValue("$until", until.ToUnixTimeMilliseconds());
        }

        AppendNotIn(command, where, "project_id", "$xp", scope.ExcludedProjects);
        AppendNotIn(command, where, "space_id", "$xs", scope.ExcludedSpaces);
    }

    private static void AppendNotIn(SqliteCommand command, StringBuilder where, string column, string prefix, IReadOnlySet<string>? values)
    {
        if (values is not { Count: > 0 }) return;
        where.Append(" AND (i.").Append(column).Append(" IS NULL OR i.").Append(column).Append(" COLLATE NOCASE NOT IN (");
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) where.Append(',');
            where.Append(prefix).Append(index);
            command.Parameters.AddWithValue(prefix + index, value);
            index++;
        }

        where.Append("))");
    }

    /// <summary>Gets items by their ids.</summary>
    internal async ValueTask<IReadOnlyDictionary<long, IndexedItem>> GetItemsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new Dictionary<long, IndexedItem>();
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyDictionary<long, IndexedItem>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, kind, ref, title, at, space_id, project_id, session_id, run_id, role, extra FROM search_item WHERE id IN ({string.Join(',', ids)})";
            var map = new Dictionary<long, IndexedItem>(ids.Count);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var item = ReadItem(reader);
                map[item.Id] = item;
            }

            return map;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets an item by its ref, with its whole text, or null.</summary>
    internal async ValueTask<(IndexedItem Item, string Text)?> GetItemAsync(string reference, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<(IndexedItem, string)?>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, kind, ref, title, at, space_id, project_id, session_id, run_id, role, extra FROM search_item WHERE ref = $ref";
            command.Parameters.AddWithValue("$ref", reference);
            IndexedItem? item = null;
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(token).ConfigureAwait(false)) item = ReadItem(reader);
            }

            if (item is null) return null;
            await using var chunks = connection.CreateCommand();
            chunks.CommandText = "SELECT text FROM search_chunk WHERE item_id = $item ORDER BY ordinal";
            chunks.Parameters.AddWithValue("$item", item.Id);
            var text = new StringBuilder();
            await using (var reader = await chunks.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (text.Length > 0) text.Append("\n\n");
                    text.Append(reader.GetString(0));
                }
            }

            return (item, text.ToString());
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the text of chunks by their ids.</summary>
    internal async ValueTask<IReadOnlyDictionary<long, (long ItemId, string Text)>> GetChunksAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new Dictionary<long, (long, string)>();
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyDictionary<long, (long, string)>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, item_id, text FROM search_chunk WHERE id IN ({string.Join(',', ids)})";
            var map = new Dictionary<long, (long, string)>(ids.Count);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) map[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetString(2));
            return map;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IndexedItem ReadItem(SqliteDataReader reader) => new(
        reader.GetInt64(0), (SearchKind)reader.GetInt32(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), (SearchRole)reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>Counts what is in the index.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The counts.</returns>
    public ValueTask<SearchIndexCounts> CountAsync(CancellationToken cancellationToken = default) => CountAsync(new SearchScope(), null, cancellationToken);

    /// <summary>Counts what a scope of the index holds: what a space can see, for the canvas of that space.</summary>
    /// <param name="scope">The scope; its <see cref="SearchScope.CatalogFilter"/> is not applied, the counts being made in the database.</param>
    /// <param name="since">The time from which the items of the content kinds are counted as <see cref="SearchIndexCounts.Recent"/>, or null.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The counts; <see cref="SearchIndexCounts.Bytes"/> is the size of the whole index.</returns>
    public async ValueTask<SearchIndexCounts> CountAsync(SearchScope scope, DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        var content = string.Join(',', SearchNames.ContentKinds.Select(static k => (int)k));
        var counts = await _database.ReadAsync(Owner, async (connection, token) =>
        {
            // Each count is a command of its own, with the conditions of the scope after its own.
            SqliteCommand Scoped(string condition, string select, string? tail = null)
            {
                var command = connection.CreateCommand();
                var where = new StringBuilder(condition);
                AppendScope(command, where, scope);
                command.CommandText = $"{select} WHERE {where} {tail}";
                return command;
            }

            var items = new Dictionary<SearchKind, long>();
            await using (var command = Scoped("1 = 1", "SELECT i.kind, count(*) FROM search_item i", "GROUP BY i.kind"))
            {
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) items[(SearchKind)reader.GetInt32(0)] = reader.GetInt64(1);
            }

            var projects = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var command = Scoped($"i.project_id IS NOT NULL AND i.kind IN ({content})", "SELECT i.project_id, count(*) FROM search_item i", "GROUP BY i.project_id"))
            {
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) projects[reader.GetString(0)] = reader.GetInt64(1);
            }

            var days = new List<(DateOnly, long)>();
            await using (var command = Scoped($"i.kind = {(int)SearchKind.Message} AND i.role IN (1, 3)",
                "SELECT date(i.at / 1000, 'unixepoch', 'localtime') AS day, count(*) FROM search_item i", "GROUP BY day ORDER BY day DESC LIMIT 400"))
            {
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (DateOnly.TryParse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture, out var day)) days.Add((day, reader.GetInt64(1)));
                }
            }

            long chunks, sessions, recent = 0;
            await using (var command = Scoped("1 = 1", "SELECT count(*) FROM search_chunk c JOIN search_item i ON i.id = c.item_id"))
            {
                chunks = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }

            await using (var command = Scoped("i.session_id IS NOT NULL", "SELECT count(DISTINCT i.session_id) FROM search_item i"))
            {
                sessions = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }

            if (since is { } from)
            {
                await using var command = Scoped($"i.kind IN ({content}) AND i.at >= $from", "SELECT count(*) FROM search_item i");
                command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                recent = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }

            return new SearchIndexCounts { Items = items, Projects = projects, Days = days, Chunks = chunks, Sessions = sessions, Recent = recent };
        }, cancellationToken).ConfigureAwait(false);
        return counts with { Bytes = FileSize() };
    }

    /// <summary>Lists the items of a scope, the newest first and then by title, each with the start of its text: what is indexed, kind by kind.</summary>
    internal async ValueTask<IReadOnlyList<(IndexedItem Item, string Text)>> ListItemsAsync(SearchScope scope, int limit, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyList<(IndexedItem, string)>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            var where = new StringBuilder("1 = 1");
            AppendScope(command, where, scope);
            command.CommandText = $"""
                SELECT i.id, i.kind, i.ref, i.title, i.at, i.space_id, i.project_id, i.session_id, i.run_id, i.role, i.extra,
                  coalesce((SELECT substr(c.text, 1, 400) FROM search_chunk c WHERE c.item_id = i.id ORDER BY c.ordinal LIMIT 1), '')
                FROM search_item i WHERE {where}
                ORDER BY i.at DESC, i.title COLLATE NOCASE LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<(IndexedItem, string)>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) list.Add((ReadItem(reader), reader.GetString(11)));
            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sums up the sources by family (the start of their key, before <c>:</c>): how many, their last check and their failures.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The families, by name.</returns>
    public async ValueTask<IReadOnlyList<SearchSourceSummary>> SummarizeSourcesAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyList<SearchSourceSummary>>(Owner, async (connection, token) =>
        {
            const string Family = "CASE WHEN instr(key, ':') > 0 THEN substr(key, 1, instr(key, ':') - 1) ELSE key END";
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                // The newest error of each family.
                command.CommandText = $"SELECT {Family} AS family, error FROM search_source WHERE error IS NOT NULL ORDER BY checked_at DESC LIMIT 200";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) errors.TryAdd(reader.GetString(0), reader.GetString(1));
            }

            var list = new List<SearchSourceSummary>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT {Family} AS family, count(*), max(checked_at), sum(error IS NOT NULL) FROM search_source GROUP BY family ORDER BY family";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var family = reader.GetString(0);
                    var checkedAt = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    list.Add(new SearchSourceSummary(family, reader.GetInt64(1), checkedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(checkedAt) : null,
                        reader.IsDBNull(3) ? 0 : reader.GetInt64(3), errors.GetValueOrDefault(family)));
                }
            }

            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the size of the file of the index, write-ahead log included.</summary>
    /// <returns>The bytes.</returns>
    public long FileSize()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var info = new FileInfo(Path + suffix);
                if (info.Exists) total += info.Length;
            }
            catch (IOException)
            {
            }
        }

        return total;
    }

    /// <summary>Runs a read on the database of the index, for the parts of search that keep their own tables (vectors, activity).</summary>
    internal ValueTask<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
        => ReadOpenedAsync(read, cancellationToken);

    /// <summary>Runs a write on the database of the index.</summary>
    internal async ValueTask<T> WriteAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, write, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> ReadOpenedAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync(Owner, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes everything: the tables are dropped and made again, empty.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        await _database.DropTablesAsync(Owner, Prefix, cancellationToken).ConfigureAwait(false);
        // No VACUUM: it cannot run in the transaction of a write, and the pass that follows fills the free pages again.
        await _database.MigrateAsync(Owner, Prefix, SchemaVersion, MigrateAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
        _openGate.Dispose();
    }

    internal static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes items and their chunks, keeping the FTS table in step: the commands are prepared once per write.</summary>
    private sealed class ItemWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _find;
        private readonly SqliteCommand _insertItem;
        private readonly SqliteCommand _updateItem;
        private readonly SqliteCommand _insertChunk;
        private readonly SqliteCommand _insertFts;

        public ItemWriter(SqliteConnection connection)
        {
            _connection = connection;
            _find = Command("SELECT id, text_hash, title, at, extra, space_id, project_id FROM search_item WHERE ref = $ref", "$ref");
            _insertItem = Command("""
                INSERT INTO search_item(source_id, kind, ref, space_id, project_id, session_id, run_id, role, at, title, extra, text_hash)
                VALUES($source, $kind, $ref, $space, $project, $session, $run, $role, $at, $title, $extra, $hash) RETURNING id
                """, "$source", "$kind", "$ref", "$space", "$project", "$session", "$run", "$role", "$at", "$title", "$extra", "$hash");
            _updateItem = Command("""
                UPDATE search_item SET source_id = $source, kind = $kind, space_id = $space, project_id = $project, session_id = $session,
                  run_id = $run, role = $role, at = $at, title = $title, extra = $extra, text_hash = $hash WHERE id = $id
                """, "$source", "$kind", "$space", "$project", "$session", "$run", "$role", "$at", "$title", "$extra", "$hash", "$id");
            _insertChunk = Command("""
                INSERT INTO search_chunk(item_id, ordinal, title, text, words, embed_hash, embed_title)
                VALUES($item, $ordinal, $title, $text, $words, $embed, $embedTitle) RETURNING id
                """, "$item", "$ordinal", "$title", "$text", "$words", "$embed", "$embedTitle");
            _insertFts = Command("INSERT INTO search_fts(rowid, title, text, words) VALUES($id, $title, $text, $words)", "$id", "$title", "$text", "$words");
        }

        private SqliteCommand Command(string sql, params string[] parameters)
        {
            var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var name in parameters) command.Parameters.Add(new SqliteParameter(name, null));
            return command;
        }

        // True when the text was written; false when only what describes the item changed, or nothing.
        public async ValueTask<bool> WriteAsync(long sourceId, SearchDocument document, CancellationToken cancellationToken)
        {
            var hash = SearchText.Hash(document.Title + "\u0000" + document.Text + "\u0000" + (document.EmbeddingTitle ?? ""));
            _find.Parameters["$ref"].Value = document.Ref;
            long? existing = null;
            var sameText = false;
            await using (var reader = await _find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existing = reader.GetInt64(0);
                    sameText = ((byte[])reader[1]).AsSpan().SequenceEqual(hash);
                }
            }

            var command = existing is null ? _insertItem : _updateItem;
            command.Parameters["$source"].Value = sourceId;
            command.Parameters["$kind"].Value = (int)document.Kind;
            if (existing is null) command.Parameters["$ref"].Value = document.Ref;
            else command.Parameters["$id"].Value = existing.Value;
            command.Parameters["$space"].Value = (object?)document.SpaceId ?? DBNull.Value;
            command.Parameters["$project"].Value = (object?)document.ProjectId ?? DBNull.Value;
            command.Parameters["$session"].Value = (object?)document.SessionId ?? DBNull.Value;
            command.Parameters["$run"].Value = (object?)document.RunId ?? DBNull.Value;
            command.Parameters["$role"].Value = (int)document.Role;
            command.Parameters["$at"].Value = document.At.ToUnixTimeMilliseconds();
            command.Parameters["$title"].Value = document.Title;
            command.Parameters["$extra"].Value = (object?)document.Extra ?? DBNull.Value;
            command.Parameters["$hash"].Value = hash;
            long itemId;
            if (existing is null)
            {
                itemId = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }
            else
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                itemId = existing.Value;
                if (sameText) return false;
                await DeleteChunksAsync(itemId, cancellationToken).ConfigureAwait(false);
            }

            var chunks = SearchText.Chunk(document.Text);
            var embedTitle = document.EmbeddingTitle ?? document.Title;
            for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
            {
                var text = chunks[ordinal];
                var words = SearchText.SplitIdentifiers(ordinal == 0 ? document.Title + " " + text : text);
                _insertChunk.Parameters["$item"].Value = itemId;
                _insertChunk.Parameters["$ordinal"].Value = ordinal;
                _insertChunk.Parameters["$title"].Value = document.Title;
                _insertChunk.Parameters["$text"].Value = text;
                _insertChunk.Parameters["$words"].Value = words;
                _insertChunk.Parameters["$embed"].Value = SearchText.Hash(embedTitle + "\u0000" + text);
                // Kept even when it is the title: a rename changes the title, not what the hash was made of.
                _insertChunk.Parameters["$embedTitle"].Value = embedTitle;
                var chunkId = (long)(await _insertChunk.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                _insertFts.Parameters["$id"].Value = chunkId;
                _insertFts.Parameters["$title"].Value = document.Title;
                _insertFts.Parameters["$text"].Value = text;
                _insertFts.Parameters["$words"].Value = words;
                await _insertFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        public async ValueTask DeleteAsync(string reference, CancellationToken cancellationToken)
            => await DeleteWhereAsync("ref = $value", reference, cancellationToken).ConfigureAwait(false);

        public async ValueTask<int> DeleteWhereAsync(string condition, string value, CancellationToken cancellationToken)
        {
            var ids = new List<long>();
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM search_item WHERE " + condition;
                command.Parameters.AddWithValue("$value", value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
            }

            foreach (var id in ids)
            {
                await DeleteChunksAsync(id, cancellationToken).ConfigureAwait(false);
                await using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM search_item WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return ids.Count;
        }

        private async ValueTask DeleteChunksAsync(long itemId, CancellationToken cancellationToken)
        {
            // An external-content FTS table forgets a row only when it is told its old values.
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO search_fts(search_fts, rowid, title, text, words)
                    SELECT 'delete', id, title, text, words FROM search_chunk WHERE item_id = $item
                    """;
                command.Parameters.AddWithValue("$item", itemId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM search_chunk WHERE item_id = $item";
                command.Parameters.AddWithValue("$item", itemId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _find.Dispose();
            _insertItem.Dispose();
            _updateItem.Dispose();
            _insertChunk.Dispose();
            _insertFts.Dispose();
        }
    }
}
