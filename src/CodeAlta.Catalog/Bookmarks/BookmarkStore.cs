using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Bookmarks;

/// <summary>
/// Keeps the bookmarks in the application database of the instance, in a table of their own. The database lives
/// under the state root: a bookmark points to a session of one instance.
/// </summary>
internal sealed class BookmarkStore
{
    internal const string Owner = "bookmarks";
    internal const string TablePrefix = "bookmarks";
    internal const int SchemaVersion = 1;
    private const string Table = "bookmarks_item";
    private const string Columns = "id, kind, session_id, session_title, project_id, role, label, excerpt, run_id, content_id, journal_offset, message_at, created_at, created_by";

    private readonly IApplicationDatabase _database;

    internal BookmarkStore(IApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Gets the number of times the file of the database was replaced.</summary>
    internal int Generation => _database.Generation;

    /// <summary>Makes the table when the database does not have it: a new one, or one that came back empty or older.</summary>
    internal ValueTask MigrateAsync(CancellationToken cancellationToken)
        => _database.MigrateAsync(Owner, TablePrefix, SchemaVersion, static async (connection, _, _, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {Table} (
                    id TEXT NOT NULL PRIMARY KEY,
                    kind TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    session_title TEXT NOT NULL,
                    project_id TEXT NULL,
                    role TEXT NULL,
                    label TEXT NULL,
                    excerpt TEXT NULL,
                    run_id TEXT NULL,
                    content_id TEXT NULL,
                    journal_offset INTEGER NULL,
                    message_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    created_by TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS {Table}_session ON {Table} (session_id);
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Reads every bookmark. A row that does not read as a bookmark is left out.</summary>
    internal ValueTask<List<Bookmark>> ReadAllAsync(CancellationToken cancellationToken)
        => _database.ReadAsync(Owner, static async (connection, token) =>
        {
            var bookmarks = new List<Bookmark>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM {Table};";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var kind = reader.GetString(1);
                if (kind is not (BookmarkKinds.Session or BookmarkKinds.Message) || ParseTime(reader.GetString(12)) is not { } created)
                {
                    continue;
                }

                bookmarks.Add(new(
                    reader.GetString(0), kind, reader.GetString(2), reader.GetString(3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7),
                    Text(reader, 8), Text(reader, 9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : ParseTime(reader.GetString(11)), created, Text(reader, 13)));
            }

            return bookmarks;
        }, cancellationToken);

    internal ValueTask InsertAsync(Bookmark bookmark, CancellationToken cancellationToken)
        => _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {Table} ({Columns})
                VALUES ($id, $kind, $session, $title, $project, $role, $label, $excerpt, $run, $content, $offset, $message_at, $created_at, $created_by);
                """;
            command.Parameters.AddWithValue("$id", bookmark.Id);
            command.Parameters.AddWithValue("$kind", bookmark.Kind);
            command.Parameters.AddWithValue("$session", bookmark.SessionId);
            command.Parameters.AddWithValue("$title", bookmark.SessionTitle);
            command.Parameters.AddWithValue("$project", Value(bookmark.ProjectId));
            command.Parameters.AddWithValue("$role", Value(bookmark.Role));
            command.Parameters.AddWithValue("$label", Value(bookmark.Label));
            command.Parameters.AddWithValue("$excerpt", Value(bookmark.Excerpt));
            command.Parameters.AddWithValue("$run", Value(bookmark.RunId));
            command.Parameters.AddWithValue("$content", Value(bookmark.ContentId));
            command.Parameters.AddWithValue("$offset", (object?)bookmark.Offset ?? DBNull.Value);
            command.Parameters.AddWithValue("$message_at", bookmark.MessageAt is { } at ? FormatTime(at) : DBNull.Value as object);
            command.Parameters.AddWithValue("$created_at", FormatTime(bookmark.CreatedAt));
            command.Parameters.AddWithValue("$created_by", Value(bookmark.CreatedBy));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    internal ValueTask UpdateLabelAsync(string id, string? label, CancellationToken cancellationToken)
        => ExecuteAsync($"UPDATE {Table} SET label = $value WHERE id = $id;", cancellationToken, ("$id", id), ("$value", Value(label)));

    internal ValueTask UpdateOffsetAsync(string id, long offset, CancellationToken cancellationToken)
        => ExecuteAsync($"UPDATE {Table} SET journal_offset = $value WHERE id = $id;", cancellationToken, ("$id", id), ("$value", offset));

    /// <summary>Writes the title and the project of the sessions of some bookmarks as they are now.</summary>
    internal ValueTask UpdateSessionsAsync(IReadOnlyList<Bookmark> bookmarks, CancellationToken cancellationToken)
        => _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {Table} SET session_title = $title, project_id = $project WHERE id = $id;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var title = command.Parameters.Add("$title", SqliteType.Text);
            var project = command.Parameters.Add("$project", SqliteType.Text);
            foreach (var bookmark in bookmarks)
            {
                id.Value = bookmark.Id;
                title.Value = bookmark.SessionTitle;
                project.Value = Value(bookmark.ProjectId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);

    internal ValueTask DeleteAsync(string id, CancellationToken cancellationToken)
        => ExecuteAsync($"DELETE FROM {Table} WHERE id = $id;", cancellationToken, ("$id", id));

    internal ValueTask DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        => ExecuteAsync($"DELETE FROM {Table} WHERE session_id = $id;", cancellationToken, ("$id", sessionId));

    private ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        => _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private static object Value(string? text) => text is null ? DBNull.Value : text;

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static string FormatTime(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string text)
        => DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
}
