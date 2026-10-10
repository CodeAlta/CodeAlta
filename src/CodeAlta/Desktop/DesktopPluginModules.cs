using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>A plugin as the module server sees it: its key, its package folder, and the object that is the same until the plugin is replaced.</summary>
/// <param name="RuntimeKey">The runtime key of the plugin.</param>
/// <param name="PackageDirectory">The package folder of a source plugin, or null for a built-in plugin.</param>
/// <param name="Activation">The object that stands for this activation of the plugin: a reloaded plugin has another one, which gives its modules other addresses.</param>
internal sealed record PluginModuleOwner(string RuntimeKey, string? PackageDirectory, object Activation)
{
    /// <summary>Gets a value indicating whether the plugin is one of the application: it has no package folder and its key says so.</summary>
    public bool IsBuiltIn => PackageDirectory is null && RuntimeKey.StartsWith("builtin:", StringComparison.Ordinal);
}

/// <summary>
/// Serves the script of plugins to the page: the files of a source plugin's package folder and the modules a plugin gives as text,
/// at <c>app://codealta/plugin/&lt;plugin key&gt;/&lt;stamp&gt;/&lt;path&gt;</c>, which the policy of the page (<c>script-src 'self'</c>)
/// accepts because it is the origin of the application.
/// </summary>
/// <remarks>
/// <para>
/// The <em>stamp</em> is part of the address so that a new build of the plugin, or a changed file, is a new module that the page
/// imports again: a browser never unloads a module, so the old one is let go, not replaced. The server does not trust the stamp
/// for anything else: it answers from the plugin that is active now, and only with what it publishes.
/// </para>
/// <para>
/// What is served is bounded. Only files with a known web extension (<see cref="MimeTypes"/>) and at most
/// <see cref="MaximumFileBytes"/> long, only below the package folder of the plugin, never through a link, and nothing for a plugin that
/// is not active. Modules given as text are kept in memory, a few sets for each plugin, and are served under generated names.
/// </para>
/// </remarks>
internal sealed class DesktopPluginModules : INeoResourceProvider
{
    /// <summary>The first segment of the addresses this server answers.</summary>
    internal const string Prefix = "/plugin/";

    /// <summary>The folder of the application's files that holds the modules of its own build that built-in plugins name (<see cref="PluginScript.App"/>).</summary>
    internal const string AppPrefix = "/lib/app/";

    /// <summary>The largest file served, in bytes.</summary>
    internal const int MaximumFileBytes = 8 * 1024 * 1024;

    /// <summary>The most sets of modules kept in memory for each plugin: the current one and a few that open tabs may still import.</summary>
    internal const int MaximumSetsPerPlugin = 8;

    /// <summary>The most files looked at to tell whether a package folder changed.</summary>
    internal const int MaximumFingerprintFiles = 512;

    // The files a plugin may serve, with the type the page needs to use them: scripts and styles, the pictures and fonts a script draws with, and data.
    private static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private static readonly string[] SkippedFolders = ["bin", "obj", ".git"];

    private Func<IReadOnlyList<PluginModuleOwner>> _activePlugins;
    private readonly ConditionalWeakTable<object, string> _activations = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<MemorySet>> _memory = new(StringComparer.Ordinal);

    /// <summary>Creates a server that serves nothing until <see cref="Attach"/> tells it which plugins are active: plugins start before the window has a page.</summary>
    internal DesktopPluginModules()
    {
        _activePlugins = static () => [];
    }

    /// <summary>Tells the server which plugins are active, from now on.</summary>
    /// <param name="activePlugins">Gives the plugins that are active now: what a request is answered from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="activePlugins"/> is null.</exception>
    internal void Attach(Func<IReadOnlyList<PluginModuleOwner>> activePlugins)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        _activePlugins = activePlugins;
    }

    /// <summary>Tells the server to serve the plugins that are active in a runtime, from now on.</summary>
    /// <param name="runtime">The plugin runtime of the host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    internal void Attach(PluginRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Attach(() => [.. runtime.ActivePlugins.Where(static plugin => plugin.Instance is not null).Select(OwnerOf)]);
    }

    /// <summary>The plugin as the server sees it.</summary>
    /// <param name="plugin">An active plugin.</param>
    /// <returns>Its key, its package folder when it has one, and the object that stands for this activation of it.</returns>
    internal static PluginModuleOwner OwnerOf(ActivePluginInstance plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return new(plugin.Descriptor.RuntimeKey, plugin.SourcePackage?.PackageDirectory, plugin);
    }

    /// <summary>Creates the server.</summary>
    /// <param name="activePlugins">Gives the plugins that are active now: what a request is answered from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="activePlugins"/> is null.</exception>
    internal DesktopPluginModules(Func<IReadOnlyList<PluginModuleOwner>> activePlugins)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        _activePlugins = activePlugins;
    }

    /// <summary>The address segment that names a plugin: its runtime key, encoded to be one safe path segment.</summary>
    internal static string KeySegment(string pluginKey) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(pluginKey));

    /// <summary>
    /// Makes a script of a plugin available and gives the path of its entry module, which the page imports.
    /// </summary>
    /// <param name="plugin">The active plugin the script belongs to.</param>
    /// <param name="script">The script.</param>
    /// <returns>The path of the entry (<c>/plugin/key/stamp/ui/board.js</c>, or <c>/lib/app/name.js</c> for a module of the application's build), or <see langword="null"/> when the script cannot be served: no entry, a file that is missing, too large or not a script.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal string? Publish(PluginModuleOwner plugin, PluginScript script)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(script);
        // A module of the application's own build: served by the application's files, so only the path is made here, and only for a plugin of the application.
        if (!string.IsNullOrEmpty(script.AppModule)) return plugin.IsBuiltIn && PluginScript.IsAppModuleName(script.AppModule) ? $"{AppPrefix}{script.AppModule}.js" : null;
        var key = KeySegment(plugin.RuntimeKey);
        var activation = _activations.GetValue(plugin.Activation, static _ => Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrEmpty(script.Path))
        {
            if (plugin.PackageDirectory is not { } directory || !TryResolve(directory, script.Path.Split('/'), out var file) || !IsScriptFile(file)) return null;
            return $"{Prefix}{key}/{FileStamp(activation, directory)}/{string.Join('/', script.Path.Split('/').Select(Uri.EscapeDataString))}";
        }

        if (string.IsNullOrWhiteSpace(script.Source)) return null;
        // The names are the ones modules import; the entry has a name of its own that no module can take.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [EntryName] = Encoding.UTF8.GetBytes(script.Source) };
        foreach (var (name, source) in script.Modules)
        {
            if (string.Equals(name, EntryName, StringComparison.Ordinal) || !MimeTypes.ContainsKey(Path.GetExtension(name))) continue;
            files[name] = Encoding.UTF8.GetBytes(source);
        }

        var stamp = MemoryStamp(activation, files);
        lock (_gate)
        {
            if (!_memory.TryGetValue(key, out var sets)) _memory[key] = sets = [];
            var existing = sets.FindIndex(set => set.Stamp == stamp);
            if (existing >= 0) sets.RemoveAt(existing);
            sets.Add(new MemorySet(stamp, files));
            while (sets.Count > MaximumSetsPerPlugin) sets.RemoveAt(0);
        }

        return $"{Prefix}{key}/{stamp}/{EntryName}";
    }

    /// <summary>Makes the script of an active plugin available, by the runtime key of the plugin.</summary>
    /// <param name="pluginKey">The runtime key of the plugin.</param>
    /// <param name="script">The script.</param>
    /// <returns>The path of its entry, or <see langword="null"/> when the plugin is not active or the script cannot be served.</returns>
    internal string? PublishFor(string pluginKey, PluginScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var owner = _activePlugins().FirstOrDefault(candidate => string.Equals(candidate.RuntimeKey, pluginKey, StringComparison.Ordinal));
        return owner is null ? null : Publish(owner, script);
    }

    /// <summary>The name an entry given as text has: scripts import their own modules by relative names, and this one is taken.</summary>
    internal const string EntryName = "main.js";

    /// <summary>Forgets what is kept for a plugin that is not active any more.</summary>
    internal void Prune()
    {
        var active = _activePlugins().Select(plugin => KeySegment(plugin.RuntimeKey)).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var key in _memory.Keys.Where(key => !active.Contains(key)).ToArray()) _memory.Remove(key);
        }
    }

    /// <inheritdoc />
    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var head = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (!head && !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return NeoResourceResponse.Empty(405, "Method Not Allowed");
        if (!TryParse(request.Uri, out var key, out var stamp, out var segments)) return NeoResourceResponse.Empty(404, "Not Found");
        var extension = Path.GetExtension(segments[^1]);
        if (!MimeTypes.TryGetValue(extension, out var mime)) return NeoResourceResponse.Empty(404, "Not Found");
        string pluginKey;
        try { pluginKey = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(key)); }
        catch (Exception exception) when (exception is FormatException or ArgumentException) { return NeoResourceResponse.Empty(404, "Not Found"); }
        var plugin = _activePlugins().FirstOrDefault(candidate => string.Equals(candidate.RuntimeKey, pluginKey, StringComparison.Ordinal));
        if (plugin is null) return NeoResourceResponse.Empty(404, "Not Found");
        // Modules the plugin gave as text.
        if (segments.Length == 1)
        {
            byte[]? bytes = null;
            lock (_gate)
            {
                if (_memory.TryGetValue(key, out var sets) && sets.Find(set => set.Stamp == stamp) is { } set) set.Files.TryGetValue(segments[0], out bytes);
            }

            if (bytes is not null) return head ? NeoResourceResponse.Empty(200, "OK") : NeoResourceResponse.FromBytes(bytes, mime);
        }

        // Files of the package folder.
        if (plugin.PackageDirectory is not { } directory || !TryResolve(directory, segments, out var path)) return NeoResourceResponse.Empty(404, "Not Found");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes) return NeoResourceResponse.Empty(404, "Not Found");
            return head ? NeoResourceResponse.Empty(200, "OK") : NeoResourceResponse.FromBytes(File.ReadAllBytes(path), mime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return NeoResourceResponse.Empty(404, "Not Found");
        }
    }

    // `/plugin/<key>/<stamp>/<segments...>`, each segment decoded and refused when it could leave the folder or name something else than a file.
    private static bool TryParse(Uri uri, out string key, out string stamp, out string[] segments)
    {
        key = stamp = string.Empty;
        segments = [];
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = uri.AbsolutePath[Prefix.Length..].Split('/');
        if (parts.Length < 3 || parts[0].Length is 0 or > 1024 || parts[1].Length is 0 or > 64) return false;
        key = parts[0];
        stamp = parts[1];
        if (!Base64UrlText(key) || !Base64UrlText(stamp)) return false;
        var decoded = new List<string>(parts.Length - 2);
        foreach (var part in parts.Skip(2))
        {
            string segment;
            try { segment = Uri.UnescapeDataString(part); }
            catch (UriFormatException) { return false; }
            if (!PlainSegment(segment)) return false;
            decoded.Add(segment);
        }

        if (decoded.Count > 16) return false;
        segments = [.. decoded];
        return true;
    }

    private static bool Base64UrlText(string text) => text.All(static value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_');

    // A name that is one entry of a folder and nothing else: no separator, no drive or stream, no relative segment, none that Windows would trim.
    private static bool PlainSegment(string segment)
        => segment.Length is > 0 and <= 255 && segment is not ("." or "..") && !segment.Any(static value => value is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(value))
            && segment[^1] is not ('.' or ' ') && segment[0] != ' ';

    /// <summary>Resolves path segments below a folder: the full path, or false when they leave the folder or pass through a link.</summary>
    internal static bool TryResolve(string directory, IReadOnlyList<string> segments, out string path)
    {
        path = string.Empty;
        if (segments.Count == 0 || segments.Any(static segment => !PlainSegment(segment))) return false;
        var root = Path.GetFullPath(directory);
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            // A link inside the folder could point anywhere: only plain files and folders are followed.
            try
            {
                if (!File.Exists(current) && !Directory.Exists(current)) return false;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(current);
        if (!full.StartsWith(prefix, comparison) || !File.Exists(full)) return false;
        path = full;
        return true;
    }

    private static bool IsScriptFile(string path)
        => Path.GetExtension(path) is { } extension && (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
            && new FileInfo(path).Length <= MaximumFileBytes;

    // What tells a new version of a package: the files it can serve, their sizes and their times. A reload or an edit of any of them is a new address.
    private static string FileStamp(string activation, string directory)
    {
        var builder = new StringBuilder(activation);
        var count = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0 && count < MaximumFingerprintFiles)
        {
            var (folder, depth) = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(static entry => entry.Name, StringComparer.Ordinal))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo child)
                    {
                        if (depth < 6 && !SkippedFolders.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) pending.Push((child.FullName, depth + 1));
                    }
                    else if (entry is FileInfo file && MimeTypes.ContainsKey(file.Extension) && count++ < MaximumFingerprintFiles)
                    {
                        builder.Append('|').Append(file.FullName).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string MemoryStamp(string activation, Dictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(activation));
        foreach (var (name, bytes) in files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(bytes);
        }

        return Base64Url.EncodeToString(hash.GetHashAndReset().AsSpan(0, 12));
    }

    private static string Hash(byte[] bytes) => Base64Url.EncodeToString(SHA256.HashData(bytes).AsSpan(0, 12));

    private sealed record MemorySet(string Stamp, Dictionary<string, byte[]> Files);
}
