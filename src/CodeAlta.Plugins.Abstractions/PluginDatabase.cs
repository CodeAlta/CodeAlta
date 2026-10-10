using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Runs the steps that bring the tables of a plugin from the version the host has recorded to the version the
/// plugin now needs.
/// </summary>
/// <param name="connection">The connection of the write transaction the steps run in.</param>
/// <param name="fromVersion">The version the host has recorded for the plugin; 0 when it has no tables yet.</param>
/// <param name="toVersion">The version passed to <see cref="IPluginDatabase.MigrateAsync"/>.</param>
/// <param name="cancellationToken">A token to cancel the migration.</param>
/// <returns>A task representing the migration.</returns>
/// <remarks>
/// A migration that creates a table writes <c>if (fromVersion &lt; 1) { create }</c>, then
/// <c>if (fromVersion &lt; 2) { alter }</c>, and so on: it runs once for a plugin that is several versions behind.
/// The host keeps the steps while the plugin runs, and runs them again when the file of the database had to be
/// replaced (it was damaged): they depend on nothing but their arguments.
/// </remarks>
public delegate ValueTask PluginDatabaseMigration(
    SqliteConnection connection,
    int fromVersion,
    int toVersion,
    CancellationToken cancellationToken);

/// <summary>
/// The tables a plugin keeps in the SQLite database of the application: the real data of a plugin, which a JSON
/// file of <see cref="IPluginStateStore"/> would carry badly.
/// </summary>
/// <remarks>
/// <para>
/// There is one database for each instance of CodeAlta (the developer instance has its own), shared with the list of
/// sessions and with the other plugins. A plugin gives every table, index, view and trigger it creates the name
/// <see cref="TablePrefix"/> starts: the host checks it when the plugin migrates, so that a plugin cannot create or
/// drop the objects of another one. A plugin is trusted code: the prefix is a convention the host verifies, not a wall.
/// </para>
/// <para>
/// Commands run on the connection they are given; they need no transaction object. A write is one transaction: do
/// not begin or end one inside it. Keep writes short, because every writer of the application waits for the one
/// before it; the host logs a write that holds the queue too long. A plugin that reads a large history commits every
/// few megabytes.
/// </para>
/// <para>
/// Use <see cref="IPluginStateStore"/> for settings and small documents, and this service for rows that are
/// queried, added up or filtered. The data stays when the plugin is disabled; the host drops the tables when the
/// user removes the plugin and confirms it.
/// </para>
/// </remarks>
public interface IPluginDatabase
{
    /// <summary>
    /// Gets a value indicating whether the host has an application database. A host without one (a catalog-only
    /// tool, a test) gives a plugin a service whose operations throw <see cref="InvalidOperationException"/>.
    /// </summary>
    bool HasDatabase { get; }

    /// <summary>
    /// Gets the prefix of every table of this plugin, given by the host from the key of the plugin: for the
    /// built-in plugin <c>builtin:statistics</c> it is <c>statistics_</c>. It is lowercase, ends with an underscore,
    /// and no prefix starts another one. It is empty when <see cref="HasDatabase"/> is <see langword="false"/>.
    /// </summary>
    string TablePrefix { get; }

    /// <summary>
    /// Brings the tables of the plugin to a version. The host keeps the version recorded for the plugin and runs
    /// <paramref name="migrate"/> once, in a write transaction, when that version is lower than
    /// <paramref name="version"/>; the version is recorded with the changes, or neither is.
    /// </summary>
    /// <remarks>
    /// A plugin migrates once, when it is activated. When the file of the database is replaced while the plugin
    /// runs (it was damaged), the host runs the last migration again before the next read or write of the plugin:
    /// the tables exist again, empty or as the restored copy had them. A plugin that keeps in memory what it wrote
    /// reads it again from its tables when it matters.
    /// </remarks>
    /// <param name="version">The version the plugin needs; at least 1.</param>
    /// <param name="migrate">The steps.</param>
    /// <param name="cancellationToken">A token to cancel the migration.</param>
    /// <returns>A task representing the migration.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is below 1.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="migrate"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host has no database; the recorded version is higher than <paramref name="version"/> (the tables were
    /// written by a newer build of the plugin); the steps created, changed or dropped an object whose name does
    /// not start with <see cref="TablePrefix"/>, or an index or a trigger on a table whose name does not (the
    /// transaction is rolled back); or the migration was started inside a write.
    /// </exception>
    /// <exception cref="SqliteException">SQLite failed.</exception>
    ValueTask MigrateAsync(int version, PluginDatabaseMigration migrate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads on a connection of its own. A read never waits for a writer, and cannot write.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The host has no database.</exception>
    /// <exception cref="SqliteException">SQLite failed.</exception>
    ValueTask<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes in one transaction, in turn with all the other writers of the application. The transaction is
    /// committed when <paramref name="write"/> returns and rolled back when it throws. A write that is still queued
    /// when <paramref name="cancellationToken"/> is canceled never runs. A write does not start another write or a
    /// migration and wait for it: it would wait for itself, so the host refuses it.
    /// </summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The host has no database, or the write was started inside another write.</exception>
    /// <exception cref="SqliteException">SQLite failed, or another process held the write lock past the busy timeout.</exception>
    ValueTask WriteAsync(Func<SqliteConnection, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default);
}

/// <summary>
/// The database of a host that has none: <see cref="IPluginDatabase.HasDatabase"/> is <see langword="false"/> and
/// every operation throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class NoopPluginDatabase : IPluginDatabase
{
    /// <inheritdoc />
    public bool HasDatabase => false;

    /// <inheritdoc />
    public string TablePrefix => string.Empty;

    /// <inheritdoc />
    public ValueTask MigrateAsync(int version, PluginDatabaseMigration migrate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        throw CreateException();
    }

    /// <inheritdoc />
    public ValueTask<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        throw CreateException();
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(Func<SqliteConnection, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        throw CreateException();
    }

    private static InvalidOperationException CreateException()
        => new("This host has no application database: check IPluginServices.Database.HasDatabase before using it.");
}
