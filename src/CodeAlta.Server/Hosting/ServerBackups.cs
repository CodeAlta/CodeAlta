using CodeAlta.Fleet.Update;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Hosting;

/// <summary>A backup of the database in the data root, and whether it keeps a secret of the server in clear.</summary>
/// <param name="Path">The file.</param>
/// <param name="Unsealed">Whether it holds the company's client secret or the collector's headers in clear.</param>
/// <param name="KeptForTrial">The version on trial whose rollback restores it, or null.</param>
public sealed record ServerBackup(string Path, bool Unsealed, string? KeptForTrial);

/// <summary>
/// The backups of the database a server keeps in its data root (<c>backups/</c>, the copies taken before each update). A
/// version that seals the secrets the server uses itself takes copies without one in clear; a copy an older version took
/// may hold the company's client secret and the collector's headers in clear, which the server says at its start and
/// <c>altaserver backups --delete-unsealed</c> deletes, never the copy a version on trial rolls back to.
/// </summary>
public static class ServerBackups
{
    /// <summary>Lists the backups of a data root.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <returns>The backups, the newest first.</returns>
    public static IReadOnlyList<ServerBackup> List(ServerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.BackupsRoot)) return [];
        UpdateState? state = null;
        try
        {
            state = new UpdateVersionStore(paths.VersionsRoot).Read();
        }
        catch (UpdateException)
        {
            // Versions that do not read keep every backup: none is said to be the trial's.
        }

        string? Trial(string path)
        {
            foreach (var trial in new[] { state?.Trial, state?.RollingBack?.Trial })
            {
                if (trial?.Backup is { } backup && string.Equals(Path.GetFullPath(backup), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return trial.Version;
            }

            return null;
        }

        return [.. Directory.EnumerateFiles(paths.BackupsRoot, "*.sqlite3").OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => new ServerBackup(path, HoldsSecretInClear(path), Trial(path)))];
    }

    /// <summary>Lists the backups that keep a secret of the server in clear.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <returns>Their files.</returns>
    public static IReadOnlyList<string> FindUnsealed(ServerPaths paths) => [.. List(paths).Where(static backup => backup.Unsealed).Select(static backup => backup.Path)];

    /// <summary>Deletes the backups that keep a secret in clear, but the one a version on trial rolls back to.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <returns>The files deleted, and those kept for a trial.</returns>
    /// <exception cref="IOException">A backup could not be deleted.</exception>
    public static (IReadOnlyList<string> Deleted, IReadOnlyList<ServerBackup> Kept) DeleteUnsealed(ServerPaths paths)
    {
        var deleted = new List<string>();
        var kept = new List<ServerBackup>();
        foreach (var backup in List(paths).Where(static backup => backup.Unsealed))
        {
            if (backup.KeptForTrial is not null)
            {
                kept.Add(backup);
                continue;
            }

            File.Delete(backup.Path);
            deleted.Add(backup.Path);
        }

        return (deleted, kept);
    }

    // A copy whose columns of before the sealing (company_client_secret, server_headers) hold a value. A file that does not read
    // as a database of the server holds nothing the server knows of.
    private static bool HoldsSecretInClear(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return Count(connection, "SELECT COUNT(*) FROM sign_in_settings WHERE company_client_secret IS NOT NULL") > 0
                || Count(connection, "SELECT COUNT(*) FROM telemetry_settings WHERE server_headers IS NOT NULL") > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            // A version before that table (or that column) kept nothing there.
            return 0;
        }
    }
}
