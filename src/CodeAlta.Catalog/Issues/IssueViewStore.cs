using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog.Issues;

/// <summary>What a view says of one filter: a filter a view does not name is off.</summary>
/// <param name="Id">The id of the filter: <c>team</c>, <c>sprint</c>, or <c>field:&lt;reference name&gt;</c>.</param>
/// <param name="Active">Whether the view lists only the items of its value; an inactive filter keeps its value and its name (<c>mode = "off"</c> in the file).</param>
/// <param name="Value">Its value: the id or the label of a value, several joined with U+001F; empty or null for every item.</param>
/// <param name="Label">The name the tab shows for it, in place of the one of the tracker.</param>
/// <param name="Pick">Whether the tab offers to change the value of the active filter for a while (<c>pick = true</c>): the view keeps its value.</param>
public sealed record IssueFilterSetting(string Id, bool Active = true, string? Value = null, string? Label = null, bool Pick = false);

/// <summary>The filters of a view, in their order.</summary>
/// <param name="Filters">What is said of each filter, each id once.</param>
public sealed record IssueFilterSettings(IReadOnlyList<IssueFilterSetting> Filters)
{
    /// <summary>Gets settings that say nothing.</summary>
    public static IssueFilterSettings Empty { get; } = new([]);

    /// <summary>Gets what is said of a filter; null when nothing is.</summary>
    /// <param name="id">The id of the filter.</param>
    /// <returns>The setting, or null.</returns>
    public IssueFilterSetting? Find(string id) => Filters.FirstOrDefault(filter => string.Equals(filter.Id, id, StringComparison.Ordinal));
}

/// <summary>Whose a view of the Issues tab is.</summary>
public enum IssueViewSource
{
    /// <summary>The user's: <c>&lt;project&gt;/.alta/local/issues.toml</c>, which git ignores.</summary>
    Personal,

    /// <summary>The team's: <c>&lt;project&gt;/.alta/issues.toml</c>, committed with the repository.</summary>
    Team,
}

/// <summary>A view of the Issues tab: a named set of filters, the user's or the team's.</summary>
/// <param name="Name">The name of the view, which names it in its file.</param>
/// <param name="Source">Whose it is.</param>
/// <param name="Filters">Its filters.</param>
public sealed record IssueView(string Name, IssueViewSource Source, IssueFilterSettings Filters);

/// <summary>A name of a view is already the name of another one: a name is one view, the user's or the team's.</summary>
public sealed class IssueViewNameTakenException : InvalidOperationException
{
    /// <summary>Creates the exception for a name.</summary>
    /// <param name="name">The name that is taken.</param>
    public IssueViewNameTakenException(string name)
        : base($"A view is already named '{name}'.") => Name = name;

    /// <summary>Gets the name that is taken.</summary>
    public string Name { get; }
}

/// <summary>A file of views already holds <see cref="IssueViewStore.MaximumViews"/> views: a view added to it would not be read back.</summary>
public sealed class IssueViewLimitException : InvalidOperationException
{
    /// <summary>Creates the exception for a file.</summary>
    /// <param name="path">The file that is full.</param>
    public IssueViewLimitException(string path)
        : base($"'{path}' already holds {IssueViewStore.MaximumViews} views, the most that are read: remove one first.") => Path = path;

    /// <summary>Gets the file that is full.</summary>
    public string Path { get; }
}

/// <summary>
/// Reads and writes the views of the Issues tab of a project: the team's in <c>.alta/issues.toml</c> and the user's in
/// <c>.alta/local/issues.toml</c>, with the view the user chose. A view is <c>[&lt;service&gt;.views.&lt;name&gt;]</c> and each
/// of its filters <c>[&lt;service&gt;.views.&lt;name&gt;.&lt;filter&gt;]</c> with <c>value</c>, <c>label</c>, <c>pick</c> and
/// <c>mode = "off"</c> for a filter kept unused; a field of the tracker is written by its name (<c>"System.State"</c>) and read
/// as <c>field:System.State</c>. A view keeps its place in its file when it is saved or renamed.
/// </summary>
public sealed class IssueViewStore
{
    /// <summary>The most filters one view has.</summary>
    public const int MaximumFilters = 32;

    /// <summary>The most views one file has.</summary>
    public const int MaximumViews = 64;

    /// <summary>The longest name of a view.</summary>
    public const int MaximumName = 64;

    /// <summary>What joins the values of a filter that takes several, as the trackers join them (U+001F).</summary>
    public const string ValueSeparator = "\u001F";

    /// <summary>What the id of a filter made of a field starts with.</summary>
    public const string FieldPrefix = "field:";

    /// <summary>The longest value of a filter, each one of a filter that takes several, and the longest label, in UTF-16 units.</summary>
    public const int MaximumText = 256;

    private const string FileName = "issues.toml";

    /// <summary>Gets whether a text can be the id of a filter: letters, digits, <c>_ - . :</c>, 128 at most.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsFilterId(string? value)
        => value is { Length: > 0 and <= 128 } && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');

    /// <summary>
    /// Gets whether a text can be the value or the label of a filter: null, or one line whose values (joined with
    /// <see cref="ValueSeparator"/>) are <see cref="MaximumText"/> units at most each, as the file is read back.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsFilterText(string? value)
        => value is null || value.Length <= MaximumText * MaximumFilters && !value.Any(static ch => char.IsControl(ch) && ch != ValueSeparator[0])
            && value.Split(ValueSeparator).All(static one => one.Trim().Length <= MaximumText);

    /// <summary>Gets whether a text can be the name of a view: one line of 64 characters at most, without spaces around it.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsViewName(string? value)
        => value is { Length: > 0 and <= MaximumName } && value == value.Trim() && !value.Any(char.IsControl);

    /// <summary>Gets the file the views of a source are kept in.</summary>
    /// <param name="source">Whose views.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The path of the file.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is empty.</exception>
    public static string Locate(IssueViewSource source, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return source == IssueViewSource.Team ? System.IO.Path.Combine(projectRoot, ".alta", FileName) : System.IO.Path.Combine(projectRoot, ".alta", "local", FileName);
    }

    /// <summary>Reads the views of a tracker of a project: the user's first, then the team's, each in the order of its file.</summary>
    /// <param name="service">The service of the tracker: <c>azure_devops</c>, <c>jira</c>...</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The views; a file that is missing or cannot be read has none (<see cref="ReadProblem"/> says why).</returns>
    /// <exception cref="ArgumentException">An argument is empty or not a plain name.</exception>
    public IReadOnlyList<IssueView> ReadViews(string service, string projectRoot)
        => [.. ReadFile(IssueViewSource.Personal, service, projectRoot).Views, .. ReadFile(IssueViewSource.Team, service, projectRoot).Views];

    /// <summary>
    /// Reads the view the tab uses: the one the user chose, the user's before the team's of the same name; else the
    /// user's first view, else the team's first; null when there is none.
    /// </summary>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The view, or null.</returns>
    /// <exception cref="ArgumentException">An argument is empty or not a plain name.</exception>
    public IssueView? ReadCurrent(string service, string projectRoot)
    {
        var personal = ReadFile(IssueViewSource.Personal, service, projectRoot);
        var views = ReadViews(service, projectRoot);
        return (personal.Current is { } chosen ? views.FirstOrDefault(view => string.Equals(view.Name, chosen, StringComparison.Ordinal)) : null) ?? views.FirstOrDefault();
    }

    /// <summary>
    /// Says why views of a tracker of a project are not shown: a file of views that cannot be read or is not valid TOML
    /// (none of its views is read), or one that holds more than <see cref="MaximumViews"/> views.
    /// </summary>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>What is wrong, in words for the user; null when every view is read.</returns>
    /// <exception cref="ArgumentException">An argument is empty or not a plain name.</exception>
    public string? ReadProblem(string service, string projectRoot)
    {
        string[] problems = [.. new[] { ReadFile(IssueViewSource.Personal, service, projectRoot).Problem, ReadFile(IssueViewSource.Team, service, projectRoot).Problem }.OfType<string>()];
        return problems.Length == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>
    /// Writes the filters of a view, made when it is missing: what the view said before is replaced. A view of the other
    /// source with the same name is refused, under the lock of the file written.
    /// </summary>
    /// <param name="source">Whose view.</param>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">The name of the view.</param>
    /// <param name="filters">Its filters.</param>
    /// <exception cref="ArgumentException">An argument is empty, too long or not a plain name, or a filter is repeated.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="filters"/> is null.</exception>
    /// <exception cref="IssueViewNameTakenException">A view of the other source has the name.</exception>
    /// <exception cref="IssueViewLimitException">The view is missing and its file already holds <see cref="MaximumViews"/> views.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML, or writes the view in a form that is not edited here (an inline table, dotted keys).</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public void SaveView(IssueViewSource source, string service, string projectRoot, string name, IssueFilterSettings filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        CheckView(service, name);
        CheckFilters(filters);
        EditView(source, service, projectRoot, name, _ => filters);
    }

    /// <summary>
    /// Changes the filters of a view from those its file holds when it is written, under the lock of the file: what another
    /// writer saved meanwhile is kept, not written over. A view of the other source with the same name is refused.
    /// </summary>
    /// <param name="source">Whose view.</param>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">The name of the view.</param>
    /// <param name="edit">
    /// Gets the filters of the view in its file, null when the file has no such view, and gives the filters to write (the
    /// view is made when it is missing); null writes nothing. It is called once, while the lock is held.
    /// </param>
    /// <returns>The filters written; null when <paramref name="edit"/> gave none.</returns>
    /// <exception cref="ArgumentException">An argument is empty, too long or not a plain name, or a filter <paramref name="edit"/> gives is repeated or too long.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    /// <exception cref="IssueViewNameTakenException">A view of the other source has the name.</exception>
    /// <exception cref="IssueViewLimitException">The view is missing and its file already holds <see cref="MaximumViews"/> views.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML, or writes the view in a form that is not edited here (an inline table, dotted keys).</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public IssueFilterSettings? EditView(IssueViewSource source, string service, string projectRoot, string name, Func<IssueFilterSettings?, IssueFilterSettings?> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        CheckView(service, name);
        IssueFilterSettings? written = null;
        Edit(source, projectRoot, content =>
        {
            var file = ParseFile(content, source, service);
            var view = file.Views.FirstOrDefault(candidate => candidate.Name == name);
            if (edit(view?.Filters) is not { } filters) return content;
            CheckFilters(filters);
            if (ReadFile(source == IssueViewSource.Team ? IssueViewSource.Personal : IssueViewSource.Team, service, projectRoot).Views.Any(other => other.Name == name))
                throw new IssueViewNameTakenException(name);
            // A view past the most that are read would be written and never read back.
            if (view is null && file.Views.Count >= MaximumViews) throw new IssueViewLimitException(Locate(source, projectRoot));
            written = filters;
            return ReplaceView(content, service, name, name, filters);
        });
        return written;
    }

    /// <summary>
    /// Renames a view, keeping its filters as its file holds them under the lock; the user's choice follows it, in the
    /// same write for a view of the user and in a second write of the user's file for a view of the team.
    /// </summary>
    /// <param name="source">Whose view.</param>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">Its name.</param>
    /// <param name="newName">Its new name.</param>
    /// <returns><see langword="false"/> when the view is missing.</returns>
    /// <exception cref="ArgumentException">A name is empty, too long or not a plain name.</exception>
    /// <exception cref="IssueViewNameTakenException">A view of either source has the new name.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML, or writes the view in a form that is not edited here (an inline table, dotted keys).</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public bool RenameView(IssueViewSource source, string service, string projectRoot, string name, string newName)
    {
        CheckView(service, name);
        CheckView(service, newName);
        var found = false;
        var followed = false;
        Edit(source, projectRoot, content =>
        {
            // The filters are those of the file under the lock: a save made meanwhile is renamed with the view.
            var file = ParseFile(content, source, service);
            if (file.Views.FirstOrDefault(view => view.Name == name) is not { } view) return content;
            found = true;
            if (name == newName) return content;
            // The name is checked under the lock of the file written, against both files.
            if (ReadViews(service, projectRoot).Any(other => other.Name == newName)) throw new IssueViewNameTakenException(newName);
            followed = (source == IssueViewSource.Personal ? file.Current : ReadFile(IssueViewSource.Personal, service, projectRoot).Current) == name;
            content = ReplaceView(content, service, name, newName, view.Filters);
            return followed && source == IssueViewSource.Personal ? ConfigTableSyntax.SetText(content, [service], "current", newName) : content;
        });
        if (followed && source == IssueViewSource.Team) SetCurrent(service, projectRoot, newName);
        return found;
    }

    /// <summary>Removes a view and its filters.</summary>
    /// <param name="source">Whose view.</param>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">Its name.</param>
    /// <returns><see langword="false"/> when the view is missing.</returns>
    /// <exception cref="ArgumentException">An argument is empty, too long or not a plain name.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML, or writes the view in a form that is not edited here (an inline table, dotted keys).</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public bool RemoveView(IssueViewSource source, string service, string projectRoot, string name)
    {
        CheckView(service, name);
        var found = false;
        Edit(source, projectRoot, content =>
        {
            if (ParseFile(content, source, service).Views.All(view => view.Name != name)) return content;
            found = true;
            var removed = ConfigTableSyntax.RemoveTables(content, [service, "views", name]);
            return HasView(removed, service, name) ? throw UneditedView(service, name) : removed;
        });
        if (!found) return false;
        // The choice of a view that is gone is forgotten: the tab shows the first view.
        if (ReadFile(IssueViewSource.Personal, service, projectRoot).Current == name && ReadViews(service, projectRoot).All(view => view.Name != name)) SetCurrent(service, projectRoot, null);
        return true;
    }

    /// <summary>Keeps the view the user chose, in the user's file; null forgets the choice.</summary>
    /// <param name="service">The service of the tracker.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">The name of the view.</param>
    /// <exception cref="ArgumentException">An argument is empty, too long or not a plain name.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML.</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public void SetCurrent(string service, string projectRoot, string? name)
    {
        if (name is not null) CheckView(service, name);
        else if (!IsKey(service)) throw new ArgumentException("The service is empty or not a plain name.", nameof(service));
        // The table of the service exists through its views alone: its header is written before its key.
        Edit(IssueViewSource.Personal, projectRoot, content => ConfigTableSyntax.SetText(name is null ? content : ConfigTableSyntax.EnsureTable(content, [service]), [service], "current", name));
    }

    // The views of one file, the view the user chose when it is the user's file, and why views of it are not shown.
    private sealed record ViewFile(IReadOnlyList<IssueView> Views, string? Current, string? Problem = null);

    // A file that is missing has no views; one that cannot be read or does not parse has none either, and says why.
    private static ViewFile ReadFile(IssueViewSource source, string service, string projectRoot)
    {
        if (!IsKey(service)) throw new ArgumentException("The service is empty or not a plain name.", nameof(service));
        var path = Locate(source, projectRoot);
        if (!File.Exists(path)) return new([], null);
        try
        {
            var file = ParseFile(SharedTextFile.ReadAllText(path), source, service);
            return file.Problem is null ? file : file with { Problem = $"'{path}' {file.Problem}" };
        }
        catch (InvalidDataException exception)
        {
            var reason = exception.InnerException?.Message.Split('\n', 2)[0].Trim() is { Length: > 0 and <= 200 } brief ? $" ({brief})" : string.Empty;
            return new([], null, $"'{path}' is not valid TOML{reason}: none of its views is shown until it is fixed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new([], null, $"'{path}' cannot be read: none of its views is shown.");
        }
    }

    // The views of the text of a file, the problem a part of the file says without its path.
    private static ViewFile ParseFile(string content, IssueViewSource source, string service)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable();
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The views are not valid TOML.", exception);
        }

        if (!root.TryGetValue(service, out var found) || found is not TomlTable table) return new([], null);
        var current = source == IssueViewSource.Personal && table.TryGetValue("current", out var chosen) && chosen is string name && IsViewName(name) ? name : null;
        var views = new List<IssueView>();
        string? problem = null;
        if (table.TryGetValue("views", out var listed) && listed is TomlTable viewTables)
        {
            foreach (var (viewName, value) in viewTables)
            {
                if (!IsViewName(viewName) || value is not TomlTable filters) continue;
                if (views.Count >= MaximumViews)
                {
                    problem = $"has more than {MaximumViews} views: those after the first {MaximumViews} are not shown.";
                    break;
                }

                views.Add(new(viewName, source, Parse(filters)));
            }
        }

        return new(views, current, problem);
    }

    // Whether the text still defines a view, in whatever form: a removal of its tables leaves an inline one or dotted keys.
    // A text that does not parse is refused when it is written.
    private static bool HasView(string content, string service, string name)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable();
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            return false;
        }

        return root.TryGetValue(service, out var found) && found is TomlTable table && table.TryGetValue("views", out var listed) && listed is TomlTable views && views.ContainsKey(name);
    }

    private static InvalidDataException UneditedView(string service, string name)
        => new($"The view '{name}' is written in a form CodeAlta does not edit (an inline table or dotted keys): change it by hand, "
            + $"or write each of its filters as a table, [{string.Join('.', new[] { service, "views", name }.Select(ConfigTableSyntax.KeyText))}.<filter>].");

    private static void CheckFilters(IssueFilterSettings filters)
    {
        if (filters.Filters.Count > MaximumFilters || !filters.Filters.All(static filter => IsFilterId(filter.Id) && IsFilterText(filter.Value) && IsFilterText(filter.Label)
            && filter.Label?.Contains(ValueSeparator, StringComparison.Ordinal) != true)
            || filters.Filters.Select(static filter => filter.Id).Distinct(StringComparer.Ordinal).Count() != filters.Filters.Count)
            throw new ArgumentException("A filter id is empty, repeated, too long or not a plain name, or a value is too long.", nameof(filters));
    }

    // What the file says that is not of the expected shape is left out, not refused: the Issues tab still opens.
    private static IssueFilterSettings Parse(TomlTable table)
    {
        var settings = new List<IssueFilterSetting>();
        foreach (var (key, value) in table)
        {
            if (settings.Count >= MaximumFilters) break;
            var id = IdOf(key);
            if (!IsFilterId(id) || value is not TomlTable filter || settings.Any(known => known.Id == id)) continue;
            // Only "off" turns a filter off; a table without a mode, or with one written by hand, is active.
            var active = !string.Equals(Read(filter, "mode"), "off", StringComparison.OrdinalIgnoreCase);
            var given = filter.TryGetValue("value", out var written) && written is TomlArray list
                ? string.Join(ValueSeparator, list.OfType<string>().Select(static one => one.Trim()).Where(static one => one.Length is > 0 and <= MaximumText))
                : Read(filter, "value")?.Trim();
            settings.Add(new(id, active, given, Read(filter, "label")?.Trim(), filter.TryGetValue("pick", out var pick) && pick is true));
        }

        return new(settings);
    }

    // The text after a view is written: its tables are replaced where the first of them was, else added at the end.
    private static string ReplaceView(string content, string service, string name, string newName, IssueFilterSettings filters)
    {
        string[] old = [service, "views", name];
        var at = ConfigTableSyntax.TableStart(content, old);
        content = ConfigTableSyntax.RemoveTables(content, old);
        // A view its tables do not hold all would be kept beside the one written, or answered as renamed while it stays.
        if (HasView(content, service, name)) throw UneditedView(service, name);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var tables = ViewText([service, "views", newName], filters).Replace("\n", newline, StringComparison.Ordinal);
        if (at is not { } start) return content + (content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline) + tables;
        var rest = content[start..];
        return content[..start] + tables + (rest.Length == 0 ? string.Empty : newline) + rest;
    }

    // The tables of a view alone: its header, then one table per filter.
    private static string ViewText(string[] view, IssueFilterSettings filters)
    {
        var content = ConfigTableSyntax.EnsureTable(string.Empty, view);
        foreach (var filter in filters.Filters)
        {
            string[] table = [.. view, KeyOf(filter.Id)];
            // The table names the filter even when it says nothing more: an active filter of every item.
            content = ConfigTableSyntax.EnsureTable(content, table);
            if (!filter.Active) content = ConfigTableSyntax.SetText(content, table, "mode", "off");
            // Several values are written as a list: value = ["New", "Ready"].
            content = filter.Value is { } value && value.Contains(ValueSeparator, StringComparison.Ordinal)
                ? ConfigTableSyntax.SetList(content, table, "value", value.Split(ValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                : ConfigTableSyntax.SetText(content, table, "value", filter.Value);
            content = ConfigTableSyntax.SetText(content, table, "label", filter.Label);
            if (filter.Pick) content = ConfigTableSyntax.SetValue(content, table, "pick", true);
        }

        return content;
    }

    // A field of the tracker is written by its name, which has a dot (System.State); the filters of the tracker's own have none.
    private static string IdOf(string key) => key.Contains('.', StringComparison.Ordinal) && !key.StartsWith(FieldPrefix, StringComparison.Ordinal) ? FieldPrefix + key : key;

    private static string KeyOf(string id) => id.StartsWith(FieldPrefix, StringComparison.Ordinal) && id.AsSpan(FieldPrefix.Length).Contains('.') ? id[FieldPrefix.Length..] : id;

    // Edits a file of views under the lock of the file; the user's folder is kept out of git.
    private static void Edit(IssueViewSource source, string projectRoot, Func<string, string> edit)
    {
        var path = Locate(source, projectRoot);
        var folder = System.IO.Path.GetDirectoryName(path)!;
        using var writing = ConfigFileLock.Acquire(path);
        var content = File.Exists(path) ? SharedTextFile.ReadAllText(path) : string.Empty;
        var updated = edit(content);
        if (string.Equals(updated, content, StringComparison.Ordinal)) return;
        try
        {
            _ = TomlSerializer.Deserialize<TomlTable>(updated);
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The views of '{path}' would not be valid TOML.", exception);
        }

        Directory.CreateDirectory(folder);
        if (source == IssueViewSource.Personal)
        {
            var ignore = System.IO.Path.Combine(folder, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
        }

        File.WriteAllText(path, updated);
    }

    private static void CheckView(string service, string name)
    {
        if (!IsKey(service)) throw new ArgumentException("The service is empty or not a plain name.", nameof(service));
        if (!IsViewName(name)) throw new ArgumentException("The name of a view is one line of 64 characters at most.", nameof(name));
    }

    private static string? Read(TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is string text && text.Length <= MaximumText ? text : null;

    private static bool IsKey(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
}
