using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Desktop;

/// <summary>
/// A folder of the disk that is no project, on which the code editor opens for a file that a link names.
/// </summary>
/// <param name="Id">The id the page names the folder with: <c>folder:&lt;key&gt;</c>.</param>
/// <param name="Root">The full path of the folder.</param>
/// <param name="Name">The name its tab shows: the name of the folder.</param>
internal readonly record struct DiskFolder(string Id, string Root, string Name);

/// <summary>
/// The folders of the disk that the host opened the code editor on, where a request of the page names a project.
/// </summary>
/// <remarks>
/// The page names an id and never a path, as it does for the folder of a plugin or of a skill: an id only names
/// a folder that this host gave it for a link that was followed. An id of a project has no colon, so the id of a
/// project is never read as one of these. The folders are remembered while the host runs.
/// </remarks>
internal sealed class DiskFolders
{
    /// <summary>What every id of a folder of the disk starts with.</summary>
    internal const string Prefix = "folder:";

    /// <summary>The folders remembered: the one that was given first is forgotten for the next.</summary>
    internal const int MaximumFolders = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Whether an id is the one of a folder of the disk, known or not.</summary>
    internal static bool IsId(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Gives a folder its id: the same one every time the same folder is given.</summary>
    /// <param name="directory">The full path of a folder.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is not a full path.</exception>
    internal DiskFolder Give(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The folder must be a full path.", nameof(directory));
        var root = Path.GetFullPath(directory);
        // The root of a drive keeps its separator; any other folder is named without one.
        if (Path.GetPathRoot(root) is { } drive && root.Length > drive.Length) root = Path.TrimEndingDirectorySeparator(root);
        var key = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var id = Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        lock (_gate)
        {
            if (!_roots.ContainsKey(id))
            {
                if (_order.Count >= MaximumFolders) _roots.Remove(_order.Dequeue());
                _order.Enqueue(id);
            }

            _roots[id] = root;
        }

        var name = Path.GetFileName(root);
        return new(id, root, name.Length > 0 ? name : root);
    }

    /// <summary>
    /// Returns <c>ok</c> with the folder an id names, <c>unknown_project</c> for an id this host did not give,
    /// or <c>project_unavailable</c> when the folder is gone.
    /// </summary>
    internal (string Status, string? Root) Resolve(string? id)
    {
        string? root;
        lock (_gate)
        {
            if (id is null || !_roots.TryGetValue(id, out root)) return ("unknown_project", null);
        }

        return Directory.Exists(root) ? ("ok", root) : ("project_unavailable", null);
    }
}
