using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>Where a library script comes from: the samples of CodeAlta, your scripts, or the scripts of a project.</summary>
public enum CodeModeScriptScope
{
    /// <summary>A sample that comes with CodeAlta.</summary>
    BuiltIn,

    /// <summary>Your script, in <c>~/.alta/scripts</c>.</summary>
    Global,

    /// <summary>A script of the project, in <c>.alta/scripts</c>, committed with it.</summary>
    Project,
}

/// <summary>A step a library script declares in its <c>meta</c>.</summary>
/// <param name="Id">The id that calls and <c>step()</c> name.</param>
/// <param name="Title">The title shown on its box.</param>
/// <param name="After">
/// The steps it runs after: null means after the step declared before it, an empty list at the start. Steps with the same
/// <paramref name="After"/> are parallel branches.
/// </param>
/// <param name="Each">Whether the step fans out, one instance per item, at run time.</param>
public sealed record CodeModeLibraryStep(string Id, string Title, IReadOnlyList<string>? After, bool Each);

/// <summary>A script of the library: its name, where it is, and what its <c>meta</c> says.</summary>
/// <param name="Name">Its name, which <c>workflow("name")</c> runs.</param>
/// <param name="Scope">Where it comes from.</param>
/// <param name="Path">Its file.</param>
/// <param name="Hash">The SHA-256 of its content: a save names it to refuse a copy that changed since it was edited.</param>
/// <param name="Source">The script, read and prepared.</param>
public sealed record CodeModeLibraryScript(string Name, CodeModeScriptScope Scope, string Path, string Hash, CodeModeSource Source)
{
    /// <summary>Gets why the script does not parse, or null when it does.</summary>
    public string? Problem => Source.Error?.Message;

    /// <summary>Gets the title its <c>meta</c> gives, or null.</summary>
    public string? Title => Source.Descriptor?.Title;

    /// <summary>Gets the one-line description its <c>meta</c> gives, or null.</summary>
    public string? Description => Source.Descriptor?.Description;

    /// <summary>Gets the steps its <c>meta</c> declares, in their order: what a graph of the script draws before it runs.</summary>
    public IReadOnlyList<CodeModeLibraryStep> Steps
        => Source.Descriptor is { } descriptor
            ? [.. descriptor.Steps.Select(static step => new CodeModeLibraryStep(step.Id, step.Title, step.After, step.Each))]
            : [];

    /// <summary>Gets the JSON Schema of its arguments, built from <c>meta.params</c>, or null when it takes none.</summary>
    public string? ParametersSchemaJson => Source.Descriptor?.ParametersSchemaJson;

    /// <summary>Checks arguments against its parameters, as <c>workflow("name", args)</c> does before it runs it.</summary>
    /// <param name="argumentsJson">The arguments, a JSON object.</param>
    /// <returns>What is wrong with them, or null when they fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argumentsJson"/> is null.</exception>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="argumentsJson"/> is not JSON.</exception>
    public string? CheckArguments(string argumentsJson) => Source.Descriptor?.CheckArguments(argumentsJson);
}

/// <summary>
/// The library of scripts of Code Mode: the samples of CodeAlta, then your scripts (<c>~/.alta/scripts/&lt;name&gt;.js</c>), then the
/// scripts of the project (<c>.alta/scripts/&lt;name&gt;.js</c>), which win on a name they share. Agents publish scripts through
/// <see cref="Save"/>, never by writing the folders: <c>.alta</c> is out of the reach of their file tools on purpose, since
/// a script runs later with the tools of whoever runs it.
/// </summary>
public sealed partial class CodeModeScriptLibrary
{
    private readonly CatalogOptions _catalog;
    private readonly string? _builtInRoot;

    /// <summary>Creates the library of a host.</summary>
    /// <param name="catalog">The roots of the host: your scripts are under its global root.</param>
    /// <param name="builtInRoot">The folder of the samples of CodeAlta, or null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public CodeModeScriptLibrary(CatalogOptions catalog, string? builtInRoot = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _builtInRoot = builtInRoot;
    }

    /// <summary>Gets the folder of your scripts.</summary>
    public string GlobalFolder => System.IO.Path.Combine(_catalog.GlobalRoot, "scripts");

    /// <summary>Gets the folder of the scripts of a project.</summary>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>The folder.</returns>
    public static string ProjectFolder(string projectRoot) => System.IO.Path.Combine(projectRoot, ".alta", "scripts");

    /// <summary>Gets whether a name is the name of a script: lowercase letters, digits and hyphens, starting with a letter.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for a name.</returns>
    public static bool IsName(string? name) => name is { Length: > 0 and <= 64 } && NamePattern().IsMatch(name);

    /// <summary>Lists the scripts a project sees, one per name: the project's, then yours, then the samples.</summary>
    /// <param name="projectRoot">The folder of the project, or null for none.</param>
    /// <returns>The scripts, by name.</returns>
    public IReadOnlyList<CodeModeLibraryScript> List(string? projectRoot)
    {
        var byName = new Dictionary<string, CodeModeLibraryScript>(StringComparer.Ordinal);
        foreach (var (scope, folder) in Folders(projectRoot))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.js"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (IsName(name) && Read(name, scope, file) is { } script) byName[name] = script;
            }
        }

        return [.. byName.Values.OrderBy(static script => script.Name, StringComparer.Ordinal)];
    }

    /// <summary>Finds the script a project runs under a name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="projectRoot">The folder of the project, or null.</param>
    /// <returns>The script, or null.</returns>
    public CodeModeLibraryScript? Find(string name, string? projectRoot)
    {
        if (!IsName(name)) return null;
        foreach (var (scope, folder) in Folders(projectRoot).Reverse())
        {
            var file = System.IO.Path.Combine(folder, name + ".js");
            if (File.Exists(file) && Read(name, scope, file) is { } script) return script;
        }

        return null;
    }

    /// <summary>Gets the hash of the script a project runs under a name, without preparing it: what an automation was allowed with.</summary>
    /// <param name="name">The name.</param>
    /// <param name="projectRoot">The folder of the project, or null.</param>
    /// <returns>The hash, or null when there is no such script.</returns>
    public string? FindHash(string name, string? projectRoot)
    {
        if (!IsName(name)) return null;
        foreach (var (_, folder) in Folders(projectRoot).Reverse())
        {
            var file = System.IO.Path.Combine(folder, name + ".js");
            try
            {
                if (File.Exists(file)) return HashOf(File.ReadAllText(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Publishes a script: checks it, and writes it in the library of its scope. A script whose library copy changed since
    /// the hash the caller started from is refused, so that a save never loses a change made in the meantime.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="scope">Where to save it: <see cref="CodeModeScriptScope.Global"/> or <see cref="CodeModeScriptScope.Project"/>.</param>
    /// <param name="content">The script.</param>
    /// <param name="projectRoot">The folder of the project, for a project script.</param>
    /// <param name="expectedHash">The hash of the library copy the change started from, or null for a new script or to replace it.</param>
    /// <returns>The script saved, or why it was refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public (CodeModeLibraryScript? Script, string? Refusal) Save(string name, CodeModeScriptScope scope, string content, string? projectRoot, string? expectedHash)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsName(name)) return (null, $"'{name}' is not a script name: lowercase letters, digits and hyphens, starting with a letter.");
        if (scope == CodeModeScriptScope.BuiltIn) return (null, "The samples of CodeAlta are not saved: save a copy under your scripts or the project's.");
        if (scope == CodeModeScriptScope.Project && string.IsNullOrWhiteSpace(projectRoot)) return (null, "A project script needs a project.");
        var source = CodeModeSource.Prepare(content, name + ".js");
        if (source.Error is { } error) return (null, $"The script does not parse: {error.Message}");
        var folder = scope == CodeModeScriptScope.Project ? ProjectFolder(projectRoot!) : GlobalFolder;
        var file = System.IO.Path.Combine(folder, name + ".js");
        if (expectedHash is not null && File.Exists(file) && !string.Equals(HashOf(File.ReadAllText(file)), expectedHash, StringComparison.OrdinalIgnoreCase))
            return (null, $"The library copy of '{name}' changed since it was edited: edit it again from `alta script edit {name}`, then save.");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return (Read(name, scope, file), null);
    }

    /// <summary>Deletes a script of your library or of the project.</summary>
    /// <param name="name">The name.</param>
    /// <param name="scope">Where it is.</param>
    /// <param name="projectRoot">The folder of the project, for a project script.</param>
    /// <returns>True when it was deleted.</returns>
    /// <exception cref="IOException">The file cannot be deleted.</exception>
    public bool Delete(string name, CodeModeScriptScope scope, string? projectRoot)
    {
        if (!IsName(name) || scope == CodeModeScriptScope.BuiltIn || scope == CodeModeScriptScope.Project && string.IsNullOrWhiteSpace(projectRoot)) return false;
        var file = System.IO.Path.Combine(scope == CodeModeScriptScope.Project ? ProjectFolder(projectRoot!) : GlobalFolder, name + ".js");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>Gets the hash a save compares: the SHA-256 of the text, in lowercase hexadecimal.</summary>
    /// <param name="content">The text.</param>
    /// <returns>The hash.</returns>
    public static string HashOf(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Finds the folder of the project a folder is in: the nearest folder above it that has <c>.alta</c> or <c>.git</c>.</summary>
    /// <param name="folder">A folder, such as the working folder of a session.</param>
    /// <returns>The folder of the project, or null.</returns>
    public static string? ProjectRootOf(string? folder)
    {
        for (var current = string.IsNullOrWhiteSpace(folder) ? null : new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(current.FullName, ".alta")) || Directory.Exists(System.IO.Path.Combine(current.FullName, ".git"))
                || File.Exists(System.IO.Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    // The folders, from the lowest precedence to the highest.
    private IEnumerable<(CodeModeScriptScope Scope, string Folder)> Folders(string? projectRoot)
    {
        if (_builtInRoot is { Length: > 0 } builtIn) yield return (CodeModeScriptScope.BuiltIn, builtIn);
        yield return (CodeModeScriptScope.Global, GlobalFolder);
        if (!string.IsNullOrWhiteSpace(projectRoot)) yield return (CodeModeScriptScope.Project, ProjectFolder(projectRoot));
    }

    private static CodeModeLibraryScript? Read(string name, CodeModeScriptScope scope, string file)
    {
        try
        {
            var content = File.ReadAllText(file);
            return new CodeModeLibraryScript(name, scope, file, HashOf(content), CodeModeSource.Prepare(content, name + ".js"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex NamePattern();
}
