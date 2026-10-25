using System.Diagnostics;
using System.Globalization;

namespace CodeAlta.Catalog;

/// <summary>
/// An application database that belongs to one process and is thrown away with it: a headless process that must not
/// write the database of the state root it shares (CodeAlta Desktop may hold it) still needs one, because the host
/// projects the journals into a database. It lives in a folder of its own under the temporary folder, makes no copy,
/// and is deleted when it is disposed. The folders of processes that ended without disposing theirs are swept by the
/// next one.
/// </summary>
/// <remarks>
/// A database that starts empty is filled from the journals as they are read, and a session is found by its identifier
/// without the list of every session being built (see <c>doc/runtime.md</c>, "Journals are the source of truth").
/// </remarks>
public sealed class PrivateApplicationDatabase : IAsyncDisposable
{
    /// <summary>The name of the folder, under the temporary folder, that holds the private databases.</summary>
    public const string DefaultFolderName = "codealta-cli";

    private const string OwnerFileName = "owner.lock";
    private const string DatabaseFileName = "alta.sqlite3";

    private readonly FileStream _owner;
    private int _disposed;

    private PrivateApplicationDatabase(string directory, FileStream owner, ApplicationDatabase database)
    {
        Directory = directory;
        _owner = owner;
        Database = database;
    }

    /// <summary>Gets the folder of this database, which is deleted with it.</summary>
    public string Directory { get; }

    /// <summary>Gets the database, for <c>CodeAltaHostOptions.ApplicationDatabase</c>; it is disposed with this instance.</summary>
    public ApplicationDatabase Database { get; }

    /// <summary>
    /// Creates a private database in a new folder <c>&lt;pid&gt;-&lt;guid&gt;</c> of <paramref name="root"/>, after
    /// sweeping the folders of processes that are gone (<see cref="SweepLeftovers"/>).
    /// </summary>
    /// <param name="root">The folder of the private databases; null for <see cref="DefaultFolderName"/> under the temporary folder.</param>
    /// <returns>The database, not yet opened.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not an absolute path.</exception>
    /// <exception cref="IOException">The folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be created.</exception>
    public static PrivateApplicationDatabase Create(string? root = null)
    {
        root = ResolveRoot(root);
        SweepLeftovers(root);
        var directory = Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{Guid.NewGuid():N}"));
        System.IO.Directory.CreateDirectory(directory);
        FileStream? owner = null;
        try
        {
            // Held open, unshared, for the life of the database: a sweep of another process sees the folder is in use.
            owner = new FileStream(Path.Combine(directory, OwnerFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var database = new ApplicationDatabase(new ApplicationDatabaseOptions
            {
                DatabasePath = Path.Combine(directory, DatabaseFileName),
                BackupDirectory = Path.Combine(directory, "backups"),
                // A database that is thrown away is never copied: the host starts no upkeep for it, and a copy asked
                // for is never due.
                BackupInterval = TimeSpan.MaxValue,
            });
            return new PrivateApplicationDatabase(directory, owner, database);
        }
        catch
        {
            owner?.Dispose();
            TryDelete(directory);
            throw;
        }
    }

    /// <summary>
    /// Deletes the folders of private databases whose process is gone: a folder whose owner file nobody holds. A folder in
    /// use, or that cannot be deleted now, is left for a later sweep.
    /// </summary>
    /// <param name="root">The folder of the private databases; null for <see cref="DefaultFolderName"/> under the temporary folder.</param>
    /// <returns>How many folders were deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not an absolute path.</exception>
    public static int SweepLeftovers(string? root = null)
    {
        root = ResolveRoot(root);
        if (!System.IO.Directory.Exists(root)) return 0;
        var deleted = 0;
        IEnumerable<string> directories;
        try
        {
            directories = System.IO.Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0 || !int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || !Guid.TryParseExact(name.AsSpan(dash + 1), "N", out _))
            {
                // Not a folder of a private database.
                continue;
            }

            if (processId == Environment.ProcessId || IsHeld(directory, processId)) continue;
            if (TryDelete(directory)) deleted++;
        }

        return deleted;
    }

    /// <summary>Disposes the database and deletes its folder; a folder that cannot be deleted now is left for a later sweep.</summary>
    /// <returns>A task that completes when the database is closed and its folder deleted.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Database.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _owner.DisposeAsync().ConfigureAwait(false);
            // The database opens no pooled connection; a file that is still open is left to a later sweep.
            TryDelete(Directory);
        }
    }

    private static string ResolveRoot(string? root)
    {
        if (root is null) return Path.Combine(Path.GetTempPath(), DefaultFolderName);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("The folder of the private databases is an absolute path.", nameof(root));
        return Path.GetFullPath(root);
    }

    // Whether the process that created a folder still holds it: its process runs, and its owner file cannot be opened.
    private static bool IsHeld(string directory, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // No such process.
            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process cannot be inspected: the owner file decides.
        }

        try
        {
            // A process id that was given to another process: the owner file is free.
            using var probe = new FileStream(Path.Combine(directory, OwnerFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
