using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("configuration", Version = 1)]
internal sealed class ConfigurationService(
    ModelProviderRegistry? providerRegistry = null,
    PluginRuntimeManager? pluginRuntime = null)
{
    private readonly string? _catalogRoot;

    internal ConfigurationService(string catalogRoot) : this()
    {
        if (string.IsNullOrWhiteSpace(catalogRoot) || !Path.IsPathFullyQualified(catalogRoot))
            throw new ArgumentException("An absolute catalog root is required.", nameof(catalogRoot));
        _catalogRoot = Path.GetFullPath(catalogRoot);
    }

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
        if (providerRegistry is null && _catalogRoot is not null)
        {
            try
            {
                var document = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = _catalogRoot }).LoadGlobal();
                var configuredProviders = document.Providers ?? [];
                providers = configuredProviders.Take(32).Select(value => new ConfigurationProvider(
                    Bound(value.Key), Bound(value.Value.DisplayName ?? value.Key), Bound(value.Value.ProviderType ?? value.Key),
                    value.Value.Enabled ?? CodeAltaProviderDocument.DefaultEnabled,
                    string.Equals(document.Chat?.DefaultProvider, value.Key, StringComparison.OrdinalIgnoreCase),
                    BoundOptional(value.Value.Model), BoundOptional(value.Value.ReasoningEffort))).ToArray();
                var configuredPlugins = document.Plugins ?? [];
                plugins = configuredPlugins.Take(32).Select(value => new ConfigurationPlugin(
                    Bound(value.Key), Bound(value.Key), null, value.Value.Enabled == false ? "Disabled" : "Configured", 0)).ToArray();
                return new ConfigurationSnapshot(providers, plugins, false, false,
                    configuredProviders.Count > providers.Length, configuredPlugins.Count > plugins.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new ConfigurationSnapshot([], [], false, false, false, false);
            }
        }
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
