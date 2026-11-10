using System.Collections.Immutable;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog.Issues;

/// <summary>A type of item, or a label of it, and the text <c>{type}</c> stands for in the name of its branch.</summary>
/// <param name="Name">The type or the label as the tracker names it, such as <c>Bug</c>; <c>*</c> for every other item.</param>
/// <param name="Value">The text of <c>{type}</c>, such as <c>fix</c>.</param>
public sealed record IssueBranchType(string Name, string Value);

/// <summary>
/// How the branch of a new worktree started on an issue is named: a template whose placeholders are
/// <c>{type}</c>, <c>{id}</c> and <c>{title}</c>, such as <c>{type}/{id}-{title}</c>, the longest the words of the title
/// make the name, and what <c>{type}</c> is for each type of item.
/// </summary>
/// <param name="Template">The template.</param>
/// <param name="TitleLength">The longest <c>{title}</c> is, cut at a whole word.</param>
/// <param name="Types">What <c>{type}</c> is for a type or a label of an item, in the order they are tried; <c>*</c> for the others.</param>
public sealed record IssueBranchScheme(string Template, int TitleLength, IReadOnlyList<IssueBranchType> Types)
{
    /// <summary>The template when no file names one.</summary>
    public const string DefaultTemplate = "{type}/{id}-{title}";

    /// <summary>The longest <c>{title}</c> when no file says it.</summary>
    public const int DefaultTitleLength = 40;

    /// <summary>The longest <c>{title}</c> a file can ask for.</summary>
    public const int MaximumTitleLength = 120;

    /// <summary>The longest template.</summary>
    public const int MaximumTemplateLength = 200;

    /// <summary>The name of the type of the items no other type or label names.</summary>
    public const string OtherTypes = "*";

    /// <summary>Gets the placeholders a template can hold, without their braces.</summary>
    public static ImmutableArray<string> Placeholders { get; } = ["type", "id", "title"];

    /// <summary>Gets the scheme when no file says one: <c>{type}/{id}-{title}</c>, a bug as <c>fix</c> and the rest as <c>feature</c>.</summary>
    public static IssueBranchScheme Default { get; } = new(DefaultTemplate, DefaultTitleLength,
        [new("Bug", "fix"), new("Bugfix", "fix"), new("Defect", "fix"), new("Fix", "fix"), new(OtherTypes, "feature")]);

    /// <summary>
    /// Says what is wrong with a template: braces that do not close, or a placeholder that is not one of
    /// <see cref="Placeholders"/>.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>The problem, or null when the template is right.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    public static string? TemplateProblem(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Trim().Length == 0) return "The template is empty.";
        if (template.Length > MaximumTemplateLength) return $"The template is longer than {MaximumTemplateLength} characters.";
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '}') return "The template has a '}' that no '{' opens.";
            if (template[index] != '{') continue;
            var end = template.IndexOf('}', index + 1);
            if (end < 0) return "The template has a '{' that no '}' closes.";
            var name = template[(index + 1)..end];
            if (!Placeholders.Contains(name, StringComparer.Ordinal))
                return $"The template has the placeholder {{{name}}}; it can hold {string.Join(", ", Placeholders.Select(static known => "{" + known + "}"))}.";
            index = end;
        }

        return null;
    }
}

/// <summary>The scheme of the branches of a project, and what is wrong in the files that say it.</summary>
/// <param name="Scheme">The scheme: what the files say that is right, the default for the rest.</param>
/// <param name="Problem">What is wrong in a file, with its path; null when nothing is.</param>
/// <param name="Configured">Whether a file of the project has a <c>[branch]</c> table: the project has a convention of its own.</param>
public sealed record IssueBranchSchemeReading(IssueBranchScheme Scheme, string? Problem, bool Configured = false);

/// <summary>What the form of the scheme of one file shows: the scheme to change, and whether the file has its own.</summary>
/// <param name="Scheme">
/// The scheme of the file when it has a <c>[branch]</c> table; else the one that applies without it: the default for
/// the team's file, the team's for the user's.
/// </param>
/// <param name="Own">Whether the file has a <c>[branch]</c> table.</param>
/// <param name="Problem">What is wrong in the files read, with their path; null when nothing is.</param>
public sealed record IssueBranchSchemeForm(IssueBranchScheme Scheme, bool Own, string? Problem);

/// <summary>
/// Reads the scheme of the branches of a project from the <c>[branch]</c> table of the files of the Issues tab:
/// the team's <c>.alta/issues.toml</c>, committed with the repository, and the user's <c>.alta/local/issues.toml</c>,
/// which git ignores. Each of <c>template</c>, <c>title_length</c> and <c>[branch.types]</c> is the user's when the
/// user's file has it, else the team's, else the default; <c>[branch.types]</c> is taken whole, not merged.
/// </summary>
public static class IssueBranchSchemes
{
    /// <summary>Reads the scheme of the branches of a project.</summary>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The scheme, and what is wrong in its files; a file that is missing says nothing.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is empty.</exception>
    public static IssueBranchSchemeReading Read(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var problems = new List<string>();
        // The team's first: the user's file says the last word.
        var scheme = Layer(IssueViewSource.Team, projectRoot, IssueBranchScheme.Default, problems, out var team);
        scheme = Layer(IssueViewSource.Personal, projectRoot, scheme, problems, out var mine);
        return new(scheme, Join(problems), team || mine);
    }

    /// <summary>
    /// Reads what the form of the scheme of one file shows: the team's file over the default, or the user's file over
    /// the team's, and whether the file has a <c>[branch]</c> table of its own.
    /// </summary>
    /// <param name="source">Whose file: the team's or the user's.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The scheme to change, whether the file has its own, and what is wrong in the files read.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is empty.</exception>
    public static IssueBranchSchemeForm ReadForm(IssueViewSource source, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var problems = new List<string>();
        var scheme = Layer(IssueViewSource.Team, projectRoot, IssueBranchScheme.Default, problems, out var own);
        if (source == IssueViewSource.Personal) scheme = Layer(IssueViewSource.Personal, projectRoot, scheme, problems, out own);
        return new(scheme, own, Join(problems));
    }

    /// <summary>
    /// Writes the whole <c>[branch]</c> table of one file, or removes it, and leaves the rest of the file (the views) as it is.
    /// The user's folder is kept out of git, as for the views.
    /// </summary>
    /// <param name="source">Whose file: the team's or the user's.</param>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="scheme">The scheme to write; null removes the table, and the scheme of the files below applies again.</param>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is empty, or the scheme is not one that can be read back (see <see cref="SchemeProblem"/>).</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML, or writes its scheme in a form that is not changed here (inline or with dotted keys).</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public static void Save(IssueViewSource source, string projectRoot, IssueBranchScheme? scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (scheme is not null && SchemeProblem(scheme) is { } problem) throw new ArgumentException(problem, nameof(scheme));
        var path = IssueViewStore.Locate(source, projectRoot);
        IssueViewStore.Edit(source, projectRoot, content =>
        {
            var removed = ConfigTableSyntax.RemoveTables(content, BranchTable);
            // The blank line that separated the table goes with it: saving again does not add one each time.
            if (!string.Equals(removed, content, StringComparison.Ordinal)) removed = WithOneFinalNewline(removed);
            // Only [branch] headers are taken out: a scheme written inline (branch = { ... }) or with dotted keys
            // (branch.template = ...) would stay and still apply, so it is refused rather than said to be gone.
            if (HasBranch(removed)) throw new InvalidDataException($"'{path}' writes [branch] in a form the window does not change: change it in the file.");
            content = removed;
            if (scheme is null) return content;
            content = ConfigTableSyntax.SetText(content, BranchTable, "template", scheme.Template);
            content = ConfigTableSyntax.SetInteger(content, BranchTable, "title_length", scheme.TitleLength);
            content = ConfigTableSyntax.EnsureTable(content, TypesTable);
            foreach (var type in scheme.Types) content = ConfigTableSyntax.SetText(content, TypesTable, type.Name.Trim(), type.Value.Trim());
            return content;
        });
    }

    /// <summary>Says what keeps a scheme from being written: a wrong template or length, a type without a name, twice, or with a text that is no part of a branch name.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>The problem, or null when the scheme can be written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is null.</exception>
    public static string? SchemeProblem(IssueBranchScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        if (IssueBranchScheme.TemplateProblem(scheme.Template) is { } template) return template;
        if (scheme.TitleLength is < 1 or > IssueBranchScheme.MaximumTitleLength) return $"The length of the title is not a number from 1 to {IssueBranchScheme.MaximumTitleLength}.";
        if (scheme.Types.Count > MaximumTypes) return $"There are more than {MaximumTypes} types.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in scheme.Types)
        {
            if (type.Name.Trim().Length == 0 || type.Name.Length > 64 || type.Name.Any(char.IsControl)) return "A type has no name, or a name longer than 64 characters.";
            if (!names.Add(type.Name.Trim())) return $"The type {type.Name.Trim()} is there twice.";
            if (!IsTypeText(type.Value.Trim())) return $"The text of the type {type.Name.Trim()} is not a part of a branch name.";
        }

        return null;
    }

    private const int MaximumTypes = 64;
    private static readonly ImmutableArray<string> BranchTable = ["branch"];
    private static readonly ImmutableArray<string> TypesTable = ["branch", "types"];

    // Whether a text, valid TOML, still says a branch scheme.
    private static bool HasBranch(string content)
    {
        try
        {
            return (TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable()).ContainsKey("branch");
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The file is not valid TOML.", exception);
        }
    }

    // A text that ends with one line break, or an empty one; the line break of the file is kept.
    private static string WithOneFinalNewline(string content)
    {
        var trimmed = content.TrimEnd('\r', '\n', ' ', '\t');
        if (trimmed.Length == 0) return string.Empty;
        return trimmed + (content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    }

    private static string? Join(List<string> problems) => problems.Count == 0 ? null : string.Join(" ", problems);

    // The scheme one file makes of the one below it; what is wrong is said, and whether the file has a [branch] table.
    private static IssueBranchScheme Layer(IssueViewSource source, string projectRoot, IssueBranchScheme scheme, List<string> problems, out bool own)
    {
        own = false;
        var path = IssueViewStore.Locate(source, projectRoot);
        TomlTable root;
        try
        {
            if (SharedTextFile.TryReadAllText(path) is not { } text) return scheme;
            root = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            problems.Add($"'{path}' is not valid TOML: its [branch] is not used.");
            return scheme;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"'{path}' cannot be read: its [branch] is not used.");
            return scheme;
        }

        if (!root.TryGetValue("branch", out var found)) return scheme;
        own = true;
        if (found is not TomlTable branch)
        {
            problems.Add($"'{path}': branch is not a table.");
            return scheme;
        }

        return Apply(scheme, branch, problem => problems.Add($"'{path}': {problem}"));
    }

    // What one [branch] table changes in a scheme; what is wrong is said and left as it was.
    private static IssueBranchScheme Apply(IssueBranchScheme scheme, TomlTable branch, Action<string> problem)
    {
        if (branch.TryGetValue("template", out var template))
        {
            // A template with a placeholder that is not known is kept: the name it gives shows the mistake in the field.
            if (template is string text && text.Trim().Length > 0 && text.Length <= IssueBranchScheme.MaximumTemplateLength)
            {
                scheme = scheme with { Template = text.Trim() };
                if (IssueBranchScheme.TemplateProblem(scheme.Template) is { } wrong) problem(wrong);
            }
            else
            {
                problem($"template is not a text of 1 to {IssueBranchScheme.MaximumTemplateLength} characters.");
            }
        }

        if (branch.TryGetValue("title_length", out var length))
        {
            if (length is long value and >= 1 and <= IssueBranchScheme.MaximumTitleLength) scheme = scheme with { TitleLength = (int)value };
            else problem($"title_length is not a number from 1 to {IssueBranchScheme.MaximumTitleLength}.");
        }

        if (branch.TryGetValue("types", out var listed))
        {
            if (listed is TomlTable types)
            {
                var read = new List<IssueBranchType>();
                foreach (var (name, value) in types)
                {
                    if (value is string text && IsTypeText(text)) read.Add(new(name.Trim(), text.Trim()));
                    else problem($"types.{name} is not a part of a branch name.");
                }

                scheme = scheme with { Types = read };
            }
            else
            {
                problem("types is not a table.");
            }
        }

        return scheme;
    }

    // What {type} stands for: a part of a branch name, or nothing for a template that does not start with it.
    private static bool IsTypeText(string text)
        => text.Length <= 64 && text.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/')
           && !text.Contains("..", StringComparison.Ordinal) && !text.StartsWith('.') && !text.StartsWith('/') && !text.EndsWith('/');
}
