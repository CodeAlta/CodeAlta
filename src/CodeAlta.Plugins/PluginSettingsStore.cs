using System.Buffers;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Plugins;

/// <summary>
/// The values of the settings plugins declare, kept in <c>config.toml</c> under <c>[plugins.&lt;id&gt;]</c>: the user's file
/// for a setting of the user, the file of a project for a setting of each project. A value the user never changed is the
/// default of its setting. Each activation of a plugin is given its own view (<see cref="ForPlugin"/>), told of the changes
/// made through <see cref="Write"/> by <see cref="NotifyChangedAsync"/>.
/// </summary>
/// <remarks>
/// Writes edit one key of the text with <see cref="ConfigTableSyntax"/> under the lock of the file, so comments and the keys
/// of the table that are not settings (<c>enabled</c>, <c>spaces</c>, a policy of the plugin) stay. Reads parse the file
/// again: a hand edit is seen at the next read. The store keeps no view: a change reaches a plugin through its running
/// activation, so an activation that failed or ended is never called.
/// </remarks>
public sealed class PluginSettingsStore : IPluginSettingsService
{
    private readonly string _userConfigPath;
    private readonly Func<string, string?> _projectRoot;

    /// <summary>Creates the store over the configuration of the user and the folders of the projects.</summary>
    /// <param name="userConfigPath">The path of <c>~/.alta/config.toml</c>.</param>
    /// <param name="projectRoot">Gives the folder of a project from its id; null for a project it does not know.</param>
    /// <exception cref="ArgumentException"><paramref name="userConfigPath"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="projectRoot"/> is null.</exception>
    public PluginSettingsStore(string userConfigPath, Func<string, string?> projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userConfigPath);
        ArgumentNullException.ThrowIfNull(projectRoot);
        _userConfigPath = userConfigPath;
        _projectRoot = projectRoot;
    }

    /// <inheritdoc />
    /// <remarks>The store itself belongs to no plugin: it is never told of a change.</remarks>
    public event EventHandler<PluginSettingChangedEventArgs>? Changed { add { } remove { } }

    /// <inheritdoc />
    /// <remarks>The store itself belongs to no plugin: it reads nothing. A plugin reads through <see cref="ForPlugin"/>.</remarks>
    public JsonElement? GetValue(string key, string? projectId = null) => null;

    /// <summary>Gives an activation of a plugin its own view of its settings.</summary>
    /// <param name="pluginId">The id of the plugin in the configuration.</param>
    /// <param name="ownProjectId">The project of a plugin of a project, whose settings are all that project's; null for a plugin of the user.</param>
    /// <returns>The view; it reads the settings the activation declares once the runtime has collected them.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginId"/> is blank.</exception>
    public IPluginSettingsService ForPlugin(string pluginId, string? ownProjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new PluginScopedSettings(this, pluginId, ownProjectId);
    }

    /// <summary>
    /// Tells a running plugin that one of its settings changed. The handler runs off the caller's thread, admitted by the
    /// activation like its other callbacks: a plugin that is stopping is not called, and its stop waits for a handler that runs.
    /// </summary>
    /// <param name="plugin">The running plugin.</param>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project whose value changed, or null.</param>
    /// <param name="origin">Who changed it.</param>
    /// <returns>A task that ends once the plugin was told, or was not because it is stopping or busy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plugin"/> is null.</exception>
    public async Task NotifyChangedAsync(ActivePluginInstance plugin, string key, string? projectId, string origin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.RuntimeContext.Services.Settings is not PluginScopedSettings view || !ReferenceEquals(view.Store, this)) return;
        var change = new PluginSettingChangedEventArgs(key, projectId, origin);
        // A change the plugin cannot take now (stopping, too many callbacks running) is not told: it reads the value when it uses it.
        _ = await plugin.ObserveOwnedAgentEventAsync(() => Task.Run(() => view.Raise(change), CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>The path of the configuration that keeps a value: the user's, or the one of a project.</summary>
    /// <param name="setting">The setting.</param>
    /// <param name="projectId">The project, for a setting of each project.</param>
    /// <returns>The path; null for a project the store does not know, or a setting of each project without one.</returns>
    public string? PathOf(PluginSettingContribution setting, string? projectId)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Scope == PluginSettingScope.User) return _userConfigPath;
        return projectId is { Length: > 0 } && _projectRoot(projectId) is { Length: > 0 } root ? Path.Combine(root, ".alta", "config.toml") : null;
    }

    /// <summary>Reads the value of a setting: the one written in its file, else its default.</summary>
    /// <param name="pluginId">The id of the plugin.</param>
    /// <param name="setting">The setting.</param>
    /// <param name="projectId">The project, for a setting of each project.</param>
    /// <returns>The value, and whether it is written in the file; the default (or null) when the file cannot be read.</returns>
    public (JsonElement? Value, bool Written) Read(string pluginId, PluginSettingContribution setting, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(setting);
        if (PathOf(setting, projectId) is not { } path || !File.Exists(path)) return (setting.Default, false);
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? [];
            if (model.TryGetValue("plugins", out var plugins) && plugins is TomlTable all && all.TryGetValue(pluginId, out var own) && own is TomlTable table
                && table.TryGetValue(setting.Key, out var stored) && ToJson(stored) is { } value && setting.Fits(value))
                return (value, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TomlException)
        {
            // A file that cannot be read gives the default: the page of the configuration says what is wrong with it.
        }

        return (setting.Default, false);
    }

    /// <summary>Writes the value of a setting in its file, or removes it so that the setting takes its default again.</summary>
    /// <param name="pluginId">The id of the plugin.</param>
    /// <param name="setting">The setting.</param>
    /// <param name="value">The value, which fits the setting; null removes it.</param>
    /// <param name="projectId">The project, for a setting of each project.</param>
    /// <exception cref="ArgumentException">The value does not fit the setting.</exception>
    /// <exception cref="InvalidOperationException">A setting of each project is written without a project the store knows.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="InvalidDataException">The file is not a configuration this version reads.</exception>
    public void Write(string pluginId, PluginSettingContribution setting, JsonElement? value, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(setting);
        if (value is { } given && !setting.Fits(given)) throw new ArgumentException($"The value does not fit the setting '{setting.Key}'.", nameof(value));
        var path = PathOf(setting, projectId) ?? throw new InvalidOperationException($"The setting '{setting.Key}' is kept by each project: name a project.");
        string[] table = ["plugins", pluginId];
        CodeAltaConfigStore.EditConfigFile(path, text => value is not { } item ? ConfigTableSyntax.SetText(text, table, setting.Key, null) : setting.Kind switch
        {
            PluginSettingKind.Boolean => ConfigTableSyntax.SetValue(text, table, setting.Key, item.GetBoolean()),
            // A whole number is written as it is: a long above 2^53 has no exact double.
            PluginSettingKind.Integer => ConfigTableSyntax.SetInteger(text, table, setting.Key, item.GetInt64()),
            PluginSettingKind.Number => ConfigTableSyntax.SetNumber(text, table, setting.Key, item.GetDouble()),
            PluginSettingKind.List => ConfigTableSyntax.SetList(text, table, setting.Key, [.. item.EnumerateArray().Select(static entry => entry.GetString()!)]),
            _ => ConfigTableSyntax.SetText(text, table, setting.Key, item.GetString()),
        });
    }

    // A value of the file as JSON: a boolean, a number, a text or a list of texts; null for anything else.
    private static JsonElement? ToJson(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value)
            {
                case bool flag: writer.WriteBooleanValue(flag); break;
                case long whole: writer.WriteNumberValue(whole); break;
                case int small: writer.WriteNumberValue(small); break;
                case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
                case string text: writer.WriteStringValue(text); break;
                case TomlArray array when array.All(static item => item is string):
                    writer.WriteStartArray();
                    foreach (var item in array) writer.WriteStringValue((string)item!);
                    writer.WriteEndArray();
                    break;
                default: return null;
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The settings of one activation of a plugin: its own keys, under its id, of its own project when it is a plugin of a project.</summary>
    internal sealed class PluginScopedSettings(PluginSettingsStore store, string pluginId, string? ownProjectId) : IPluginSettingsService
    {
        private IReadOnlyList<PluginSettingContribution> _declared = [];

        public event EventHandler<PluginSettingChangedEventArgs>? Changed;

        internal PluginSettingsStore Store => store;

        /// <summary>Takes the settings the activation declares, once they are checked: before its <see cref="PluginBase.OnActivatedAsync"/>.</summary>
        internal void Declare(IReadOnlyList<PluginSettingContribution> settings) => Volatile.Write(ref _declared, settings);

        public JsonElement? GetValue(string key, string? projectId = null)
        {
            if (Volatile.Read(ref _declared).FirstOrDefault(setting => setting.Key == key) is not { } setting) return null;
            // A plugin of a project reads its own project only, whatever it asks.
            var project = ownProjectId is not null ? ownProjectId : setting.Scope == PluginSettingScope.Project ? projectId : null;
            if (ownProjectId is not null && setting.Scope != PluginSettingScope.Project) setting = setting with { Scope = PluginSettingScope.Project };
            return store.Read(pluginId, setting, project).Value;
        }

        internal void Raise(PluginSettingChangedEventArgs change)
        {
            try
            {
                Changed?.Invoke(this, change);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A plugin that fails on a change does not stop the change, nor the other plugins.
            }
        }
    }
}
