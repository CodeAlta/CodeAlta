using CodeAlta.Agent;
using CodeAlta.Plugins;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("configuration", Version = 1)]
internal sealed class ConfigurationService(
    ModelProviderRegistry? providerRegistry = null,
    PluginRuntimeManager? pluginRuntime = null)
{
    [NeoRpcMethod("snapshot")]
    public ConfigurationSnapshot Snapshot(ConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var providerDescriptors = providerRegistry?.ListProviders(includeDisabled: true) ?? [];
        var pluginInstances = pluginRuntime?.ActivePlugins ?? [];
        var providers = providerDescriptors
            .Take(32)
            .Select(static value => new ConfigurationProvider(
                Bound(value.ProviderId.Value),
                Bound(value.DisplayName),
                Bound(value.ProviderType),
                value.IsEnabled,
                value.IsDefault,
                BoundOptional(value.DefaultModelId),
                BoundOptional(value.DefaultReasoningEffort?.ToString())))
            .ToArray();
        var plugins = pluginInstances
            .Take(32)
            .Select(static value => new ConfigurationPlugin(
                Bound(value.Descriptor.RuntimeKey),
                Bound(value.Descriptor.DisplayName ?? value.Descriptor.TypeName),
                BoundOptional(value.Descriptor.Version),
                Bound(value.State.ToString()),
                value.Contributions.Count))
            .ToArray();
        return new ConfigurationSnapshot(providers, plugins, providerRegistry is not null, pluginRuntime is not null,
            providerDescriptors.Count > providers.Length, pluginInstances.Count > plugins.Length);
    }

    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
    private static string? BoundOptional(string? value) => value is null ? null : Bound(value);
}

internal sealed record ConfigurationRequest;
internal sealed record ConfigurationSnapshot(
    IReadOnlyList<ConfigurationProvider> Providers,
    IReadOnlyList<ConfigurationPlugin> Plugins,
    bool ProviderRuntimeAvailable,
    bool PluginRuntimeAvailable,
    bool ProvidersTruncated,
    bool PluginsTruncated);
internal sealed record ConfigurationProvider(
    string Id,
    string Name,
    string Type,
    bool Enabled,
    bool IsDefault,
    string? DefaultModel,
    string? DefaultReasoning);
internal sealed record ConfigurationPlugin(
    string Id,
    string Name,
    string? Version,
    string State,
    int ContributionCount);
