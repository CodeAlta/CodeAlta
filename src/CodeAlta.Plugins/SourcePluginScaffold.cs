using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Plugins;

/// <summary>The result of creating a source plugin package.</summary>
public sealed record SourcePluginScaffoldResult
{
    /// <summary>Gets the package that was created; null when it was not.</summary>
    public SourcePluginPackage? Package { get; init; }

    /// <summary>Gets why the package was not created: <c>invalid_id</c>, <c>exists</c> or <c>write_failed</c>; null when it was.</summary>
    public string? Error { get; init; }

    /// <summary>Gets a sentence for <see cref="Error"/>.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Creates the folder of a source plugin package with a first <c>plugin.cs</c> that builds and starts: a plugin
/// with one command, to be replaced by what the plugin is for.
/// </summary>
public static partial class SourcePluginScaffold
{
    /// <summary>The longest package id that is created: the id is also the name of the first command.</summary>
    public const int MaximumIdLength = 64;

    /// <summary>Creates the package folder, its <c>plugin.cs</c> and its <c>README.md</c>.</summary>
    /// <param name="root">The plugin folder that gets the package: the global one, or the one of a project.</param>
    /// <param name="packageId">The id of the package, which names its folder.</param>
    /// <param name="displayName">The name shown for the plugin; null derives one from the id.</param>
    /// <param name="description">One sentence that says what the plugin does; null writes a placeholder.</param>
    /// <returns>The package, or why it was not created. An existing package is never written over.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public static SourcePluginScaffoldResult Create(PluginRoot root, string? packageId, string? displayName = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(root.RootPath);
        if (packageId is not { Length: > 0 and <= MaximumIdLength } id || !PackageId().IsMatch(id))
        {
            return Refused("invalid_id", $"A plugin id starts with a letter or a digit and uses letters, digits, '.', '_' or '-' ({MaximumIdLength} at most).");
        }

        var directory = Path.Combine(root.RootPath, id);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return Refused("exists", $"The folder '{directory}' already exists and is not empty.");
        }

        var name = OneLine(displayName) is { Length: > 0 } given ? given : DisplayName(id);
        var summary = OneLine(description) is { Length: > 0 } text ? text : $"Adds the /{id} command.";
        try
        {
            Directory.CreateDirectory(directory);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(Path.Combine(directory, "plugin.cs"), Source(id, name, summary), encoding);
            File.WriteAllText(Path.Combine(directory, "README.md"), $"# {name}\n\n{summary}\n", encoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refused("write_failed", $"The plugin could not be written to '{directory}': {exception.Message}");
        }

        var package = new SourcePluginDiscoveryService().Discover(root).FirstOrDefault(found => string.Equals(found.PackageId, id, StringComparison.OrdinalIgnoreCase));
        return package is null ? Refused("write_failed", $"The plugin was written to '{directory}' and is not found there.") : new() { Package = package };
    }

    /// <summary>The first <c>plugin.cs</c> of a package.</summary>
    /// <param name="id">The package id: the key of the plugin and the name of its command.</param>
    /// <param name="displayName">The name shown for the plugin.</param>
    /// <param name="description">What the plugin does.</param>
    internal static string Source(string id, string displayName, string description)
        => SourceText(id, displayName, description).ReplaceLineEndings("\n");

    private static string SourceText(string id, string displayName, string description)
        => $$"""
using CodeAlta.Plugins.Abstractions;

[Plugin("{{id}}", DisplayName = "{{Literal(displayName)}}", Description = "{{Literal(description)}}")]
public sealed class {{ClassName(id)}} : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("{{id}}", "{{Literal(description)}}", static async (context, cancellationToken) =>
        {
            await context.Ui.NotifyAsync("Hello from {{Literal(displayName)}}.", cancellationToken);
            return PluginCommandResult.Handled;
        });
    }
}

""";

    // "my-notes" names the class MyNotesPlugin.
    internal static string ClassName(string id)
    {
        var builder = new StringBuilder();
        foreach (var word in id.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        if (builder.Length == 0 || !char.IsAsciiLetter(builder[0])) builder.Insert(0, "My");
        var name = builder.ToString();
        // "notes-plugin" is NotesPlugin; a class named Plugin would hide the attribute of the same name.
        return name.Length > "Plugin".Length && name.EndsWith("Plugin", StringComparison.Ordinal) ? name : name + "Plugin";
    }

    // "my-notes" is shown as "My notes".
    internal static string DisplayName(string id)
    {
        var words = string.Join(' ', id.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? OneLine(string? text)
        => text is null ? null : new string([.. text.Where(static character => !char.IsControl(character))]).Trim();

    private static string Literal(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static SourcePluginScaffoldResult Refused(string error, string message) => new() { Error = error, Message = message };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();
}
