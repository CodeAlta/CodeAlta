using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;
using CodeAlta.Plugins;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>A real application database in a temporary folder, with the store of the statistics plugin over it.</summary>
internal sealed class StoreHarness : IAsyncDisposable
{
    private StoreHarness(string root, TimeZoneInfo timeZone)
    {
        Root = root;
        TimeZone = timeZone;
    }

    public string Root { get; }

    public TimeZoneInfo TimeZone { get; private set; }

    public ApplicationDatabase Application { get; private set; } = null!;

    public PluginDatabase Database { get; private set; } = null!;

    public StatisticsStore Store { get; private set; } = null!;

    public static async Task<StoreHarness> CreateAsync(TimeZoneInfo? timeZone = null, string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "CodeAlta.Plugin.Statistics.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var harness = new StoreHarness(root, timeZone ?? TimeZoneInfo.Utc);
        await harness.OpenAsync();
        return harness;
    }

    /// <summary>Closes the database and opens it again, as a restart of the application does; a new time zone may be given.</summary>
    public async Task ReopenAsync(TimeZoneInfo? timeZone = null)
    {
        await Application.DisposeAsync();
        TimeZone = timeZone ?? TimeZone;
        await OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Application.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    public async Task<string> DumpAsync(params string[] skipTables)
    {
        var builder = new StringBuilder();
        var prefix = Store.Prefix;
        var tables = await Store.ReadAsync(sql => sql.Query(
            $"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '{prefix}%' ORDER BY name",
            static reader => reader.GetString(0)));
        foreach (var table in tables)
        {
            if (table.EndsWith("journal", StringComparison.Ordinal) || table.EndsWith("meta", StringComparison.Ordinal) || skipTables.Any(table.EndsWith))
            {
                continue;
            }

            var lines = await Store.ReadAsync(sql => sql.Query(
                $"SELECT * FROM {table}",
                static reader =>
                {
                    var line = new StringBuilder();
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        line.Append(reader.GetName(index)).Append('=').Append(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).Append(';');
                    }

                    return line.ToString();
                }));
            lines.Sort(StringComparer.Ordinal);
            builder.Append(table).Append(':').AppendLine();
            foreach (var line in lines)
            {
                builder.Append("  ").AppendLine(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>Checks that every day, month and year table equals the sum of the quarters it covers, computed here from the quarters.</summary>
    public async Task VerifyRollupsAsync()
    {
        var days = new LocalDays(TimeZone);
        foreach (var table in FactTables.All)
        {
            var columns = table.Keys.Select(static key => key.Name).Concat(table.Measures.Select(static measure => measure.Name)).ToArray();
            var quarters = await Store.ReadAsync(sql => sql.Query(
                $"SELECT q, {string.Join(", ", columns)} FROM {table.TableName(Store.Prefix, RollupLevel.Quarter)}",
                reader => (Q: (int)reader.GetInt64(0), Row: ReadRow(reader, 1, table))));
            foreach (var level in new[] { RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
            {
                var expected = new Dictionary<string, long[]>(StringComparer.Ordinal);
                foreach (var (q, row) in quarters)
                {
                    var day = days.DayOf(q);
                    var period = level switch
                    {
                        RollupLevel.Day => day,
                        RollupLevel.Month => LocalDays.MonthOf(day),
                        _ => LocalDays.YearOf(day),
                    };
                    var id = period + "|" + string.Join('|', row.Keys.Select(static key => Convert.ToString(key, CultureInfo.InvariantCulture)));
                    if (!expected.TryGetValue(id, out var sums))
                    {
                        expected[id] = sums = new long[table.Measures.Length];
                    }

                    for (var index = 0; index < sums.Length; index++)
                    {
                        sums[index] = table.Measures[index].IsMax ? Math.Max(sums[index], row.Measures[index]) : sums[index] + row.Measures[index];
                    }
                }

                var stored = await Store.ReadAsync(sql => sql.Query(
                    $"SELECT p, {string.Join(", ", columns)} FROM {table.TableName(Store.Prefix, level)}",
                    reader => (P: (int)reader.GetInt64(0), Row: ReadRow(reader, 1, table))));
                var actual = new Dictionary<string, long[]>(StringComparer.Ordinal);
                foreach (var (p, row) in stored)
                {
                    actual[p + "|" + string.Join('|', row.Keys.Select(static key => Convert.ToString(key, CultureInfo.InvariantCulture)))] = row.Measures;
                }

                foreach (var (id, sums) in expected.Where(static pair => pair.Value.Any(static value => value != 0)))
                {
                    Assert.IsTrue(actual.TryGetValue(id, out var found), $"{table.Name} {level}: no row for {id}");
                    CollectionAssert.AreEqual(sums, found, $"{table.Name} {level} {id}");
                }

                foreach (var (id, measures) in actual.Where(static pair => pair.Value.Any(static value => value != 0)))
                {
                    Assert.IsTrue(expected.TryGetValue(id, out var sums) && sums.SequenceEqual(measures), $"{table.Name} {level}: unexpected row {id}");
                }
            }
        }

        await VerifyExtremesAsync(days);
    }

    public static (object[] Keys, long[] Measures) ReadRow(Microsoft.Data.Sqlite.SqliteDataReader reader, int offset, FactTable table)
    {
        var keys = new object[table.Keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = reader.GetValue(offset + index);
        }

        var measures = new long[table.Measures.Length];
        for (var index = 0; index < measures.Length; index++)
        {
            measures[index] = reader.GetInt64(offset + keys.Length + index);
        }

        return (keys, measures);
    }

    public async Task<long> SumAsync(string table, string column)
        => await Store.ReadAsync(sql => sql.ScalarLong($"SELECT COALESCE(SUM({column}), 0) FROM {Store.Prefix}{table}"));

    public async Task<long> CountAsync(string table)
        => await Store.ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {Store.Prefix}{table}"));

    private async Task VerifyExtremesAsync(LocalDays days)
    {
        var quarters = await Store.ReadAsync(sql => sql.Query(
            $"SELECT q, measure, subject, value, at_ms FROM {Store.Prefix}extreme_q",
            static reader => (Q: (int)reader.GetInt64(0), Measure: reader.GetInt64(1), Subject: reader.GetString(2), Value: reader.GetInt64(3), At: reader.GetInt64(4))));
        foreach (var (level, table) in new[] { (RollupLevel.Day, "extreme_day"), (RollupLevel.Month, "extreme_month"), (RollupLevel.Year, "extreme_year") })
        {
            var expected = new Dictionary<string, (long Value, long At)>(StringComparer.Ordinal);
            foreach (var row in quarters)
            {
                var day = days.DayOf(row.Q);
                var period = level switch { RollupLevel.Day => day, RollupLevel.Month => LocalDays.MonthOf(day), _ => LocalDays.YearOf(day) };
                var id = $"{period}|{row.Measure}|{row.Subject}";
                if (!expected.TryGetValue(id, out var current) || row.Value > current.Value || (row.Value == current.Value && row.At < current.At))
                {
                    expected[id] = (row.Value, row.At);
                }
            }

            var actual = (await Store.ReadAsync(sql => sql.Query(
                $"SELECT p, measure, subject, value, at_ms FROM {Store.Prefix}{table}",
                static reader => ($"{reader.GetInt64(0)}|{reader.GetInt64(1)}|{reader.GetString(2)}", (Value: reader.GetInt64(3), At: reader.GetInt64(4)))))).ToDictionary();
            CollectionAssert.AreEquivalent(expected.ToArray(), actual.ToArray(), table);
        }
    }

    private async Task OpenAsync()
    {
        Application = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(Root, "data", "alta.sqlite3") });
        Database = new PluginDatabase(Application, "builtin:statistics");
        Store = new StatisticsStore(Database, new LocalDays(TimeZone));
        await Store.InitializeAsync();
    }
}
