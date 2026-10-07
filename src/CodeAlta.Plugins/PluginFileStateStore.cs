using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// The data a plugin keeps between the runs of the application: one JSON file per item, in a folder of the
/// plugin. The data of the user is in <c>plugin-data/&lt;plugin&gt;</c> of the CodeAlta home, the data of a project
/// in <c>.alta/plugin-data/&lt;plugin&gt;</c> of that project, and the data of a session in
/// <c>sessions/&lt;session&gt;</c> under the data of the user.
/// </summary>
/// <remarks>
/// The project is the one of a project plugin, else the one the host names as selected; the session is the one the
/// host names as selected. Without one, the scope has no folder and its operations throw. A file is replaced as a
/// whole, so a reader never sees half of a write.
/// </remarks>
internal sealed class PluginFileStateStore : IPluginStateStore
{
    private readonly string _userDirectory;
    private readonly string _pluginFolder;
    private readonly string? _scopeProjectPath;
    private readonly IPluginServices _host;
    // Owned by the activation: it keeps the types of the plugin, which must go with the plugin.
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store of one plugin.</summary>
    /// <param name="dataRoot">The folder that holds the data of the plugins of the user.</param>
    /// <param name="pluginRuntimeKey">The key of the plugin, which names its folder.</param>
    /// <param name="scopeProjectPath">The project of a project plugin; null for a plugin of the user.</param>
    /// <param name="host">The services of the host, which name the selected project and session.</param>
    internal PluginFileStateStore(string dataRoot, string pluginRuntimeKey, string? scopeProjectPath, IPluginServices host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        ArgumentNullException.ThrowIfNull(host);
        _pluginFolder = FolderName(pluginRuntimeKey);
        _userDirectory = Path.Combine(dataRoot, _pluginFolder);
        _scopeProjectPath = string.IsNullOrWhiteSpace(scopeProjectPath) ? null : scopeProjectPath;
        _host = host;
    }

    /// <inheritdoc />
    public string GetDirectory(PluginStateScope scope)
        => scope switch
        {
            PluginStateScope.User => _userDirectory,
            PluginStateScope.Project => (_scopeProjectPath ?? _host.Workspace.SelectedProjectPath) is { Length: > 0 } project
                ? Path.Combine(project, ".alta", "plugin-data", _pluginFolder)
                : throw new InvalidOperationException("No project is selected: the plugin has no project data here."),
            PluginStateScope.Session => _host.Sessions.SelectedSessionId is { Length: > 0 } session
                ? Path.Combine(_userDirectory, "sessions", FolderName(session))
                : throw new InvalidOperationException("No session is selected: the plugin has no session data here."),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    /// <inheritdoc />
    public async ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
    {
        var path = ItemPath(scope, name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A file that is not what the plugin wrote is no data: the plugin starts again from nothing.
            return default;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default)
    {
        var path = ItemPath(scope, name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
    {
        var path = ItemPath(scope, name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    // "notes" and "notes.json" are the same item; a name is one file name, never a path.
    private string ItemPath(PluginStateScope scope, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var file = name.Trim();
        if (file.Length > 128 || file is "." or ".." || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains('/') || file.Contains('\\'))
        {
            throw new ArgumentException("The name of a state item is a file name: letters, digits, '.', '_' and '-'.", nameof(name));
        }

        return Path.Combine(GetDirectory(scope), file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file : file + ".json");
    }

    // "plugin:notes" is the folder "plugin_notes".
    private static string FolderName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return builder.ToString().Trim('.') is { Length: > 0 } name ? name : "plugin";
    }
}
