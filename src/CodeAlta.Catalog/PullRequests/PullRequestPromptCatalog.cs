using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodeAlta.Catalog.PullRequests;

/// <summary>Where the instructions of a kind of pull request come from.</summary>
public enum PullRequestPromptSource
{
    /// <summary>They ship with CodeAlta.</summary>
    BuiltIn,

    /// <summary>They are the user's, for every project: <c>~/.alta/prompts/pull-requests/</c>.</summary>
    Global,

    /// <summary>They are kept with a project: <c>&lt;project&gt;/.alta/prompts/pull-requests/</c>.</summary>
    Project,
}

/// <summary>
/// The instructions a session is sent when the user asks it to create a pull request. A kind is a Markdown file
/// (<c>name.pr.md</c>) with an optional front matter (<c>name</c>, <c>description</c>).
/// </summary>
/// <param name="Id">The name of the file without <c>.pr.md</c>, in lower case.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Description">One line that says what it does.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Path">Its file; null for what ships with CodeAlta.</param>
/// <param name="Content">The instructions, without the front matter.</param>
public sealed record PullRequestPrompt(string Id, string Name, string? Description, PullRequestPromptSource Source, string? Path, string Content)
{
    /// <summary>Gets whether a file nearer to the project has the same id and is used in place of this one.</summary>
    public bool Overridden { get; init; }
}

/// <summary>
/// The kinds of pull request a session can be asked to create: the one that ships with CodeAlta (<c>default</c>),
/// those of the user, and those of a project. A file of the project replaces one of the user with the same name,
/// which replaces the built-in one.
/// </summary>
public sealed class PullRequestPromptCatalog
{
    /// <summary>The id of the kind that ships with CodeAlta.</summary>
    public const string DefaultId = "default";

    /// <summary>The extension of a file of instructions.</summary>
    public const string Extension = ".pr.md";

    /// <summary>The most text one file of instructions holds, in characters.</summary>
    public const int MaximumContent = 32 * 1024;

    /// <summary>The most kinds read from one folder.</summary>
    public const int MaximumPrompts = 64;

    private const string ResourceName = "CodeAlta.Catalog.PullRequests.default.pr.md";
    private static readonly Lazy<PullRequestPrompt> BuiltIn = new(static () => Parse(DefaultId, ReadResource(), PullRequestPromptSource.BuiltIn, null));
    private readonly string _globalRoot;

    /// <summary>Creates the catalog of a CodeAlta root.</summary>
    /// <param name="options">The roots of the catalog.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PullRequestPromptCatalog(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _globalRoot = options.GlobalRoot;
    }

    /// <summary>Gets the instructions that ship with CodeAlta.</summary>
    public static PullRequestPrompt Default => BuiltIn.Value;

    /// <summary>Gets the folder of the kinds of the user.</summary>
    public string GlobalFolder => Path.Combine(_globalRoot, "prompts", "pull-requests");

    /// <summary>Gets the folder of the kinds of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The folder.</returns>
    public static string ProjectFolder(string projectPath) => Path.Combine(projectPath, ".alta", "prompts", "pull-requests");

    /// <summary>
    /// Lists the kinds, the built-in one first, then the user's, then the project's. One that is replaced by a nearer
    /// file of the same id is marked <see cref="PullRequestPrompt.Overridden"/>.
    /// </summary>
    /// <param name="projectPath">The folder of a project; null for the kinds of every project.</param>
    /// <returns>The kinds.</returns>
    public IReadOnlyList<PullRequestPrompt> List(string? projectPath)
    {
        var all = new List<PullRequestPrompt> { Default };
        all.AddRange(Read(GlobalFolder, PullRequestPromptSource.Global));
        if (!string.IsNullOrWhiteSpace(projectPath)) all.AddRange(Read(ProjectFolder(projectPath), PullRequestPromptSource.Project));
        // The last one of an id is the one in use: the nearest to the project.
        return [.. all.Select((prompt, index) => all.Skip(index + 1).Any(later => later.Id == prompt.Id) ? prompt with { Overridden = true } : prompt)];
    }

    /// <summary>Lists the kinds a session of a project can be sent: one for each id, the nearest to the project.</summary>
    /// <param name="projectPath">The folder of the project; null for none.</param>
    /// <returns>The kinds, the default one first, then by name.</returns>
    public IReadOnlyList<PullRequestPrompt> Effective(string? projectPath)
        => [.. List(projectPath).Where(static prompt => !prompt.Overridden).OrderBy(static prompt => prompt.Id == DefaultId ? 0 : 1).ThenBy(static prompt => prompt.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Writes a kind of the user or of a project.</summary>
    /// <param name="projectPath">The folder of the project it is kept with; null for the user's.</param>
    /// <param name="id">Its id: letters, digits, <c>-</c> or <c>_</c>.</param>
    /// <param name="name">The name shown to the user; null for the id.</param>
    /// <param name="description">One line that says what it does.</param>
    /// <param name="content">The instructions.</param>
    /// <returns>The kind as it was written.</returns>
    /// <exception cref="ArgumentException">The id or the instructions are not ones that can be written.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public PullRequestPrompt Save(string? projectPath, string id, string? name, string? description, string content)
    {
        var key = NormalizeId(id) ?? throw new ArgumentException("An id uses letters, digits, '-' or '_' (at most 64).", nameof(id));
        ArgumentNullException.ThrowIfNull(content);
        var body = content.ReplaceLineEndings("\n").Trim();
        if (body.Length is 0 or > MaximumContent) throw new ArgumentException($"The instructions hold between 1 and {MaximumContent} characters.", nameof(content));
        var folder = projectPath is null ? GlobalFolder : ProjectFolder(projectPath);
        Directory.CreateDirectory(folder);
        var text = new StringBuilder("---\n");
        // A header is always written: without one, instructions that begin with a rule would be read as a header.
        text.Append("name: ").Append(Quote(OneLine(name) ?? key)).Append('\n');
        if (OneLine(description) is { } about) text.Append("description: ").Append(Quote(about)).Append('\n');
        text.Append("---\n").Append(body).Append('\n');
        var path = Path.Combine(folder, key + Extension);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return Parse(key, text.ToString(), projectPath is null ? PullRequestPromptSource.Global : PullRequestPromptSource.Project, path);
    }

    /// <summary>Deletes a kind of the user or of a project.</summary>
    /// <param name="projectPath">The folder of the project it is kept with; null for the user's.</param>
    /// <param name="id">Its id.</param>
    /// <returns>False when there is no such file.</returns>
    public bool Delete(string? projectPath, string id)
    {
        if (NormalizeId(id) is not { } key) return false;
        var path = Path.Combine(projectPath is null ? GlobalFolder : ProjectFolder(projectPath), key + Extension);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Checks an id: letters, digits, <c>-</c> or <c>_</c>, at most 64.</summary>
    /// <param name="id">What was written.</param>
    /// <returns>The id in lower case; null when it is not one.</returns>
    public static string? NormalizeId(string? id)
    {
        var text = id?.Trim().ToLowerInvariant();
        return text is { Length: > 0 and <= 64 } && text.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? text : null;
    }

    private static IEnumerable<PullRequestPrompt> Read(string folder, PullRequestPromptSource source)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return [];
            files = Directory.GetFiles(folder, "*" + Extension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var prompts = new List<PullRequestPrompt>();
        foreach (var file in files.OrderBy(static file => file, StringComparer.OrdinalIgnoreCase).Take(MaximumPrompts))
        {
            if (NormalizeId(Path.GetFileName(file)[..^Extension.Length]) is not { } id) continue;
            try
            {
                var info = new FileInfo(file);
                // A link could show any file of whoever opens the project.
                if (info.LinkTarget is not null || info.Length > MaximumContent * 4) continue;
                prompts.Add(Parse(id, File.ReadAllText(file), source, file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return prompts.Where(static prompt => prompt.Content.Length > 0);
    }

    private static PullRequestPrompt Parse(string id, string text, PullRequestPromptSource source, string? path)
    {
        var (frontmatter, body) = PromptFileFormat.SplitFrontmatter(text.ReplaceLineEndings("\n"));
        var content = body.Trim();
        if (content.Length > MaximumContent) content = content[..MaximumContent];
        var name = frontmatter.TryGetValue("name", out var named) ? OneLine(named) : null;
        var description = frontmatter.TryGetValue("description", out var described) ? OneLine(described) : null;
        return new(id, name ?? id, description, source, path, content);
    }

    private static string? OneLine(string? value)
    {
        var text = value?.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= 200 ? text : text[..200];
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    private static string ReadResource()
    {
        using var stream = typeof(PullRequestPromptCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The instructions for a pull request that ship with CodeAlta are missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
