using System.Collections.Frozen;
using System.Text;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.StartPrompts;

/// <summary>Where the text of a start prompt comes from.</summary>
public enum StartPromptSource
{
    /// <summary>It ships with CodeAlta.</summary>
    BuiltIn,

    /// <summary>It is the user's, for every project: <c>~/.alta/prompts/start/</c>.</summary>
    Global,

    /// <summary>It is kept with a project: <c>&lt;project&gt;/.alta/prompts/start/</c>.</summary>
    Project,
}

/// <summary>One text of a start prompt.</summary>
/// <param name="Id">The prompt it is the text of, such as <see cref="StartPromptCatalog.PullRequestReviewId"/>.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Path">Its file; null for what ships with CodeAlta.</param>
/// <param name="Content">The text, with its placeholders.</param>
public sealed record StartPrompt(string Id, StartPromptSource Source, string? Path, string Content)
{
    /// <summary>Gets whether a file nearer to the project has the same id and is used in place of this one.</summary>
    public bool Overridden { get; init; }
}

/// <summary>
/// The first words CodeAlta writes to a session it starts on a piece of work, such as the review of a pull request.
/// Each prompt has a text that ships with CodeAlta; a file of the user (<c>~/.alta/prompts/start/&lt;id&gt;.md</c>)
/// replaces it, and a file of a project replaces both. The text is the instruction alone: what it is about (the
/// title, the link and the description of the item) is added after it by the code that starts the session.
/// </summary>
public sealed class StartPromptCatalog
{
    /// <summary>The id of the prompt that starts the review of a pull request.</summary>
    public const string PullRequestReviewId = "pull-request-review";

    /// <summary>The extension of a file of a start prompt.</summary>
    public const string Extension = ".md";

    /// <summary>The most text one file holds, in characters.</summary>
    public const int MaximumContent = 16 * 1024;

    private static readonly FrozenDictionary<string, string> BuiltIns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PullRequestReviewId] = "Review the pull request {reference} of {location} on {service}: read its changes, check them out or fetch its branch if you need to, verify what it claims, and report what you found. Do not merge it, push to it or comment on it unless I ask.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly string _globalRoot;

    /// <summary>Creates the catalog of a CodeAlta root.</summary>
    /// <param name="options">The roots of the catalog.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public StartPromptCatalog(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _globalRoot = options.GlobalRoot;
    }

    /// <summary>Gets the ids of the start prompts, in the order they are shown.</summary>
    public static IReadOnlyList<string> Ids { get; } = [PullRequestReviewId];

    /// <summary>Gets the folder of the start prompts of the user.</summary>
    public string GlobalFolder => Path.Combine(_globalRoot, "prompts", "start");

    /// <summary>Gets the folder of the start prompts of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The folder.</returns>
    public static string ProjectFolder(string projectPath) => Path.Combine(projectPath, ".alta", "prompts", "start");

    /// <summary>Gets the text of a start prompt that ships with CodeAlta.</summary>
    /// <param name="id">The id of the prompt.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> names no start prompt.</exception>
    public static string BuiltIn(string id)
        => id is not null && BuiltIns.TryGetValue(id, out var text) ? text : throw new ArgumentException($"There is no start prompt '{id}'.", nameof(id));

    /// <summary>Checks an id.</summary>
    /// <param name="id">What was written.</param>
    /// <returns>The id in lower case; null when it names no start prompt.</returns>
    public static string? NormalizeId(string? id)
    {
        var text = id?.Trim().ToLowerInvariant();
        return text is not null && BuiltIns.ContainsKey(text) ? text : null;
    }

    /// <summary>
    /// Lists the texts of every start prompt: the one that ships, then the user's, then the project's. One that is
    /// replaced by a nearer file is marked <see cref="StartPrompt.Overridden"/>.
    /// </summary>
    /// <param name="projectPath">The folder of a project; null for the texts of every project.</param>
    /// <returns>The texts.</returns>
    public IReadOnlyList<StartPrompt> List(string? projectPath)
    {
        var all = new List<StartPrompt>();
        foreach (var id in Ids)
        {
            var texts = new List<StartPrompt> { new(id, StartPromptSource.BuiltIn, null, BuiltIns[id]) };
            if (Read(null, id) is { } global) texts.Add(global);
            if (!string.IsNullOrWhiteSpace(projectPath) && Read(projectPath, id) is { } project) texts.Add(project);
            // The last one is the one in use: the nearest to the project.
            all.AddRange(texts.Select((text, index) => index < texts.Count - 1 ? text with { Overridden = true } : text));
        }

        return all;
    }

    /// <summary>Gets the text a session of a project is started with: the nearest to the project.</summary>
    /// <param name="id">The id of the prompt.</param>
    /// <param name="projectPath">The folder of the project; null for none.</param>
    /// <returns>The text, with its placeholders.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> names no start prompt.</exception>
    public string Text(string id, string? projectPath)
    {
        var key = NormalizeId(id) ?? throw new ArgumentException($"There is no start prompt '{id}'.", nameof(id));
        return (!string.IsNullOrWhiteSpace(projectPath) ? Read(projectPath, key) : null)?.Content
            ?? Read(null, key)?.Content
            ?? BuiltIns[key];
    }

    /// <summary>Writes the text of a start prompt of the user or of a project.</summary>
    /// <param name="projectPath">The folder of the project it is kept with; null for the user's.</param>
    /// <param name="id">The id of the prompt.</param>
    /// <param name="content">The text.</param>
    /// <returns>The text as it was written.</returns>
    /// <exception cref="ArgumentException">The id names no start prompt, or the text is empty or too long.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or one of its folders is a link, or access is denied.</exception>
    public StartPrompt Save(string? projectPath, string id, string content)
    {
        var key = NormalizeId(id) ?? throw new ArgumentException($"There is no start prompt '{id}'.", nameof(id));
        ArgumentNullException.ThrowIfNull(content);
        var body = content.ReplaceLineEndings("\n").Trim();
        if (body.Length is 0 or > MaximumContent) throw new ArgumentException($"The text holds between 1 and {MaximumContent} characters.", nameof(content));
        var (root, path) = Locate(projectPath, key);
        CheckNotLinked(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckNotLinked(root, path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, body + "\n", new UTF8Encoding(false));
            StagedFileReplacement.MoveOver(temporary, path);
        }
        finally
        {
            // A write that failed leaves no staged file beside the prompt.
            try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return new(key, projectPath is null ? StartPromptSource.Global : StartPromptSource.Project, path, body);
    }

    /// <summary>Deletes the text of a start prompt of the user or of a project: the one that ships is used again.</summary>
    /// <param name="projectPath">The folder of the project it is kept with; null for the user's.</param>
    /// <param name="id">The id of the prompt.</param>
    /// <returns>False when there is no such file.</returns>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or one of its folders is a link, or access is denied.</exception>
    public bool Delete(string? projectPath, string id)
    {
        if (NormalizeId(id) is not { } key) return false;
        var (root, path) = Locate(projectPath, key);
        if (!File.Exists(path)) return false;
        CheckNotLinked(root, path);
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Replaces each <c>{name}</c> of a text whose name is a key of <paramref name="values"/> by its value, in one pass:
    /// a value that holds a placeholder is not replaced again. Any other text in braces is kept as it is.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="values">The values, by name.</param>
    /// <returns>The text with its values.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        var result = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            var close = open < 0 ? -1 : text.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(text, index, text.Length - index);
                break;
            }

            result.Append(text, index, open - index);
            if (values.TryGetValue(text[(open + 1)..close], out var value))
            {
                result.Append(value);
                index = close + 1;
            }
            else
            {
                result.Append('{');
                index = open + 1;
            }
        }

        return result.ToString();
    }

    // The file of a prompt of the user (no project) or of a project, with the folder its path is checked from.
    private (string Root, string Path) Locate(string? projectPath, string key)
        => projectPath is null ? (_globalRoot, Path.Combine(GlobalFolder, key + Extension)) : (projectPath, Path.Combine(ProjectFolder(projectPath), key + Extension));

    // A link in the folders below the root, or the file itself as a link, could lead a read, a write or a delete to
    // any file of whoever opens the project. The root and what is above it are the user's own folders.
    private static void CheckNotLinked(string root, string path)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var component = Path.GetFullPath(path); component.Length > top.Length; component = Path.GetDirectoryName(component)!)
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("A start prompt is not read or written through a link.");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private StartPrompt? Read(string? projectPath, string id)
    {
        var (root, path) = Locate(projectPath, id);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumContent * 4) return null;
            CheckNotLinked(root, path);
            // A save may replace the file while it is read: the read shares it with writers and deletes.
            var content = SharedTextFile.ReadAllText(path).ReplaceLineEndings("\n").Trim();
            if (content.Length > MaximumContent) content = content[..MaximumContent];
            return content.Length == 0 ? null : new(id, projectPath is null ? StartPromptSource.Global : StartPromptSource.Project, path, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
