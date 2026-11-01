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
        if (Resolve(setting) is not { } found) return new(new AltaSettingRead(AltaSettingStatus.Unavailable, null, null, $"The plugin of {setting.Id} is not running."));
        var target = ProjectOf(found.Plugin, found.Setting, projectId);
        if (target.Refused is { } refused) return new(new AltaSettingRead(AltaSettingStatus.Unavailable, null, null, refused));
        var project = target.ProjectId;
        var (value, written) = _store.Read(found.Plugin.Id, found.Setting, project);
        return new(new AltaSettingRead(AltaSettingStatus.Ok, value, written ? project is null ? "user" : "project" : "default", null));
    }

    /// <inheritdoc />
    public ValueTask<AltaSettingWrite> SetAsync(AltaSettingDescriptor setting, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(origin);
        if (Resolve(setting) is not { } found) return new(new AltaSettingWrite(AltaSettingStatus.Unavailable, null, null, $"The plugin of {setting.Id} is not running."));
        var target = ProjectOf(found.Plugin, found.Setting, projectId);
        if (target.Refused is { } refused) return new(new AltaSettingWrite(AltaSettingStatus.Unavailable, null, null, refused));
        var project = target.ProjectId;
        try
        {
            var previous = _store.Read(found.Plugin.Id, found.Setting, project).Value;
            _store.Write(found.Plugin.Id, found.Setting, value, project);
            var now = _store.Read(found.Plugin.Id, found.Setting, project).Value;
            // The plugin is told off this thread, admitted by its activation: the change does not wait for it.
            _ = _store.NotifyChangedAsync(found.Plugin.Instance, found.Setting.Key, project, origin.Kind)
                .ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new(new AltaSettingWrite(AltaSettingStatus.Ok, now, previous, null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new(new AltaSettingWrite(AltaSettingStatus.Failed, null, null, $"{setting.Id} could not be written: {exception.Message}"));
        }
    }

    // A plugin of a project keeps its settings in that project, and no other; a setting of each project of a plugin of the user, in the one named.
    private static (string? ProjectId, string? Refused) ProjectOf(PluginSettings plugin, PluginSettingContribution setting, string? projectId)
    {
        if (plugin.OwnProjectId is { } own)
        {
            return projectId is null || string.Equals(projectId, own, StringComparison.Ordinal)
                ? (own, null)
                : (null, $"plugins.{plugin.Id}.{setting.Key} belongs to the plugin of another project: it has no value for this one.");
        }

        if (setting.Scope == PluginSettingScope.User) return (null, null);
        return projectId is null ? (null, $"plugins.{plugin.Id}.{setting.Key} is kept by each project: name the project with --project.") : (projectId, null);
    }

    // The plugin of a setting: the one of its project for a plugin of a project, which two projects may both have.
    private (PluginSettings Plugin, PluginSettingContribution Setting)? Resolve(AltaSettingDescriptor setting)
    {
        var id = setting.Id;
        foreach (var plugin in Plugins())
        {
            var prefix = $"plugins.{plugin.Id}.";
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || !string.Equals(plugin.OwnProjectId, setting.Project, StringComparison.Ordinal)) continue;
            var key = id[prefix.Length..];
            if (plugin.Settings.FirstOrDefault(setting => setting.Key == key) is { } found) return (plugin, found);
        }

        return null;
    }

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
            var ownProject = plugin.RuntimeContext.Scope == PluginScope.Project ? plugin.RuntimeContext.ScopeProjectId ?? "" : null;
            // The runtime keeps the settings of a plugin of a project for that project; checked again where they are written.
            if (ownProject is not null) settings = [.. settings.Select(static setting => setting.Scope == PluginSettingScope.Project ? setting : setting with { Scope = PluginSettingScope.Project })];
            yield return new PluginSettings(plugin, runtimeKey, id, plugin.Descriptor.DisplayName ?? id, page, ownProject, settings);
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
            Project = plugin.OwnProjectId,
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

    private sealed record PluginSettings(ActivePluginInstance Instance, string RuntimeKey, string Id, string Name, string Page, string? OwnProjectId,
        IReadOnlyList<PluginSettingContribution> Settings);
}
