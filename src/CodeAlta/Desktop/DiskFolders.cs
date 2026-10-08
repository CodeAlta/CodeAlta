using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Desktop;

/// <summary>
/// A folder of the disk that is no project, on which the code editor opens for a file that a link names.
/// </summary>
/// <param name="Id">The id the page names the folder with: <c>folder:&lt;key&gt;</c>.</param>
/// <param name="Root">The full path of the folder.</param>
/// <param name="Name">The name its tab shows: the name of the folder, or of the one file the tab has.</param>
internal readonly record struct DiskFolder(string Id, string Root, string Name);

/// <summary>
/// The folders of the disk that the host opened the code editor on, where a request of the page names a project.
/// </summary>
/// <remarks>
/// <para>
/// The page names an id and never a path, as it does for the folder of a plugin or of a skill: an id only names
/// a folder that this host gave it for a link that was followed, or for a file of the settings. An id of a project
/// has no colon, so the id of a project is never read as one of these. The folders are remembered while the host
/// runs.
/// </para>
/// <para>
/// A folder can be given for one of its files alone (<see cref="GiveFile"/>): the id then names that file and
/// nothing else of the folder, which is how a configuration file of <c>~/.alta</c> is edited without the rest of
/// that folder being reachable. A folder can also be given to be only read. The id says which: it starts with
/// <see cref="FilePrefix"/> or <see cref="ViewPrefix"/>.
/// </para>
/// </remarks>
internal sealed class DiskFolders
{
    /// <summary>What every id of a folder of the disk starts with.</summary>
    internal const string Prefix = "folder:";

    /// <summary>What the id of a folder that is given for one file starts with.</summary>
    internal const string FilePrefix = Prefix + "file:";

    /// <summary>What the id of a folder that is only read starts with.</summary>
    internal const string ViewPrefix = Prefix + "view:";

    /// <summary>The folders remembered: the one that was given first is forgotten for the next.</summary>
    internal const int MaximumFolders = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _roots = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    // The one file an id names, when the folder was given for that file alone.
    private readonly record struct Entry(string Root, string? File);

    /// <summary>Whether an id is the one of a folder of the disk, known or not.</summary>
    internal static bool IsId(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Whether an id names a folder in which nothing is created, renamed or removed: one that is only read, or one given for a file.</summary>
    internal static bool IsFixed(string? id)
        => id is not null && (id.StartsWith(FilePrefix, StringComparison.Ordinal) || id.StartsWith(ViewPrefix, StringComparison.Ordinal));

    /// <summary>Whether an id names a folder that was given for one file, known or not.</summary>
    internal static bool IsFile(string? id) => id is not null && id.StartsWith(FilePrefix, StringComparison.Ordinal);

    /// <summary>Whether an id names a folder whose files are only read.</summary>
    internal static bool IsReadOnly(string? id) => id is not null && id.StartsWith(ViewPrefix, StringComparison.Ordinal);

    /// <summary>Gives a folder its id: the same one every time the same folder is given.</summary>
    /// <param name="directory">The full path of a folder.</param>
    /// <param name="readOnly">Whether the folder is only read.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is not a full path.</exception>
    internal DiskFolder Give(string directory, bool readOnly = false)
    {
        var root = Normalize(directory, nameof(directory));
        var id = Remember((readOnly ? ViewPrefix : Prefix) + Key(root), new(root, null));
        var name = Path.GetFileName(root);
        return new(id, root, name.Length > 0 ? name : root);
    }

    /// <summary>
    /// Gives the folder of a file an id that names that file alone: the same one every time the same file is given.
    /// </summary>
    /// <param name="file">The full path of a file.</param>
    /// <exception cref="ArgumentException"><paramref name="file"/> is not the full path of a file in a folder.</exception>
    internal DiskFolder GiveFile(string file)
    {
        var full = Normalize(file, nameof(file));
        var name = Path.GetFileName(full);
        if (name.Length == 0 || Path.GetDirectoryName(full) is not { Length: > 0 } root) throw new ArgumentException("The file must be in a folder.", nameof(file));
        return new(Remember(FilePrefix + Key(full), new(root, name)), root, name);
    }

    /// <summary>
    /// Returns <c>ok</c> with the folder an id names, <c>unknown_project</c> for an id this host did not give,
    /// or <c>project_unavailable</c> when the folder is gone.
    /// </summary>
    internal (string Status, string? Root) Resolve(string? id)
    {
        Entry entry;
        lock (_gate)
        {
            if (id is null || !_roots.TryGetValue(id, out entry)) return ("unknown_project", null);
        }

        return Directory.Exists(entry.Root) ? ("ok", entry.Root) : ("project_unavailable", null);
    }

    /// <summary>The name of the one file an id names; null for an id that names a whole folder, or that is not known.</summary>
    internal string? OnlyFile(string? id)
    {
        lock (_gate) return id is not null && _roots.TryGetValue(id, out var entry) ? entry.File : null;
    }

    private static string Normalize(string path, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameter);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The path must be a full path.", parameter);
        var full = Path.GetFullPath(path);
        // The root of a drive keeps its separator; any other folder is named without one.
        return Path.GetPathRoot(full) is { } drive && full.Length > drive.Length ? Path.TrimEndingDirectorySeparator(full) : full;
    }

    private static string Key(string path)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)))[..24];

    private string Remember(string id, Entry entry)
    {
        lock (_gate)
        {
            if (!_roots.ContainsKey(id))
            {
                if (_order.Count >= MaximumFolders) _roots.Remove(_order.Dequeue());
                _order.Enqueue(id);
            }

            _roots[id] = entry;
        }

        return id;
    }
}
