using System.Text;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>
/// The project a session counts under, by the rule CodeAlta lists sessions with (<c>ProjectCatalog.ResolveSessionProject</c>): the
/// project its journal records when the catalog has that identifier; otherwise the project of the catalog in the folder of the
/// session, which adopted it (a session <c>altacli</c> wrote in a folder that was no project then, a project removed and added
/// again); never another project of the catalog. It is read with each question from the catalog of now, so adding a project
/// moves the numbers of the sessions of its folder at once, without reading a journal again.
/// </summary>
internal sealed class SessionProjects
{
    /// <summary>Gets the rule when no session is adopted: every session counts under the project it records.</summary>
    public static SessionProjects None { get; } = new([]);

    // The adopted sessions by the key of their folder: the project of the folder, and the projects recorded there that it adopts.
    private readonly IReadOnlyList<(string FolderKey, string ProjectId, string[] Recorded)> _adoptions;
    private readonly Dictionary<(string FolderKey, string Recorded), string> _projects = [];

    private SessionProjects(IReadOnlyList<(string FolderKey, string ProjectId, string[] Recorded)> adoptions)
    {
        _adoptions = adoptions;
        foreach (var (folderKey, projectId, recorded) in adoptions)
        {
            foreach (var reference in recorded)
            {
                _projects[(folderKey, reference)] = projectId;
            }
        }
    }

    /// <summary>Gets a value indicating whether no session is adopted.</summary>
    public bool IsEmpty => _adoptions.Count == 0;

    /// <summary>Finds the sessions the projects of the catalog adopted, from the projects and the folders the sessions record.</summary>
    /// <param name="sql">A reader of the store.</param>
    /// <param name="prefix">The prefix of the tables of the store.</param>
    /// <param name="projects">The projects of the catalog.</param>
    /// <returns>The rule.</returns>
    public static SessionProjects Read(SqlSession sql, string prefix, IReadOnlyList<ProjectInfo> projects)
    {
        // The first project of a folder, as the rule of the catalog takes it.
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (SessionFolders.Key(project.Path) is { } key)
            {
                folders.TryAdd(key, project.Id);
            }
        }

        if (folders.Count == 0)
        {
            return None;
        }

        // An identifier the catalog has in another case is the identifier of another project all the same.
        var known = projects.Select(static project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string FolderKey, string Recorded)>();
        foreach (var chunk in folders.Keys.Chunk(400))
        {
            pairs.AddRange(sql.Query(
                $"SELECT DISTINCT folder_key, project_ref FROM {prefix}session WHERE project_ref IS NOT NULL AND project_ref <> '' AND folder_key IN ({string.Join(", ", chunk.Select(static (_, index) => "@p" + index))})",
                static reader => (reader.GetString(0), reader.GetString(1)),
                [.. chunk.Cast<object?>()]));
        }

        var adoptions = pairs
            .Where(pair => !known.Contains(pair.Recorded))
            .GroupBy(static pair => pair.FolderKey, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, folders[group.Key], group.Select(static pair => pair.Recorded).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        return adoptions.Length == 0 ? None : new SessionProjects(adoptions);
    }

    /// <summary>Gets the project a session counts under.</summary>
    /// <param name="recorded">The project the session records.</param>
    /// <param name="folderKey">The key of the folder of the session.</param>
    /// <returns>The project; <paramref name="recorded"/> when the session is not adopted.</returns>
    public string? Resolve(string? recorded, string? folderKey)
        => recorded is not null && folderKey is not null && _projects.TryGetValue((folderKey, recorded), out var project) ? project : recorded;

    /// <summary>Gets the SQL expression of the project a session counts under.</summary>
    /// <param name="column">The column of the recorded project, as <c>s.project_ref</c>; its table also has <c>folder_key</c>.</param>
    /// <param name="argument">Gives the name of a parameter for a value.</param>
    /// <returns><paramref name="column"/> itself when no session is adopted.</returns>
    public string Expression(string column, Func<string, string> argument)
    {
        if (IsEmpty)
        {
            return column;
        }

        var folderColumn = column[..(column.LastIndexOf('.') + 1)] + "folder_key";
        var builder = new StringBuilder("(CASE");
        foreach (var (folderKey, projectId, recorded) in _adoptions)
        {
            builder.Append(" WHEN ").Append(folderColumn).Append(" = ").Append(argument(folderKey))
                .Append(" AND ").Append(column).Append(" IN (").AppendJoin(", ", recorded.Select(argument)).Append(')')
                .Append(" THEN ").Append(argument(projectId));
        }

        return builder.Append(" ELSE ").Append(column).Append(" END)").ToString();
    }
}
