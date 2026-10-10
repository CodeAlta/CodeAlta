using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

/// <summary>
/// The SQLite database of one CodeAlta instance, shared by the application and its plugins.
/// </summary>
/// <remarks>
/// Reads never wait for a writer. Writes run one at a time, in turn, each in one transaction. Every owner of
/// tables names itself, and the database records the version of its tables.
/// </remarks>
public interface IApplicationDatabase : IAsyncDisposable
{
    /// <summary>Gets the path of the database file.</summary>
    string DatabasePath { get; }

    /// <summary>
    /// Gets the number of times the file was replaced because it was damaged. An owner that keeps data in memory
    /// about the file compares it with the one it saw to learn that the file changed under it.
    /// </summary>
    int Generation { get; }

    /// <summary>Gets what happened the last time the file was found damaged, or <see langword="null"/> when it never was.</summary>
    ApplicationDatabaseRecovery? LastRecovery { get; }

    /// <summary>
    /// Reads on a connection of its own; it never waits for a writer and cannot write.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="owner">The owner, for diagnostics.</param>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The database was disposed.</exception>
    /// <exception cref="SqliteException">SQLite failed.</exception>
    ValueTask<T> ReadAsync<T>(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask<T>> read,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes in one transaction, in turn with the other writers.
    /// </summary>
    /// <param name="owner">The owner, for diagnostics and for the log of a write that holds the queue too long.</param>
    /// <param name="write">The write. It is committed when it returns and rolled back when it throws.</param>
    /// <param name="cancellationToken">A token to cancel the write. A write that is still queued when it is canceled never runs.</param>
    /// <returns>A task representing the write.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The database was disposed.</exception>
    /// <exception cref="SqliteException">SQLite failed.</exception>
    ValueTask WriteAsync(
        string owner,
        Func<SqliteConnection, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the version recorded for an owner.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The version; 0 when the owner never migrated.</returns>
    ValueTask<int> GetVersionAsync(string owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the tables of an owner to a version: the steps run once, in one write transaction, when the recorded
    /// version is lower than <paramref name="version"/>, and the version is recorded with them.
    /// </summary>
    /// <param name="owner">The owner, such as <c>session_cache</c> or <c>plugin:statistics</c>.</param>
    /// <param name="tablePrefix">
    /// The prefix every table, index, view and trigger the steps create, change or drop must start with, or
    /// <see langword="null"/> for an owner that is part of the application.
    /// </param>
    /// <param name="version">The version the owner needs; at least 1.</param>
    /// <param name="migrate">The steps.</param>
    /// <param name="cancellationToken">A token to cancel the migration.</param>
    /// <returns>A task representing the migration.</returns>
    /// <exception cref="InvalidOperationException">
    /// The recorded version is higher than <paramref name="version"/>, or the steps touched an object outside
    /// <paramref name="tablePrefix"/>. The transaction is rolled back.
    /// </exception>
    ValueTask MigrateAsync(
        string owner,
        string? tablePrefix,
        int version,
        ApplicationDatabaseMigration migrate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the names of the tables that start with a prefix, for the confirmation that names them.
    /// </summary>
    /// <param name="tablePrefix">The prefix.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The table names, in alphabetical order.</returns>
    ValueTask<IReadOnlyList<string>> ListTablesAsync(string tablePrefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every table, view, index and trigger of an owner and forgets its version; its next migration starts from nothing.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="tablePrefix">The prefix of its objects.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The names of the tables that were dropped.</returns>
    ValueTask<IReadOnlyList<string>> DropTablesAsync(string owner, string tablePrefix, CancellationToken cancellationToken = default);
}
