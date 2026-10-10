using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugins;

/// <summary>
/// The tables of one plugin in the application database: the <see cref="IPluginDatabase"/> a host gives a plugin.
/// </summary>
/// <remarks>
/// <para>
/// The plugin is identified by its runtime key. The same plugin therefore finds the same tables after every
/// restart, in the database of the instance it runs in. Operations end when the lifetime of the plugin ends.
/// </para>
/// <para>
/// When the file of the database is replaced while the plugin runs (<see cref="IApplicationDatabase.Generation"/>
/// changes: the file was damaged, or gone), the last migration of the plugin runs again before its next read or
/// write: the tables exist again, empty or as the restored copy had them.
/// </para>
/// </remarks>
public sealed class PluginDatabase : IPluginDatabase
{
    private const string BuiltInKeyPrefix = "builtin:";
    private const int MaxBuiltInIdLength = 24;
    private const int MaxNameLength = 16;

    // Names the application keeps for its own tables: a plugin of this name takes the hashed form of the prefix.
    private static readonly System.Collections.Frozen.FrozenSet<string> ReservedNames = new[] { "alta", "app", "plugin", "session", "sqlite" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IApplicationDatabase _database;
    private readonly CancellationToken _lifetime;

    // The last migration the plugin asked for, and the generation of the file it ran on: a plugin migrates when it
    // is activated, and a file that is replaced while it runs (damage) has none of its tables, or older ones.
    private Migration? _migration;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class.
    /// </summary>
    /// <param name="database">The application database of the instance.</param>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin, such as <c>builtin:statistics</c>.</param>
    /// <param name="lifetime">A token canceled when the plugin is deactivated; its queued operations are canceled with it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is empty.</exception>
    public PluginDatabase(IApplicationDatabase database, string pluginRuntimeKey, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        _database = database;
        _lifetime = lifetime;
        Owner = GetOwner(pluginRuntimeKey);
        TablePrefix = CreateTablePrefix(pluginRuntimeKey);
    }

    /// <inheritdoc />
    public bool HasDatabase => true;

    /// <inheritdoc />
    public string TablePrefix { get; }

    /// <summary>Gets the name under which the application database records the version of the tables of the plugin.</summary>
    public string Owner { get; }

    /// <summary>
    /// Gets the owner name of a plugin in the application database: <c>plugin:statistics</c> for the built-in plugin
    /// <c>builtin:statistics</c>, and <c>plugin:</c> followed by the key for the others.
    /// </summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <returns>The owner name.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is empty.</exception>
    public static string GetOwner(string pluginRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        return "plugin:" + (pluginRuntimeKey.StartsWith(BuiltInKeyPrefix, StringComparison.Ordinal)
            ? pluginRuntimeKey[BuiltInKeyPrefix.Length..]
            : pluginRuntimeKey);
    }

    /// <summary>
    /// Derives the prefix of the tables of a plugin from its runtime key.
    /// </summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <returns>A lowercase SQL identifier prefix that ends with an underscore.</returns>
    /// <remarks>
    /// A built-in plugin <c>builtin:&lt;id&gt;</c> whose id is lowercase letters and digits, starting with a letter,
    /// gets <c>&lt;id&gt;_</c>: <c>builtin:statistics</c> is <c>statistics_</c>. Every other key (a source plugin, or a
    /// built-in id that is not a plain word or that the application keeps for itself) gets its letters and digits, at most
    /// sixteen, then eight hexadecimal digits of the SHA-256 of the whole key, then an underscore. A prefix has no
    /// underscore but its last character, so no prefix starts another one, and two keys never share a prefix unless
    /// their hashes collide.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is empty.</exception>
    public static string CreateTablePrefix(string pluginRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        if (pluginRuntimeKey.StartsWith(BuiltInKeyPrefix, StringComparison.Ordinal))
        {
            var id = pluginRuntimeKey[BuiltInKeyPrefix.Length..];
            if (IsPlainWord(id) && !ReservedNames.Contains(id))
            {
                return id + "_";
            }
        }

        var name = new StringBuilder(MaxNameLength);
        foreach (var character in pluginRuntimeKey)
        {
            if (name.Length == MaxNameLength) break;
            if (char.IsAsciiLetterOrDigit(character)) name.Append(char.ToLowerInvariant(character));
        }

        if (name.Length == 0 || !char.IsAsciiLetterLower(name[0]))
        {
            name.Insert(0, 'p');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pluginRuntimeKey));
        return name + Convert.ToHexStringLower(hash.AsSpan(0, 4)) + "_";
    }

    /// <inheritdoc />
    public async ValueTask MigrateAsync(int version, PluginDatabaseMigration migrate, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(migrate);
        using var linked = Link(cancellationToken);
        await MigrateCoreAsync(version, migrate, linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var linked = Link(cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            await MigrateAgainIfReplacedAsync(linked.Token).ConfigureAwait(false);
            try
            {
                return await _database.ReadAsync(Owner, read, linked.Token).ConfigureAwait(false);
            }
            catch (SqliteException) when (attempt == 0 && IsReplacedSinceMigration())
            {
                // The file was replaced under this read, which then read a file without the tables of the plugin.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(Func<SqliteConnection, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var linked = Link(cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            await MigrateAgainIfReplacedAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _database.WriteAsync(Owner, write, linked.Token).ConfigureAwait(false);
                return;
            }
            catch (SqliteException) when (attempt == 0 && IsReplacedSinceMigration())
            {
                // The file was replaced under this write: it was rolled back, on a file without the tables of the plugin.
            }
        }
    }

    private async ValueTask MigrateCoreAsync(int version, PluginDatabaseMigration migrate, CancellationToken cancellationToken)
    {
        // The generation is read first: a file that is replaced while the steps run is seen as replaced, and the
        // steps, which then do nothing, run once more.
        var generation = _database.Generation;
        await _database.MigrateAsync(
                Owner,
                TablePrefix,
                version,
                (connection, from, to, token) => migrate(connection, from, to, token),
                cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _migration, new Migration(version, migrate, generation));
    }

    private bool IsReplacedSinceMigration()
        => Volatile.Read(ref _migration) is { } migration && migration.Generation != _database.Generation;

    private async ValueTask MigrateAgainIfReplacedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _migration) is { } migration && migration.Generation != _database.Generation)
        {
            await MigrateCoreAsync(migration.Version, migration.Migrate, cancellationToken).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource Link(CancellationToken cancellationToken)
        => CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);

    private sealed record Migration(int Version, PluginDatabaseMigration Migrate, int Generation);

    private static bool IsPlainWord(string id)
    {
        if (id.Length is 0 or > MaxBuiltInIdLength || !char.IsAsciiLetterLower(id[0]))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)) return false;
        }

        return true;
    }
}
