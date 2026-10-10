using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugin.Statistics.Store;

/// <summary>
/// Runs SQL on one connection with the commands cached by their text, so that a loop of upserts prepares each statement once.
/// The values are given in order and bound to <c>@p0</c>, <c>@p1</c>, and so on.
/// </summary>
/// <remarks>Not thread-safe; it lives as long as one read or one write of the store.</remarks>
internal sealed class SqlSession : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

    /// <summary>Initializes a session over a connection.</summary>
    /// <param name="connection">The connection of a read or of a write transaction.</param>
    public SqlSession(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">The values of <c>@p0</c>, <c>@p1</c>...</param>
    /// <returns>The number of rows changed.</returns>
    public int Execute(string sql, params ReadOnlySpan<object?> values)
        => Prepare(sql, values).ExecuteNonQuery();

    /// <summary>Runs a statement that returns no rows, with a text that has no cached command: a schema change.</summary>
    /// <param name="sql">The statement or statements.</param>
    public void ExecuteScript(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs a query that returns one value.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">The values of <c>@p0</c>, <c>@p1</c>...</param>
    /// <returns>The first column of the first row; null when there is no row or the value is null.</returns>
    public object? Scalar(string sql, params ReadOnlySpan<object?> values)
    {
        var value = Prepare(sql, values).ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>Runs a query that returns one integer.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">The values of <c>@p0</c>, <c>@p1</c>...</param>
    /// <returns>The value; zero when there is no row or the value is null.</returns>
    public long ScalarLong(string sql, params ReadOnlySpan<object?> values)
        => Scalar(sql, values) is { } value ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>Runs a query and reads every row with a function.</summary>
    /// <typeparam name="T">The type of a row.</typeparam>
    /// <param name="sql">The query.</param>
    /// <param name="read">Turns the current row of the reader into a value.</param>
    /// <param name="values">The values of <c>@p0</c>, <c>@p1</c>...</param>
    /// <returns>The rows.</returns>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params ReadOnlySpan<object?> values)
    {
        var rows = new List<T>();
        using var reader = Prepare(sql, values).ExecuteReader();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var command in _commands.Values)
        {
            command.Dispose();
        }

        _commands.Clear();
    }

    private SqliteCommand Prepare(string sql, ReadOnlySpan<object?> values)
    {
        if (!_commands.TryGetValue(sql, out var command))
        {
            command = _connection.CreateCommand();
            command.CommandText = sql;
            for (var index = 0; index < values.Length; index++)
            {
                command.Parameters.Add(new SqliteParameter("@p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
            }

            _commands[sql] = command;
        }

        for (var index = 0; index < values.Length; index++)
        {
            command.Parameters[index].Value = values[index] ?? DBNull.Value;
        }

        return command;
    }
}
