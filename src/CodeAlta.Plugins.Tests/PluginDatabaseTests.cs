using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginDatabaseTests
{
    [TestMethod]
    public void TablePrefix_OfABuiltInPlugin_IsItsIdAndAnUnderscore()
    {
        Assert.AreEqual("statistics_", PluginDatabase.CreateTablePrefix("builtin:statistics"));
        Assert.AreEqual("git_", PluginDatabase.CreateTablePrefix("builtin:git"));
        Assert.AreEqual("mcp2_", PluginDatabase.CreateTablePrefix("builtin:mcp2"));
        Assert.AreEqual("plugin:statistics", PluginDatabase.GetOwner("builtin:statistics"));
        Assert.AreEqual("plugin:Todo:todo", PluginDatabase.GetOwner("Todo:todo"));
    }

    [TestMethod]
    public void TablePrefix_OfOtherKeys_IsReadableAndHashed()
    {
        var source = PluginDatabase.CreateTablePrefix("My.Plugin:My.Plugin.Plugin");

        StringAssert.Matches(source, new System.Text.RegularExpressions.Regex("^myplugin[a-z]*[0-9a-f]*_$"));
        Assert.AreEqual(source, PluginDatabase.CreateTablePrefix("My.Plugin:My.Plugin.Plugin"), "The same key gives the same prefix after a restart.");
        foreach (var key in new[] { "builtin:session", "builtin:app", "builtin:Not-Plain", "builtin:", "9lives:Plugin", "::::" })
        {
            var prefix = PluginDatabase.CreateTablePrefix(key.Length == 0 ? "x" : key);
            StringAssert.Matches(prefix, new System.Text.RegularExpressions.Regex("^[a-z][a-z0-9]*_$"), key);
            Assert.IsFalse(prefix is "session_" or "app_", key);
        }
    }

    [TestMethod]
    public void TablePrefixes_NeverCollideAndNoneStartsAnother()
    {
        var keys = new[]
        {
            "builtin:statistics", "builtin:git", "builtin:jira", "builtin:mcp", "builtin:ui", "builtin:stat",
            "Stats:Stats.StatsPlugin", "stats:Stats.StatsPlugin", "My-Plugin:A", "My_Plugin:A", "MyPlugin:A", "my.plugin:a",
            "Todo:todo", "Todo:todo2", "Todo:todo-", "Todo:Todo", "a:b", "a:b:c", "ab:c",
            "builtin:session", "builtin:app", "builtin:sqlite", "session:session", "app:app",
        };

        var prefixes = keys.Select(PluginDatabase.CreateTablePrefix).ToArray();

        Assert.AreEqual(keys.Length, prefixes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var prefix in prefixes)
        {
            Assert.AreEqual(1, prefix.Count(static character => character == '_'), prefix);
            Assert.AreEqual(1, prefixes.Count(other => other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), $"Another prefix starts with {prefix}");
        }
    }

    [TestMethod]
    public async Task TwoPlugins_KeepTheirOwnTablesAndCannotTouchTheOthers()
    {
        using var temp = new TestTempDirectory();
        await using var application = CreateApplicationDatabase(temp);
        var first = new PluginDatabase(application, "builtin:first");
        var second = new PluginDatabase(application, "builtin:second");
        await first.MigrateAsync(1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE first_rows (id INTEGER); INSERT INTO first_rows VALUES (1);", token));
        await second.MigrateAsync(1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE second_rows (id INTEGER);", token));

        // A table of the other plugin, a table of the application, a name without the prefix.
        foreach (var sql in new[] { "DROP TABLE first_rows;", "ALTER TABLE first_rows ADD COLUMN x TEXT;", "CREATE TABLE session_projection_cache_x (id INTEGER);", "CREATE TABLE app_meta_x (id INTEGER);", "CREATE TABLE loose (id INTEGER);" })
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await second.MigrateAsync(2, (connection, from, to, token) => ExecuteAsync(connection, sql, token)), sql);
        }

        Assert.AreEqual(1L, await first.ReadAsync((connection, token) => ScalarAsync(connection, "SELECT COUNT(*) FROM first_rows;", token)));
        Assert.AreEqual(1, await application.GetVersionAsync("plugin:second"), "A refused migration records nothing.");
    }

    [TestMethod]
    public async Task ThePluginFindsItsTablesAgainAfterARestart_AndMigratesOnlyOnce()
    {
        using var temp = new TestTempDirectory();
        var migrations = 0;
        for (var start = 0; start < 3; start++)
        {
            await using var application = CreateApplicationDatabase(temp);
            var database = new PluginDatabase(application, "builtin:statistics");
            await database.MigrateAsync(1, (connection, from, to, token) =>
            {
                migrations++;
                return ExecuteAsync(connection, "CREATE TABLE statistics_day (n INTEGER);", token);
            });
            await database.WriteAsync((connection, token) => ExecuteAsync(connection, "INSERT INTO statistics_day VALUES (1);", token));

            Assert.AreEqual(start + 1L, await database.ReadAsync((connection, token) => ScalarAsync(connection, "SELECT COUNT(*) FROM statistics_day;", token)));
        }

        Assert.AreEqual(1, migrations);
    }

    [TestMethod]
    public async Task OperationsOfAPlugin_EndWithItsLifetime()
    {
        using var temp = new TestTempDirectory();
        await using var application = CreateApplicationDatabase(temp);
        using var lifetime = new CancellationTokenSource();
        var database = new PluginDatabase(application, "builtin:statistics", lifetime.Token);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = application.WriteAsync("other", async (connection, token) =>
        {
            started.SetResult();
            await release.Task;
        }).AsTask();
        await started.Task;
        var ran = false;
        var queued = database.WriteAsync((connection, token) =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        }).AsTask();

        await lifetime.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        await holder;
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task AHostWithoutADatabase_GivesAServiceThatSaysSo()
    {
        var noop = new NoopPluginServices(XenoAtom.Logging.LogManager.GetLogger("test"));
        IPluginDatabase database = noop.Database;

        Assert.IsFalse(database.HasDatabase);
        Assert.AreEqual(string.Empty, database.TablePrefix);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await database.MigrateAsync(1, (connection, from, to, token) => ValueTask.CompletedTask));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await database.ReadAsync((connection, token) => ValueTask.FromResult(0)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await database.WriteAsync((connection, token) => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task ARuntimeWithADatabase_GivesEachPluginItsOwnTables_AndWithoutOneGivesNone()
    {
        using var temp = new TestTempDirectory();
        await using var application = CreateApplicationDatabase(temp);
        await using var runtime = new PluginRuntimeManager();
        var started = await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, ApplicationDatabase = application, BuiltIns = CreateBuiltIns() });

        var one = started.ActivePlugins.Single(static plugin => plugin.Descriptor.RuntimeKey == "builtin:one").RuntimeContext.Services.Database;
        var two = started.ActivePlugins.Single(static plugin => plugin.Descriptor.RuntimeKey == "builtin:two").RuntimeContext.Services.Database;
        Assert.IsTrue(one.HasDatabase);
        Assert.AreEqual("one_", one.TablePrefix);
        Assert.AreEqual("two_", two.TablePrefix);
        Assert.AreSame(application, runtime.ApplicationDatabase);
        await one.MigrateAsync(1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE one_rows (id INTEGER);", token));
        await two.MigrateAsync(1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE two_rows (id INTEGER);", token));

        CollectionAssert.AreEqual(new[] { "one_rows" }, (await runtime.ListPluginTablesAsync("builtin:one")).ToArray());
        CollectionAssert.AreEqual(new[] { "one_rows" }, (await runtime.DropPluginTablesAsync("builtin:one")).ToArray());
        Assert.AreEqual(0, (await runtime.ListPluginTablesAsync("builtin:one")).Count);
        Assert.AreEqual(1, (await runtime.ListPluginTablesAsync("builtin:two")).Count);
        Assert.AreEqual(0, await application.GetVersionAsync("plugin:one"));

        await using var bare = new PluginRuntimeManager();
        var bareStarted = await bare.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, BuiltIns = CreateBuiltIns() });
        var none = bareStarted.ActivePlugins[0].RuntimeContext.Services.Database;
        Assert.IsFalse(none.HasDatabase);
        Assert.IsNull(bare.ApplicationDatabase);
        Assert.AreEqual(0, (await bare.ListPluginTablesAsync("builtin:one")).Count);
        Assert.AreEqual(0, (await bare.DropPluginTablesAsync("builtin:one")).Count);
    }

    [TestMethod]
    public async Task ARuntimeThatNamesAStateRoot_OpensTheDatabaseOfThatRootAndDisposesIt()
    {
        using var temp = new TestTempDirectory();
        var stateRoot = Path.Combine(temp.Path, "dev");
        var runtime = new PluginRuntimeManager();
        var started = await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, StateRoot = stateRoot, BuiltIns = CreateBuiltIns() });
        var database = started.ActivePlugins[0].RuntimeContext.Services.Database;
        await database.MigrateAsync(1, (connection, from, to, token) => ExecuteAsync(connection, $"CREATE TABLE {database.TablePrefix}rows (id INTEGER);", token));

        var opened = runtime.ApplicationDatabase!;
        Assert.AreEqual(Path.Combine(stateRoot, "data", "alta.sqlite3"), opened.DatabasePath);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "data", "alta.sqlite3")), "The developer instance never opens the database of the other instance.");

        await runtime.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await opened.WriteAsync("test", (connection, token) => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task ARuntime_DoesNotDisposeADatabaseItWasGiven()
    {
        using var temp = new TestTempDirectory();
        await using var application = CreateApplicationDatabase(temp);
        var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, ApplicationDatabase = application, StateRoot = temp.Path, BuiltIns = CreateBuiltIns() });

        await runtime.DisposeAsync();

        await application.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE still_open (id INTEGER);", token));
    }

    private static BuiltInPluginDefinition[] CreateBuiltIns()
        =>
        [
            new() { Id = "one", DisplayName = "One", PluginType = typeof(OnePlugin), Factory = static () => new OnePlugin() },
            new() { Id = "two", DisplayName = "Two", PluginType = typeof(TwoPlugin), Factory = static () => new TwoPlugin() },
        ];

    private static ApplicationDatabase CreateApplicationDatabase(TestTempDirectory temp)
        => new(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class OnePlugin : PluginBase
    {
    }

    private sealed class TwoPlugin : PluginBase
    {
    }
}
