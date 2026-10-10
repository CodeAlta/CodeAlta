namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// The script of the HTML of a plugin in the desktop application: a JavaScript module that the application serves from
/// the plugin and runs in its window, next to the fragment it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A module has one of two forms. It exports a React component (<c>export default function Board()</c>), which the
/// application draws in its own tree; or it exports <c>mount(root, alta)</c>, which fills the element that holds the
/// fragment and may return a function that is called when the content goes away. Either gets the libraries of the
/// application by their usual names (<c>react</c>, <c>@blueprintjs/core</c>, <c>flexlayout-react</c>, <c>lucide-react</c>) and
/// the small module <c>codealta</c>, and reaches the application through the <c>alta</c> object.
/// </para>
/// <para>
/// The script is given next to the HTML, never inside it: a <c>&lt;script&gt;</c> or an <c>on...=</c> attribute written in
/// a fragment is removed, so text that comes from outside never becomes script. Only a script named here runs.
/// </para>
/// <para>
/// The application serves the files of the package folder of a source plugin (<see cref="File"/>), and modules that the
/// plugin gives as text (<see cref="Inline"/>, <see cref="WithModule"/>), which keeps a plugin of one file a plugin of one file. A built-in
/// plugin whose interface is written in the frontend of the application names a module of the application's own build (<see cref="App"/>).
/// </para>
/// </remarks>
public sealed record PluginScript
{
    /// <summary>The most characters of one module given as text.</summary>
    public const int MaximumSourceLength = 1024 * 1024;

    /// <summary>The most modules given as text for one script, the entry included.</summary>
    public const int MaximumModules = 32;

    /// <summary>Gets the path of the entry module in the package folder of the plugin, with <c>/</c> separators, or <see langword="null"/> when the entry is given as text.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the entry module as text, or <see langword="null"/> when the entry is a file of the package.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the name of a module that the application's own build emits (<see cref="App"/>), or <see langword="null"/>. Only a built-in plugin may
    /// name one: a plugin of a package folder has no such module.
    /// </summary>
    public string? AppModule { get; init; }

    /// <summary>
    /// Gets the other modules given as text, by the relative name an <c>import "./name.js"</c> of the entry uses. They are served
    /// next to the entry.
    /// </summary>
    public IReadOnlyDictionary<string, string> Modules { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates a script whose entry is a file of the package folder of the plugin.</summary>
    /// <param name="path">The path of the module relative to the package folder, with <c>/</c> separators (<c>ui/board.js</c>). It ends in <c>.js</c> or <c>.mjs</c>.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank, absolute, leaves the folder, or is not a JavaScript file name.</exception>
    public static PluginScript File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsModulePath(path)) throw new ArgumentException("The path of a script is relative to the package folder, uses '/' and ends in .js or .mjs.", nameof(path));
        return new PluginScript { Path = path };
    }

    /// <summary>
    /// Creates a script whose entry is a module of the application's own build, for a built-in plugin whose interface is written in the frontend of the
    /// application (<c>src/CodeAlta/frontend</c>) and built with it. The module is an entry of the application's build, listed in
    /// <c>src/lent/appModules.ts</c> and emitted at <c>lib/app/&lt;name&gt;.js</c>; it exports a component or <c>mount</c> as any script does, and
    /// imports application code and libraries directly.
    /// </summary>
    /// <param name="name">The name of the module: lowercase letters, digits and <c>-</c>, starting with a letter, at most 64 characters.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not such a name.</exception>
    public static PluginScript App(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IsAppModuleName(name)) throw new ArgumentException("The name of an application module is lowercase letters, digits and '-', starting with a letter.", nameof(name));
        return new PluginScript { AppModule = name };
    }

    /// <summary>Whether a text is the name of an application module.</summary>
    /// <param name="name">The text.</param>
    /// <returns><see langword="true"/> for lowercase letters, digits and <c>-</c>, starting with a letter, at most 64 characters.</returns>
    public static bool IsAppModuleName(string? name)
        => name is { Length: > 0 and <= 64 } && name[0] is >= 'a' and <= 'z' && name.All(static value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Creates a script whose entry is the text of a module.</summary>
    /// <param name="source">The JavaScript module.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is blank or longer than <see cref="MaximumSourceLength"/>.</exception>
    public static PluginScript Inline(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > MaximumSourceLength) throw new ArgumentException($"A script is at most {MaximumSourceLength} characters.", nameof(source));
        return new PluginScript { Source = source };
    }

    /// <summary>Returns the script with one more module given as text, which the others import by <paramref name="name"/>.</summary>
    /// <param name="name">The file name the modules import, such as <c>charts.js</c>: no folder, ending in <c>.js</c> or <c>.mjs</c>.</param>
    /// <param name="source">The JavaScript module.</param>
    /// <returns>The script with the module.</returns>
    /// <exception cref="ArgumentException">The name or the source is not valid, or the script holds too many modules.</exception>
    public PluginScript WithModule(string name, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!IsModulePath(name) || name.Contains('/', StringComparison.Ordinal)) throw new ArgumentException("The name of a module is a file name ending in .js or .mjs.", nameof(name));
        if (source.Length > MaximumSourceLength) throw new ArgumentException($"A module is at most {MaximumSourceLength} characters.", nameof(source));
        if (Modules.Count >= MaximumModules - 1 && !Modules.ContainsKey(name)) throw new ArgumentException($"A script has at most {MaximumModules} modules.", nameof(name));
        var modules = new Dictionary<string, string>(Modules, StringComparer.Ordinal) { [name] = source };
        return this with { Modules = modules };
    }

    /// <summary>Gets a value indicating whether the script names an entry: a file of the package or a text.</summary>
    public bool HasEntry => !string.IsNullOrEmpty(Path) || !string.IsNullOrWhiteSpace(Source) || !string.IsNullOrEmpty(AppModule);

    // A relative path of plain segments that ends in a JavaScript extension: what the host serves from a package folder.
    private static bool IsModulePath(string path)
    {
        if (path.Length > 256 || !(path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.AsSpan().IndexOfAny("\\:*?\"<>|\0") >= 0 || char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1])) return false;
        }

        return true;
    }
}

/// <summary>
/// The UI service of a host that knows which plugin asks. The runtime asks the host for the service of each plugin it starts, so that what a
/// plugin shows (a dialog with a script) is tied to the plugin that shows it; a host that does not implement this interface gives every plugin
/// the same service.
/// </summary>
public interface IPluginUiRuntimeService : IPluginUiService
{
    /// <summary>Gets the service of one plugin.</summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <returns>A service that shows what the plugin gives in the name of that plugin.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is null, empty or whitespace.</exception>
    IPluginUiService ForPlugin(string pluginRuntimeKey);
}
