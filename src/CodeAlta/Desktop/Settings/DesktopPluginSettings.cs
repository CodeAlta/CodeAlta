using System.Text.Json;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Settings;

/// <summary>
/// The settings the running plugins declare (<see cref="PluginBase.GetSettings"/>), in the registry of the settings: each one
/// is <c>plugins.&lt;id&gt;.&lt;key&gt;</c>, on the settings page of its plugin, and its value is kept by
/// <see cref="PluginSettingsStore"/> in <c>config.toml</c>. A change is told to the plugin.
/// </summary>
internal sealed class DesktopPluginSettings : IDesktopSettingsExtension
{
    private readonly Func<IReadOnlyList<ActivePluginInstance>> _plugins;
    private readonly PluginSettingsStore _store;

    /// <summary>Creates the settings over the running plugins and the store of their values.</summary>
    /// <param name="plugins">Gives the running plugins now.</param>
    /// <param name="store">Keeps the values.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopPluginSettings(Func<IReadOnlyList<ActivePluginInstance>> plugins, PluginSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(store);
        _plugins = plugins;
        _store = store;
        store.Declared = runtimeKey => Find(runtimeKey)?.Settings ?? [];
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Tells the registry that the plugins changed: one started, stopped or was built again.</summary>
    internal void OnPluginsChanged() => Changed?.Invoke();

    /// <inheritdoc />
    public IReadOnlyList<AltaSettingDescriptor> List()
    {
        var list = new List<AltaSettingDescriptor>();
        foreach (var plugin in Plugins())
        {
            foreach (var setting in plugin.Settings) list.Add(Describe(plugin, setting));
        }

        return list;
    }

    /// <inheritdoc />
    public ValueTask<AltaSettingRead> GetAsync(AltaSettingDescriptor setting, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (Resolve(setting.Id) is not { } found) return new(new AltaSettingRead(AltaSettingStatus.Unavailable, null, null, $"The plugin of {setting.Id} is not running."));
        var project = ProjectOf(found.Plugin, found.Setting, projectId);
        if (found.Setting.Scope == PluginSettingScope.Project && project is null)
            return new(new AltaSettingRead(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is kept by each project: name the project with --project."));
        var (value, written) = _store.Read(found.Plugin.Id, found.Setting, project);
        return new(new AltaSettingRead(AltaSettingStatus.Ok, value, written ? project is null ? "user" : "project" : "default", null));
    }

    /// <inheritdoc />
    public ValueTask<AltaSettingWrite> SetAsync(AltaSettingDescriptor setting, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(origin);
        if (Resolve(setting.Id) is not { } found) return new(new AltaSettingWrite(AltaSettingStatus.Unavailable, null, null, $"The plugin of {setting.Id} is not running."));
        var project = ProjectOf(found.Plugin, found.Setting, projectId);
        if (found.Setting.Scope == PluginSettingScope.Project && project is null)
            return new(new AltaSettingWrite(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is kept by each project: name the project with --project."));
        try
        {
            var previous = _store.Read(found.Plugin.Id, found.Setting, project).Value;
            _store.Write(found.Plugin.Id, found.Setting, value, project);
            var now = _store.Read(found.Plugin.Id, found.Setting, project).Value;
            _store.NotifyChanged(found.Plugin.RuntimeKey, found.Setting.Key, project, origin.Kind);
            return new(new AltaSettingWrite(AltaSettingStatus.Ok, now, previous, null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new(new AltaSettingWrite(AltaSettingStatus.Failed, null, null, $"{setting.Id} could not be written: {exception.Message}"));
        }
    }

    // A plugin of a project keeps its settings in that project; a setting of each project of a plugin of the user, in the one named.
    private static string? ProjectOf(PluginSettings plugin, PluginSettingContribution setting, string? projectId)
        => setting.Scope == PluginSettingScope.User ? null : plugin.OwnProjectId ?? projectId;

    private (PluginSettings Plugin, PluginSettingContribution Setting)? Resolve(string id)
    {
        foreach (var plugin in Plugins())
        {
            var prefix = $"plugins.{plugin.Id}.";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var key = id[prefix.Length..];
            if (plugin.Settings.FirstOrDefault(setting => setting.Key == key) is { } found) return (plugin, found);
        }

        return null;
    }

    private PluginSettings? Find(string runtimeKey) => Plugins().FirstOrDefault(plugin => plugin.RuntimeKey == runtimeKey);

    private IEnumerable<PluginSettings> Plugins()
    {
        foreach (var plugin in _plugins())
        {
            var settings = plugin.Contributions.Select(static registration => registration.Contribution).OfType<PluginSettingContribution>().ToArray();
            if (settings.Length == 0) continue;
            var runtimeKey = plugin.Descriptor.RuntimeKey;
            var id = PluginContributionRegistry.PluginIdOf(runtimeKey, plugin.SourcePackage?.PackageId);
            var canvas = plugin.Contributions.Select(static registration => registration.Contribution).OfType<PluginCanvasContribution>()
                .FirstOrDefault(static canvas => canvas.Placement == PluginCanvasPlacement.Settings);
            // The settings of a plugin are on its settings page: the one of its canvas, else one of their own.
            var page = canvas is null ? $"plugin/{runtimeKey}" : $"plugin/{runtimeKey}/{canvas.Id}";
            var ownProject = plugin.RuntimeContext.Scope == PluginScope.Project ? plugin.RuntimeContext.ScopeProjectId : null;
            yield return new PluginSettings(runtimeKey, id, plugin.Descriptor.DisplayName ?? id, page, ownProject, settings);
        }
    }

    private static AltaSettingDescriptor Describe(PluginSettings plugin, PluginSettingContribution setting)
        => new($"plugins.{plugin.Id}.{setting.Key}", setting.Label, setting.Description, plugin.Page, $"Extensions › {plugin.Name}", setting.Group, KindOf(setting.Kind),
            [.. setting.Choices.Select(static choice => new AltaSettingChoice(choice.Value, choice.Label))], setting.Minimum, setting.Maximum, setting.Default,
            setting.Agents == PluginSettingAccess.ReadWrite ? AltaSettingAccess.ReadWrite : AltaSettingAccess.Read, setting.Keywords)
        {
            Owner = "plugin",
            Applies = setting.AppliesOnRestart ? "nextStart" : "now",
            Scopes = setting.Scope == PluginSettingScope.Project ? ["project"] : ["user"],
            Plugin = plugin.Id,
            PluginName = plugin.Name,
        };

    private static string KindOf(PluginSettingKind kind) => kind switch
    {
        PluginSettingKind.Boolean => AltaSettingKinds.Boolean,
        PluginSettingKind.Choice => AltaSettingKinds.Choice,
        PluginSettingKind.Integer => AltaSettingKinds.Integer,
        PluginSettingKind.Number => AltaSettingKinds.Number,
        PluginSettingKind.Path => AltaSettingKinds.Path,
        PluginSettingKind.List => AltaSettingKinds.List,
        _ => AltaSettingKinds.Text,
    };

    private sealed record PluginSettings(string RuntimeKey, string Id, string Name, string Page, string? OwnProjectId, IReadOnlyList<PluginSettingContribution> Settings);
}
